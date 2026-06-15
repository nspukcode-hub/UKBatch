using System.Threading.Channels;
using Cronos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Runtime;

/// <summary>
/// Cron scheduler for whole batch runs (the sibling of <see cref="JobScheduler"/>, which schedules single
/// jobs). Each due occurrence launches a run through <see cref="IJobRunner.TriggerBatchAsync"/>; that call
/// owns the run's lifecycle (the run record + its execution rows), so there is no pre-created row to
/// compensate on failure.
/// <list type="bullet">
///   <item>Min-heap of <see cref="ScheduledBatchEntry"/> sorted by next fire deadline.</item>
///   <item>Wake signal is a <see cref="Channel{T}"/> bounded to 1 with <see cref="BoundedChannelFullMode.DropOldest"/>
///         so definition-change notifications collapse safely under contention.</item>
///   <item>Loop awaits <see cref="Task.WhenAny(System.Threading.Tasks.Task[])"/> over the wake channel and
///         <see cref="Task.Delay(TimeSpan, CancellationToken)"/> until the next deadline.</item>
/// </list>
/// </summary>
/// <remarks>
/// Scheduled batches come from two sources: code-defined batches (the in-process registry, scanned
/// synchronously) and stored batches (dashboard- and API-defined, scanned through the async store). A
/// definition added or de-scheduled after start is picked up via <see cref="NotifyDefinitionChangedAsync"/>
/// without a restart. Missed-fires-on-downtime are by-design; a durable scheduler is planned for a later
/// release.
/// </remarks>
internal sealed class BatchScheduler : IDisposable, IBatchScheduleNotifier
{
    private readonly PriorityQueue<ScheduledBatchEntry, DateTimeOffset> _heap = new();
#if NET10_0_OR_GREATER
    private readonly Lock _heapLock = new();
#else
    // System.Threading.Lock requires net9+; a plain monitor object is the net8.0 equivalent.
    private readonly object _heapLock = new();
#endif
    private readonly Channel<bool> _wakeChannel;
    private readonly IBatchDefinitionLookup _batchLookup;
    private readonly IBatchDefinitionStore _batchStore;
    private readonly IJobRunner _runner;
    private readonly CronExpressionCache _cronCache;
    private readonly TimeProvider _clock;
    private readonly UKBatchOptions _options;
    private readonly ILogger<BatchScheduler> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;
    private int _started;
    private int _disposed;

    /// <summary>Constructs the scheduler with composed dependencies.</summary>
    public BatchScheduler(
        IBatchDefinitionLookup batchLookup,
        IBatchDefinitionStore batchStore,
        IJobRunner runner,
        CronExpressionCache cronCache,
        TimeProvider clock,
        IOptions<UKBatchOptions> options,
        ILogger<BatchScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(batchLookup);
        ArgumentNullException.ThrowIfNull(batchStore);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(cronCache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _batchLookup = batchLookup;
        _batchStore = batchStore;
        _runner = runner;
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

    /// <summary>Loads the schedule snapshot (code-defined plus stored batches) and launches the loop.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // One-shot, atomically: a second start would re-arm every definition into the same heap
        // and launch a second loop over it (each occurrence then fires twice), while overwriting
        // the first loop's cancellation source — leaving that loop unstoppable. The scheduler is
        // not restartable after StopAsync either; the owning host's lifetime is one-shot.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogWarning("BatchScheduler.StartAsync called more than once; ignoring the duplicate start.");
            return;
        }
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Scan both sources (the store scan is async, so unlike the single-job scheduler the initial
        // heap load happens after an await rather than synchronously inside the lock) and arm the heap.
        var entries = await ComputeScheduledEntriesAsync(cancellationToken).ConfigureAwait(false);
        lock (_heapLock)
        {
            foreach (var entry in entries)
            {
                _heap.Enqueue(entry, entry.NextFireUtc);
            }
        }

        _loopTask = Task.Run(() => LoopAsync(_stoppingCts.Token), CancellationToken.None);
    }

    /// <summary>
    /// Scans the code-defined registry and the stored definitions and parses each scheduled batch's cron
    /// expression into a heap entry. All store I/O and cron parsing happen here, OUTSIDE the heap lock; the
    /// caller takes the lock only to mutate the heap with the returned entries. A malformed cron skips ONLY
    /// that batch (logged), never the whole scan, so one bad definition cannot block scheduling for the rest.
    /// </summary>
    private async Task<List<ScheduledBatchEntry>> ComputeScheduledEntriesAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        var entries = new List<ScheduledBatchEntry>();

        // Code-defined batches (in-process registry, synchronous).
        foreach (var def in _batchLookup.All())
        {
            if (TryBuildEntry(def, nowUtc, out var entry))
            {
                entries.Add(entry);
            }
        }

        // Stored batches (dashboard- and API-defined) — paged scan.
        foreach (var source in new[] { BatchSource.Dashboard, BatchSource.Api })
        {
            const int pageSize = 200;
            var offset = 0;
            while (true)
            {
                var page = await _batchStore.ListAsync(source, offset, pageSize, cancellationToken).ConfigureAwait(false);
                foreach (var def in page)
                {
                    if (TryBuildEntry(def, nowUtc, out var entry))
                    {
                        entries.Add(entry);
                    }
                }
                if (page.Count < pageSize)
                {
                    break;
                }
                offset += pageSize;
            }
        }

        return entries;
    }

    /// <summary>
    /// Builds a heap entry for a scheduled definition, or returns <c>false</c> for an unscheduled definition,
    /// a malformed cron expression, or an expression with no future occurrence. Pure (no heap mutation, no
    /// I/O) so it is safe to call from the rebuild path while holding nothing.
    /// </summary>
    private bool TryBuildEntry(BatchDefinition def, DateTimeOffset nowUtc, out ScheduledBatchEntry entry)
    {
        entry = null!;
        if (string.IsNullOrEmpty(def.Schedule))
        {
            return false;
        }
        CronExpression expr;
        try
        {
            expr = _cronCache.Get(def.Schedule, _options.CronFormat);
        }
        catch (Exception ex)
        {
            // A malformed cron must NOT take down host startup or a rescan. Registration-time validation
            // should already have rejected this; if a definition still reaches here with a bad expression,
            // log it and skip ONLY that batch — every other scheduled batch still arms.
            _logger.LogError(ex,
                "Skipping scheduled batch '{Batch}': invalid cron expression '{Schedule}' for CronFormat={Format}.",
                def.Name, def.Schedule, _options.CronFormat);
            return false;
        }
        var next = expr.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);
        if (next is null)
        {
            return false;
        }
        entry = new ScheduledBatchEntry
        {
            BatchDefinitionId = def.Id,
            BatchName = def.Name,
            CronExpression = expr,
            NextFireUtc = new DateTimeOffset(next.Value, TimeSpan.Zero),
        };
        return true;
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

            ScheduledBatchEntry? entry;
            lock (_heapLock)
            {
                // Re-validate against the ACTUAL heap-min under the lock, not the stale peeked deadline: a
                // rescan during the wait may have rebuilt the heap, so the current min can be a different
                // (future) entry. Only dequeue-and-fire when the real min is genuinely due — otherwise leave
                // it and re-measure next iteration, so a future occurrence is never fired early.
                if (_heap.TryPeek(out _, out var dueAt) && dueAt <= _clock.GetUtcNow())
                {
                    _heap.TryDequeue(out entry, out _);
                }
                else
                {
                    entry = null;
                }
            }
            if (entry is null)
            {
                continue;
            }
            var stillScheduled = await FireAsync(entry, ct).ConfigureAwait(false);
            if (!stillScheduled)
            {
                // The fire discovered the definition was deleted; drop it from the schedule now instead of
                // re-arming an entry that would fire-then-skip every occurrence until an unrelated rescan.
                continue;
            }

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
                    // A concurrent rescan (NotifyDefinitionChangedAsync) may have already re-armed this
                    // definition while the fire was in flight — its fresh scan still includes it. Re-adding
                    // it here would leave two heap entries for one definition, firing it twice next slot.
                    if (!HeapContainsDefinition(rescheduled.BatchDefinitionId))
                    {
                        _heap.Enqueue(rescheduled, rescheduled.NextFireUtc);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Triggers the batch run for one occurrence. Returns <c>true</c> if the definition is still
    /// schedulable (the loop re-arms its next occurrence), or <c>false</c> if the definition has been
    /// deleted (the loop drops it from the schedule immediately). A failed trigger that is NOT a deletion
    /// returns <c>true</c> — a transient fault must not silently de-schedule a live batch.
    /// </summary>
    private async Task<bool> FireAsync(ScheduledBatchEntry entry, CancellationToken ct)
    {
        try
        {
            // The run owns its own lifecycle (the run record + the execution rows). There is no
            // orphan-row compensation here — unlike the single-job scheduler, no pre-created row exists
            // to fail. TriggerBatchAsync returns a run id and the run proceeds on its own.
            await _runner.TriggerBatchAsync(
                entry.BatchDefinitionId, initialParameters: null,
                triggeredBy: _options.SchedulerTriggerIdentity, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Host shutdown between dequeue and trigger — nothing created, nothing to clean. The loop is
            // exiting on the same token, so the result is moot; keep the entry schedulable.
            return true;
        }
        catch (BatchDefinitionNotFoundException)
        {
            // The definition was deleted after it was armed. Report it gone so the loop removes it from the
            // schedule at this fire, rather than re-arming an entry that would fire-then-skip every
            // occurrence until an unrelated rescan happens to rebuild the heap.
            _logger.LogWarning(
                "Scheduled batch '{Batch}' ({DefId}) no longer exists; removing it from the schedule.",
                entry.BatchName, entry.BatchDefinitionId);
            return false;
        }
        catch (Exception ex)
        {
            // A transient fault (e.g. an unregistered local job, a momentary store hiccup) — log and keep
            // the batch scheduled; one bad fire must not silently de-schedule a live definition.
            _logger.LogError(ex, "Scheduled batch fire failed for '{Batch}' ({DefId}).", entry.BatchName, entry.BatchDefinitionId);
            return true;
        }
    }

    /// <summary>
    /// Re-scans code-defined and stored batches and rebuilds the schedule heap, then wakes the loop. Called
    /// after a dashboard/API create / update / delete so a newly-scheduled batch starts firing — and a
    /// de-scheduled or deleted batch stops firing — without a host restart. Re-entrant and cheap: a small
    /// heap plus a bounded paged scan. Multiple notifications collapse to one wake via the wake channel.
    /// </summary>
    /// <remarks>
    /// Safe to call with <see cref="CancellationToken.None"/> — the rescan outlives the request that
    /// triggered it. The scan and cron parsing run outside the heap lock; the heap is then cleared and
    /// repopulated inside ONE lock region so the loop never observes a partially-rebuilt heap and the
    /// rebuild is never interleaved with a concurrent enqueue/dequeue from the loop thread. Rebuilding from
    /// the current scan (rather than additively arming) is what prunes a batch whose schedule was removed:
    /// it is simply absent from the fresh entry set.
    /// </remarks>
    public async Task NotifyDefinitionChangedAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _started) == 0)
        {
            // Not running yet — StartAsync will perform the full scan.
            return;
        }

        try
        {
            var entries = await ComputeScheduledEntriesAsync(cancellationToken).ConfigureAwait(false);
            lock (_heapLock)
            {
                // Clear-and-rebuild in a single lock region. Enumerating PriorityQueue.UnorderedItems while
                // another thread enqueues/dequeues is unsafe, so the swap must be atomic with respect to the
                // loop thread. A full rebuild also drops entries for deleted/de-scheduled definitions, which an
                // additive arm would leave behind to fire (and fail) on their next occurrence.
                _heap.Clear();
                foreach (var entry in entries)
                {
                    // The scan yields one entry per definition; the dedupe guard pairs with the loop's own
                    // reschedule guard so a definition the loop is mid-firing ends up with exactly one entry.
                    if (!HeapContainsDefinition(entry.BatchDefinitionId))
                    {
                        _heap.Enqueue(entry, entry.NextFireUtc);
                    }
                }
            }
            _wakeChannel.Writer.TryWrite(true);
        }
        catch (Exception ex)
        {
            // Runs fire-and-forget off the request thread; a faulted scan (e.g. the definition store is
            // momentarily unavailable) must not become an unobserved task exception. Log and leave the
            // existing schedule armed — the next definition change re-attempts the rescan.
            _logger.LogWarning(ex, "Batch schedule rescan after a definition change failed; the schedule was not re-armed.");
        }
    }

    /// <summary>
    /// Whether the schedule heap already holds an entry for the given definition. The caller MUST hold
    /// <c>_heapLock</c> — enumerating the underlying queue is not safe concurrent with an enqueue/dequeue.
    /// A linear scan over a small schedule heap.
    /// </summary>
    private bool HeapContainsDefinition(string batchDefinitionId)
    {
        foreach (var (entry, _) in _heap.UnorderedItems)
        {
            if (string.Equals(entry.BatchDefinitionId, batchDefinitionId, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
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
                "Batch scheduler loop did not stop within {Timeout}; abandoning the wait (process shutdown continues).",
                _options.ShutdownTimeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // host cancelled the StopAsync grace period — continue cleanup. The filter keeps any
            // OTHER cancellation (a genuinely faulted loop task) loud instead of swallowed.
        }
    }

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
