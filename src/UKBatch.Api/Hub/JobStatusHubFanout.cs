using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;

namespace UKBatch.Api.Hub;

/// <summary>
/// SignalR fan-out pump. Subscribes to the runtime change feed (<see cref="IJobExecutionReader.WatchAsync"/>),
/// new-approval-gate channel (<see cref="IApprovalGateEvents"/>), and progress-beat channel
/// (<see cref="IProgressBeatBroadcaster"/>), then pushes events to the relevant hub groups.
/// </summary>
/// <remarks>
/// <para><b>Batch completion:</b> a
/// dedicated batch-completion pump subscribes to <see cref="IBatchCompletionEvents"/>. The
/// runtime writes the batch RUN id to that channel after the batch executor's RunAsync returns
/// (success / failure / cancellation). The hub queries the store ONCE per signal to build the
/// aggregate summary and emits <see cref="BatchCompletionSummary"/> EXACTLY ONCE per batch run
/// (guarded by the <see cref="_completedBatches"/> dedupe set). This is driven by the runtime —
/// the runtime knows when the batch is genuinely complete; the watch event stream cannot, because
/// sequential step dispatch creates a race where late steps haven't yet been inserted into the
/// store at the moment earlier steps emit terminal events.</para>
/// <para><b>v0.1 explicitly excludes <c>HubBackpressureWarning</c></b> (deferred).</para>
/// <para><b>Friend-access discipline:</b> consumes
/// <c>BatchStateMachine.IsTerminal</c>, <see cref="IApprovalGateEvents"/>,
/// <see cref="IProgressBeatBroadcaster"/>, and <see cref="IBatchCompletionEvents"/> from Core
/// internals. All four follow the same "internal interface in Core, friend-accessible to Api"
/// pattern.</para>
/// </remarks>
internal sealed class JobStatusHubFanout : IHostedService, IAsyncDisposable
{
    private readonly IJobExecutionReader _reader;
    private readonly IApprovalGateEvents _approvalEvents;
    private readonly IProgressBeatBroadcaster _progressBroadcaster;
    private readonly IBatchCompletionEvents _batchCompletionEvents;
    private readonly IHubContext<JobStatusHub, IJobStatusHubClient> _hub;
    private readonly UKBatchOptions _options;
    private readonly ILogger<JobStatusHubFanout> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _watchPumpTask;
    private Task? _approvalPumpTask;
    private Task? _progressPumpTask;
    private Task? _batchCompletionPumpTask;

    // Bounded LRU dedupe of batch ids whose summary has already been emitted. Capacity 10_000 keys
    // × ~64 bytes ≈ 640 KB upper bound — survives hub stress (many concurrent clients) without
    // unbounded growth. Defense-in-depth: Signal -> channel writer drop-oldest may surface
    // duplicates; the LRU dedupe protects against re-emitting.
    private const int CompletedBatchesCapacity = 10_000;
    private readonly LruDedupeCache<string> _completedBatches = new(CompletedBatchesCapacity, StringComparer.Ordinal);

    /// <summary>Constructs the fan-out pump.</summary>
    public JobStatusHubFanout(
        IJobExecutionReader reader,
        IApprovalGateEvents approvalEvents,
        IProgressBeatBroadcaster progressBroadcaster,
        IBatchCompletionEvents batchCompletionEvents,
        IHubContext<JobStatusHub, IJobStatusHubClient> hub,
        IOptions<UKBatchOptions> options,
        ILogger<JobStatusHubFanout> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(approvalEvents);
        ArgumentNullException.ThrowIfNull(progressBroadcaster);
        ArgumentNullException.ThrowIfNull(batchCompletionEvents);
        ArgumentNullException.ThrowIfNull(hub);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _reader = reader;
        _approvalEvents = approvalEvents;
        _progressBroadcaster = progressBroadcaster;
        _batchCompletionEvents = batchCompletionEvents;
        _hub = hub;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Defense-in-depth: early-return if already started.
        // The primary guard is in AddUKBatchApi; this protects against accidental double-Start.
        if (_stoppingCts is not null)
        {
            return Task.CompletedTask;
        }
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stoppingCts.Token;
        _watchPumpTask = Task.Run(() => WatchPumpAsync(token), CancellationToken.None);
        _approvalPumpTask = Task.Run(() => ApprovalPumpAsync(token), CancellationToken.None);
        _progressPumpTask = Task.Run(() => ProgressPumpAsync(token), CancellationToken.None);
        _batchCompletionPumpTask = Task.Run(() => BatchCompletionPumpAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task WatchPumpAsync(CancellationToken ct)
    {
        var watch = new WatchOptions { BufferCapacity = _options.HubBufferCapacity };
        try
        {
            await foreach (var exec in _reader.WatchAsync(watch, ct).ConfigureAwait(false))
            {
                try
                {
                    await FanOutExecutionAsync(exec, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hub fan-out for execution {Id} failed (continuing).", exec.ExecutionId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobStatusHubFanout WatchAsync pump terminated unexpectedly.");
        }
    }

    private async Task FanOutExecutionAsync(JobExecution exec, CancellationToken ct)
    {
        _ = ct;
        await _hub.Clients.Group($"exec:{exec.ExecutionId}").ExecutionStateChanged(exec).ConfigureAwait(false);
        if (exec.BatchId is { } bid)
        {
            await _hub.Clients.Group($"batch:{bid}").ExecutionStateChanged(exec).ConfigureAwait(false);
        }
        await _hub.Clients.Group($"job:{exec.JobName}").ExecutionStateChanged(exec).ConfigureAwait(false);
        await _hub.Clients.Group("all").ExecutionStateChanged(exec).ConfigureAwait(false);
        // BatchCompleted is NOT triggered here. The runtime
        // signals batch completion via IBatchCompletionEvents (see BatchCompletionPumpAsync).
    }

    private async Task BatchCompletionPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var payload in _batchCompletionEvents.CompletedBatchRunIds.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await EmitBatchCompletionAsync(payload, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hub fan-out: BatchCompleted emission for batch {BatchId} failed.", payload.BatchRunId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobStatusHubFanout batch-completion pump terminated unexpectedly.");
        }
    }

    private async Task EmitBatchCompletionAsync(BatchCompletionSignalPayload payload, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(payload);

        // Single-shot TryAdd dedupes; the pump is single-reader (no race window). If QueryAsync
        // below throws, the dedupe key REMAINS in the cache — we intentionally do NOT retry this
        // batch (the channel is DropOldest; retries belong to the upstream signal layer, not here).
        if (!_completedBatches.TryAdd(payload.BatchRunId))
        {
            return;  // dedupe HIT — already emitted
        }

        // Query ALL executions in the batch run. By the time this signal arrives, BatchExecutor
        // has finished walking every step — so every child execution is in the store.
        IReadOnlyList<JobExecution> executions;
        try
        {
            executions = await _reader.QueryAsync(
                new JobQuery { BatchId = payload.BatchRunId, Limit = int.MaxValue, Offset = 0 }, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Hub fan-out: failed to query batch {BatchId} for completion check.", payload.BatchRunId);
            return;
        }

        var succeeded = executions.Count(e => e.Status == JobStatus.Completed);
        var failed = executions.Count(e => e.Status == JobStatus.Failed);
        var cancelled = executions.Count(e => e.Status == JobStatus.Cancelled);
        var aggregate = cancelled > 0 ? JobStatus.Cancelled
                      : failed > 0 ? JobStatus.Failed : JobStatus.Completed;

        var lastTerminal = executions.Count > 0
            ? executions.Max(e => e.CompletedAtUtc ?? DateTimeOffset.UtcNow)
            : DateTimeOffset.UtcNow;
        var summary = new BatchCompletionSummary
        {
            BatchId = payload.BatchRunId,
            BatchDefinitionId = payload.BatchDefinitionId,
            // BatchDefinition.Name is `required string` (compile-time non-null) but runtime "" is
            // still possible if a caller constructs `new BatchDefinition { Name = "" }`. Defensive
            // runtime fallback so the dashboard never renders a literally empty heading.
            BatchName = string.IsNullOrEmpty(payload.BatchName) ? "<unnamed>" : payload.BatchName,
            FinalStatus = aggregate,
            TotalJobs = executions.Count,
            SucceededJobs = succeeded,
            FailedJobs = failed,
            CancelledJobs = cancelled,
            CompletedAtUtc = lastTerminal,
        };
        await _hub.Clients.Group($"batch:{payload.BatchRunId}").BatchCompleted(summary).ConfigureAwait(false);
        await _hub.Clients.Group("all").BatchCompleted(summary).ConfigureAwait(false);
    }

    private async Task ApprovalPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var pending in _approvalEvents.NewGates.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _hub.Clients.Group($"batch:{pending.BatchId}").ApprovalRequested(pending).ConfigureAwait(false);
                    await _hub.Clients.Group("all").ApprovalRequested(pending).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hub fan-out for approval {Id} failed (continuing).", pending.ApprovalId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobStatusHubFanout approval pump terminated unexpectedly.");
        }
    }

    private async Task ProgressPumpAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var beat in _progressBroadcaster.Beats.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    await _hub.Clients.Group($"exec:{beat.ExecutionId}").ProgressUpdated(beat).ConfigureAwait(false);
                    await _hub.Clients.Group("all").ProgressUpdated(beat).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Hub fan-out for progress {Id} failed (continuing).", beat.ExecutionId);
                }
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobStatusHubFanout progress pump terminated unexpectedly.");
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts?.Cancel();
        var tasks = new List<Task>();
        if (_watchPumpTask is { } w) tasks.Add(w);
        if (_approvalPumpTask is { } a) tasks.Add(a);
        if (_progressPumpTask is { } p) tasks.Add(p);
        if (_batchCompletionPumpTask is { } bc) tasks.Add(bc);
        try
        {
            await Task.WhenAll(tasks).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best-effort drain
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _stoppingCts, null);
        cts?.Cancel();
        var tasks = new List<Task>();
        if (_watchPumpTask is { } w) tasks.Add(w);
        if (_approvalPumpTask is { } a) tasks.Add(a);
        if (_progressPumpTask is { } p) tasks.Add(p);
        if (_batchCompletionPumpTask is { } bc) tasks.Add(bc);
        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
        cts?.Dispose();
    }

}
