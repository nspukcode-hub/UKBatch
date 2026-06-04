using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Internal;
using UKBatch.Registry;

namespace UKBatch.Runtime;

/// <summary>
/// Worker task that drains <see cref="JobDispatcher"/>'s channel and executes one request at a time.
/// Guarantees retry-sequence durability, performs status writes with <see cref="CancellationToken.None"/>,
/// and handles three predecessor states in step 1 (<c>Pending</c>, <c>Retrying</c>, and
/// <c>Cancelling</c> — the last for user-cancel-before-dispatch races).
/// </summary>
internal sealed class JobWorker
{
    private readonly IServiceProvider _rootProvider;
    private readonly JobDispatcher _dispatcher;
    private readonly IJobExecutionWriter _writer;
    private readonly IJobExecutionReader _reader;
    private readonly JobDefinitionRegistry _registry;
    private readonly DebouncedProgressFlusher _flusher;
    private readonly IRetryPolicy _retryPolicy;
    private readonly TimeProvider _clock;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<JobWorker> _logger;
    private readonly UKBatchOptions _options;

    /// <summary>Constructs the worker.</summary>
    public JobWorker(
        IServiceProvider rootProvider,
        JobDispatcher dispatcher,
        IJobExecutionWriter writer,
        IJobExecutionReader reader,
        JobDefinitionRegistry registry,
        DebouncedProgressFlusher flusher,
        IRetryPolicy retryPolicy,
        TimeProvider clock,
        ILoggerFactory loggerFactory,
        IOptions<UKBatchOptions> options)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(flusher);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);
        _rootProvider = rootProvider;
        _dispatcher = dispatcher;
        _writer = writer;
        _reader = reader;
        _registry = registry;
        _flusher = flusher;
        _retryPolicy = retryPolicy;
        _clock = clock;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<JobWorker>();
        _options = options.Value;
    }

    /// <summary>Drains the dispatcher channel until <paramref name="stoppingToken"/> signals shutdown.</summary>
    public async Task RunAsync(int workerIndex, CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await using var scope = _rootProvider.CreateAsyncScope();
                try
                {
                    await ExecuteOneAsync(request, scope.ServiceProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    // shutdown
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "JobWorker[{Worker}] unexpected loop error for execution {Id}", workerIndex, request.ExecutionId);
                    await TryWriteTerminalFailureAsync(request.ExecutionId, ex).ConfigureAwait(false);
                }
                finally
                {
                    _flusher.ReleaseExecution(request.ExecutionId);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // shutdown
        }
    }

    private async Task ExecuteOneAsync(JobExecutionRequest req, IServiceProvider scopedProvider, CancellationToken stoppingToken)
    {
        // Step 1: read current row status. Legal predecessors are Pending (first attempt),
        // Retrying (re-enqueued retry), or Cancelling (the caller cancelled AFTER the dispatch
        // enqueue but BEFORE the worker pulled it — finalise Cancelled and skip execution).
        // Any other state is a programmer error.
        var current = await _reader.GetAsync(req.ExecutionId, stoppingToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"execution {req.ExecutionId} missing at dispatch");
        if (current.Status is JobStatus.Pending or JobStatus.Retrying)
        {
            await _writer.UpdateStatusAsync(req.ExecutionId, JobStatus.Running, errorMessage: null, CancellationToken.None).ConfigureAwait(false);
        }
        else if (current.Status is JobStatus.Cancelling)
        {
            // User cancelled before dispatch — transition straight to Cancelled and skip execution.
            // The `return` exits cleanly so the outer catch (Exception) path never fires for this
            // branch (which would have tried Cancelling -> Failed, an illegal transition).
            await _writer.UpdateStatusAsync(req.ExecutionId, JobStatus.Cancelled, "cancelled before dispatch", CancellationToken.None).ConfigureAwait(false);
            return;
        }
        else
        {
            throw new InvalidJobTransitionException(current.Status, JobStatus.Running);
        }

        // Step 2: build per-execution progress + logger.
        var progress = new CountingJobProgress(req.ExecutionId, _flusher);
        var jobLogger = JobLoggerFactory.CreateLogger(_loggerFactory, req.Definition.Name);
        using var scopeDisposable = JobLoggerFactory.BeginExecutionScope(
            jobLogger,
            req.ExecutionId,
            req.AttemptNumber,
            req.BatchId,
            req.BatchStepId);

        // Step 3 + 4: build context and linked CTS for per-execution timeout.
        var startedAt = _clock.GetUtcNow();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        if (req.Definition.TimeoutSeconds > 0)
        {
            linkedCts.CancelAfter(TimeSpan.FromSeconds(req.Definition.TimeoutSeconds));
        }

        var ctx = new JobContext
        {
            ExecutionId = req.ExecutionId,
            BatchId = req.BatchId,
            BatchStepId = req.BatchStepId,
            JobName = req.Definition.Name,
            Parameters = req.Parameters,
            Services = scopedProvider,
            Logger = jobLogger,
            Progress = progress,
            ParallelExecutor = new ParallelExecutor(jobLogger),
            AttemptNumber = req.AttemptNumber,
            StartedAtUtc = startedAt,
            TriggeredBy = req.TriggeredBy,
        };

        try
        {
            await DispatchToImplementationAsync(req, ctx, scopedProvider, linkedCts.Token).ConfigureAwait(false);

            // Step 7: SUCCESS.
            await _writer.UpdateProgressAsync(
                req.ExecutionId,
                progress.Processed,
                progress.Failed,
                progress.Total,
                CancellationToken.None).ConfigureAwait(false);
            await _writer.UpdateStatusAsync(
                req.ExecutionId,
                JobStatus.Completed,
                errorMessage: null,
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Step 8: host-cancelled — row goes Cancelling -> Cancelled with CT.None.
            await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelling, "host shutdown").ConfigureAwait(false);
            await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelled, "host shutdown").ConfigureAwait(false);
            await _writer.UpdateProgressAsync(req.ExecutionId, progress.Processed, progress.Failed, progress.Total, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (OperationCanceledException oce)
        {
            // Step 8b: user-cancel / per-execution timeout.
            await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelling, oce.Message).ConfigureAwait(false);
            await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelled, oce.Message).ConfigureAwait(false);
            await _writer.UpdateProgressAsync(req.ExecutionId, progress.Processed, progress.Failed, progress.Total, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Step 9: retry decision.
            var decision = _retryPolicy.Decide(req.AttemptNumber, req.Definition.MaxRetries, ex);
            if (decision.ShouldRetry)
            {
                // (a) Commit Retrying durably BEFORE the wait, so a crash during the wait
                //     re-enqueues rather than loses the attempt.
                await _writer.UpdateStatusAsync(req.ExecutionId, JobStatus.Retrying, errorMessage: ex.ToString(), CancellationToken.None).ConfigureAwait(false);
                // (b) Commit next attempt number.
                await _writer.RecordAttemptAsync(req.ExecutionId, req.AttemptNumber + 1, CancellationToken.None).ConfigureAwait(false);
                try
                {
                    if (decision.Delay > TimeSpan.Zero)
                    {
                        await Task.Delay(decision.Delay, stoppingToken).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelling, "host shutdown during retry-wait").ConfigureAwait(false);
                    await SafeTransitionAsync(req.ExecutionId, JobStatus.Cancelled, "host shutdown during retry-wait").ConfigureAwait(false);
                    await _writer.UpdateProgressAsync(req.ExecutionId, progress.Processed, progress.Failed, progress.Total, CancellationToken.None).ConfigureAwait(false);
                    return;
                }
                await _dispatcher.EnqueueAsync(
                    req with { AttemptNumber = req.AttemptNumber + 1, EnqueuedAtUtc = _clock.GetUtcNow() },
                    stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await _writer.UpdateStatusAsync(req.ExecutionId, JobStatus.Failed, errorMessage: ex.ToString(), CancellationToken.None).ConfigureAwait(false);
                await _writer.UpdateProgressAsync(req.ExecutionId, progress.Processed, progress.Failed, progress.Total, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchToImplementationAsync(
        JobExecutionRequest req,
        JobContext ctx,
        IServiceProvider scoped,
        CancellationToken executionToken)
    {
        var implType = _registry.TryGetImplementationType(req.Definition.Name);
        if (implType is null)
        {
            throw new InvalidOperationException($"Implementation type not registered for job {req.Definition.Name}.");
        }

        if (req.Definition.IsPartitioned)
        {
            var jobInstance = scoped.GetRequiredService(implType);
            var partitionInterface = implType.GetInterfaces()
                .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPartitionedJob<>))
                ?? throw new InvalidOperationException($"Job {req.Definition.Name} marked partitioned but does not implement IPartitionedJob<T>.");
            var itemType = partitionInterface.GetGenericArguments()[0];
            await PartitionedJobDispatcher.DispatchAsync(
                jobInstance,
                itemType,
                ctx,
                req.Definition,
                _registry,
                ctx.Logger,
                executionToken).ConfigureAwait(false);
        }
        else
        {
            var jobInstance = (IJob)scoped.GetRequiredService(implType);
            await jobInstance.ExecuteAsync(ctx, executionToken).ConfigureAwait(false);
        }
    }

    private async Task SafeTransitionAsync(string executionId, JobStatus target, string? message)
    {
        try
        {
            await _writer.UpdateStatusAsync(executionId, target, message, CancellationToken.None).ConfigureAwait(false);
        }
        catch (InvalidJobTransitionException) when (target is JobStatus.Cancelling or JobStatus.Cancelled)
        {
            // Already in a downstream state (e.g. previously-cancelled while in Retrying).
            _logger.LogDebug("Skipped illegal transition to {Target} on {ExecutionId} (already past it).", target, executionId);
        }
    }

    private async Task TryWriteTerminalFailureAsync(string executionId, Exception ex)
    {
        try
        {
            await _writer.UpdateStatusAsync(executionId, JobStatus.Failed, ex.ToString(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception innerEx)
        {
            _logger.LogError(innerEx, "Failed to mark execution {Id} as Failed after worker loop error.", executionId);
        }
    }
}
