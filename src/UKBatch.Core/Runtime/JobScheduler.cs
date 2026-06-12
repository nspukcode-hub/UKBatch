using System.Threading.Channels;
using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Registry;

namespace UKBatch.Runtime;

/// <summary>
/// Cron scheduler.
/// <list type="bullet">
///   <item>Min-heap of <see cref="ScheduledJobEntry"/> sorted by next fire deadline.</item>
///   <item>Wake signal is a <see cref="Channel{T}"/> bounded to 1 with <see cref="BoundedChannelFullMode.DropOldest"/>
///         so registry-change notifications collapse safely under contention.</item>
///   <item>Loop awaits <see cref="Task.WhenAny(System.Threading.Tasks.Task[])"/> over the wake channel and
///         <see cref="Task.Delay(TimeSpan, CancellationToken)"/> until the next deadline.</item>
/// </list>
/// </summary>
/// <remarks>
/// Missed-fires-on-downtime are by-design in v0.1. For SLA-bound jobs,
/// adopt the durable scheduler planned for v0.2.
/// </remarks>
internal sealed class JobScheduler : IDisposable
{
    private readonly PriorityQueue<ScheduledJobEntry, DateTimeOffset> _heap = new();
#if NET10_0_OR_GREATER
    private readonly Lock _heapLock = new();
#else
    // System.Threading.Lock requires net9+; a plain monitor object is the net8.0 equivalent.
    private readonly object _heapLock = new();
#endif
    private readonly Channel<bool> _wakeChannel;
    private readonly JobDefinitionRegistry _registry;
    private readonly JobDispatcher _dispatcher;
    private readonly Abstractions.Storage.IJobStore _store;
    private readonly CronExpressionCache _cronCache;
    private readonly TimeProvider _clock;
    private readonly UKBatchOptions _options;
    private readonly ILogger<JobScheduler> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;
    private int _started;
    private int _disposed;

    /// <summary>Constructs the scheduler with composed dependencies.</summary>
    public JobScheduler(
        JobDefinitionRegistry registry,
        JobDispatcher dispatcher,
        Abstractions.Storage.IJobStore store,
        CronExpressionCache cronCache,
        TimeProvider clock,
        IOptions<UKBatchOptions> options,
        ILogger<JobScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(cronCache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _dispatcher = dispatcher;
        _store = store;
        _cronCache = cronCache;
        _clock = clock;
        _options = options.Value;
        _logger = logger;
        _wakeChannel = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Loads the registry snapshot and launches the loop.</summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // One-shot, atomically: a second start would re-arm every definition into the same heap
        // and launch a second loop over it (each occurrence then fires twice), while overwriting
        // the first loop's cancellation source — leaving that loop unstoppable. The scheduler is
        // not restartable after StopAsync either; the owning host's lifetime is one-shot.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogWarning("JobScheduler.StartAsync called more than once; ignoring the duplicate start.");
            return Task.CompletedTask;
        }
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nowUtc = _clock.GetUtcNow();
        lock (_heapLock)
        {
            foreach (var def in _registry.All())
            {
                if (string.IsNullOrEmpty(def.Schedule))
                {
                    continue;
                }
                CronExpression expr;
                try
                {
                    expr = _cronCache.Get(def.Schedule, _options.CronFormat);
                }
                catch (Exception ex)
                {
                    // Defense-in-depth: a malformed cron must NOT take down host startup. Registration-time
                    // validation should already have rejected this; if a definition still reaches here with a
                    // bad expression, log it and skip ONLY that job — every other scheduled job still arms.
                    _logger.LogError(ex,
                        "Skipping scheduled job '{Job}': invalid cron expression '{Schedule}' for CronFormat={Format}.",
                        def.Name, def.Schedule, _options.CronFormat);
                    continue;
                }
                var next = expr.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
                if (next is null)
                {
                    continue;
                }
                var entry = new ScheduledJobEntry
                {
                    Definition = def,
                    CronExpression = expr,
                    NextFireUtc = new DateTimeOffset(next.Value, TimeSpan.Zero),
                };
                _heap.Enqueue(entry, entry.NextFireUtc);
            }
        }
        _loopTask = Task.Run(() => LoopAsync(_stoppingCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            DateTimeOffset? nextDeadline;
            lock (_heapLock)
            {
                nextDeadline = _heap.TryPeek(out _, out var when) ? when : null;
            }

            if (nextDeadline is null)
            {
                try
                {
                    await _wakeChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // The stopping source was disposed while this loop was still running
                    // (container teardown racing an abandoned shutdown wait) — same as cancel.
                    return;
                }
                while (_wakeChannel.Reader.TryRead(out _))
                {
                    // drain backlog
                }
                continue;
            }

            var remaining = nextDeadline.Value - _clock.GetUtcNow();
            if (remaining > TimeSpan.Zero)
            {
                try
                {
                    using var deadlineCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    var wakeTask = _wakeChannel.Reader.WaitToReadAsync(deadlineCts.Token).AsTask();
                    var delayTask = Task.Delay(remaining, deadlineCts.Token);
                    var winner = await Task.WhenAny(wakeTask, delayTask).ConfigureAwait(false);
                    deadlineCts.Cancel();
                    try
                    {
                        await winner.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // expected when the loser was cancelled
                    }
                    if (winner == wakeTask)
                    {
                        while (_wakeChannel.Reader.TryRead(out _))
                        {
                            // drain
                        }
                        continue;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    // Linked-source creation raced the stopping source's disposal — same as cancel.
                    return;
                }
            }

            // The wait above elapses on the timer's own clock while the deadline lives on the
            // injected wall clock; timer-resolution rounding or a wall-clock adjustment (NTP
            // slew) can complete the delay marginally BEFORE the deadline. Firing early would
            // also re-arm the very same occurrence (the next-occurrence anchor below would still
            // sit before it), producing a duplicate run one tick later. Not yet due: go around
            // and re-measure the remaining time.
            if (_clock.GetUtcNow() < nextDeadline.Value)
            {
                continue;
            }

            ScheduledJobEntry? entry;
            lock (_heapLock)
            {
                _heap.TryDequeue(out entry, out _);
            }
            if (entry is null)
            {
                continue;
            }
            await FireAsync(entry, ct).ConfigureAwait(false);

            // Anchor the next occurrence on the LATER of (the occurrence just fired, now):
            // anchoring no earlier than the fired occurrence means even a backward wall-clock
            // step can never re-arm the same occurrence (a duplicate run), while anchoring on
            // `now` when the fire ran long keeps the skip semantics — occurrences missed
            // meanwhile are dropped rather than replayed in a burst.
            var nowUtc = _clock.GetUtcNow();
            var anchor = nowUtc > entry.NextFireUtc ? nowUtc : entry.NextFireUtc;
            var nextOccurrence = entry.CronExpression.GetNextOccurrence(anchor.UtcDateTime, TimeZoneInfo.Utc);
            if (nextOccurrence is not null)
            {
                var rescheduled = entry with { NextFireUtc = new DateTimeOffset(nextOccurrence.Value, TimeSpan.Zero) };
                lock (_heapLock)
                {
                    _heap.Enqueue(rescheduled, rescheduled.NextFireUtc);
                }
            }
        }
    }

    private async Task FireAsync(ScheduledJobEntry entry, CancellationToken ct)
    {
        JobExecution? execution = null;
        try
        {
            execution = await _store.CreateAsync(entry.Definition, ct).ConfigureAwait(false);
            var request = new JobExecutionRequest
            {
                ExecutionId = execution.ExecutionId,
                Definition = entry.Definition,
                // Trusted callsite: DefaultParameters is defensive-copied at registration, so
                // WrapWithoutCopy is safe here.
                Parameters = JobParameters.WrapWithoutCopy(entry.Definition.DefaultParameters),
                AttemptNumber = 1,
                TriggeredBy = _options.SchedulerTriggerIdentity,
                BatchId = null,
                BatchStepId = null,
                EnqueuedAtUtc = _clock.GetUtcNow(),
            };
            await _dispatcher.EnqueueAsync(request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown between row creation and enqueue: without compensation the row would sit
            // in Pending forever on a persistent store (a live-process orphan only the restart
            // reaper would eventually clean). Pending -> Failed is a legal transition.
            await TryFailOrphanedExecutionAsync(execution,
                "Scheduler fire was interrupted by host shutdown before the job could be enqueued.").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Scheduler fire failed for {Job}", entry.Definition.Name);
            await TryFailOrphanedExecutionAsync(execution,
                $"Scheduler fire failed before the job could be enqueued: {ex.Message}").ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Best-effort compensation for a created-but-never-enqueued execution row. Uses
    /// <see cref="CancellationToken.None"/> because the fire token is typically already
    /// cancelled here, and swallows failures: at shutdown the store itself may be gone, and the
    /// restart reaper remains the durable backstop for persistent stores.
    /// </summary>
    private async Task TryFailOrphanedExecutionAsync(JobExecution? execution, string reason)
    {
        if (execution is null)
        {
            return;
        }
        try
        {
            await _store.UpdateStatusAsync(execution.ExecutionId, JobStatus.Failed, reason, CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception updateEx)
        {
            _logger.LogWarning(updateEx,
                "Could not mark orphaned scheduled execution {ExecutionId} as Failed; the restart sweep will reap it.",
                execution.ExecutionId);
        }
    }

    /// <summary>Signals the loop to exit. Idempotent.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Teardown order is not guaranteed: with WebApplication the provider can be disposed
        // BEFORE the host calls StopAsync, so Dispose may already have run (it nulls the field).
        // Snapshot + ObjectDisposedException-guard make a post-dispose stop a no-op.
        var cts = _stoppingCts;
        if (cts is null)
        {
            return;
        }
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            return;
        }
        if (_loopTask is not { } loop)
        {
            return;
        }
        // Bounded wait, mirroring the worker drain: the loop normally exits within one await of
        // the cancel, but a wedged in-flight fire (e.g. a store write against a dying database)
        // must not be able to hold host shutdown hostage past its grace period.
        try
        {
            await loop.WaitAsync(_options.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning(
                "Scheduler loop did not stop within {Timeout}; abandoning the wait (process shutdown continues).",
                _options.ShutdownTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // host cancelled the StopAsync grace period — continue cleanup. The filter keeps any
            // OTHER cancellation (a genuinely faulted loop task) loud instead of swallowed.
        }
    }

    /// <summary>
    /// Hook for future dynamic registry add/remove. Multiple notifications collapse to one wake
    /// thanks to <c>Channel&lt;bool&gt;(1, DropOldest)</c>.
    /// </summary>
    public void NotifyRegistryChanged() => _wakeChannel.Writer.TryWrite(true);

    /// <summary>
    /// Disposes the linked stopping source (releasing its registration on the host's stopping
    /// token). Called by the container at provider teardown; idempotent. Cancel-before-dispose
    /// lets a still-running loop observe cancellation instead of a disposed source, and the
    /// loop additionally treats <see cref="ObjectDisposedException"/> from its waits as cancel.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        try
        {
            _stoppingCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already torn down elsewhere — nothing left to release
        }
        _stoppingCts?.Dispose();
        // Null the field so a StopAsync arriving AFTER container disposal no-ops instead of
        // cancelling a disposed source (WebApplication tears the provider down first).
        _stoppingCts = null;
    }
}
