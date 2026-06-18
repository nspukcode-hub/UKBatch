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
/// without a restart.
/// <para>By default a scheduled fire that was due while the process was down is skipped (the loop arms
/// each batch's next future occurrence on start). A batch may OPT IN to catching up the single most
/// recent missed occurrence on restart by setting <c>BatchDefinition.ScheduleCatchUpWindow</c>; this
/// requires a durable <see cref="IScheduleStateStore"/> (the EF adapter) and is bounded by the per-batch
/// window — only an occurrence missed within that window is replayed, exactly once, and the persisted
/// last-fire watermark prevents firing the same occurrence twice. With in-memory storage no watermark
/// store is registered, so catch-up stays inactive and the skip behavior is unchanged.</para>
/// <para>Single-node: a shared watermark store records the last fire but does not coordinate two
/// scheduler instances. Two nodes against one database would each read the same watermarks at start and
/// both catch up the same occurrence (the monotonic watermark dedupes the write, not the fire).
/// Distributed catch-up (a claim/lease) is out of scope.</para>
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
    private readonly IScheduleStateStore? _scheduleState;
    private readonly ILogger<BatchScheduler> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task? _loopTask;
    private int _started;
    private int _disposed;

    /// <summary>
    /// Constructs the scheduler with composed dependencies. <paramref name="scheduleStateStores"/> is an
    /// enumerable purely so missed-fire catch-up can be OPTIONAL: the durable watermark store is
    /// registered only by the EF adapter, so an <c>IEnumerable</c> resolves to an empty sequence (and
    /// catch-up stays inactive) when no EF adapter is present, without needing a custom DI factory.
    /// </summary>
    public BatchScheduler(
        IBatchDefinitionLookup batchLookup,
        IBatchDefinitionStore batchStore,
        IJobRunner runner,
        CronExpressionCache cronCache,
        TimeProvider clock,
        IOptions<UKBatchOptions> options,
        IEnumerable<IScheduleStateStore> scheduleStateStores,
        ILogger<BatchScheduler> logger)
    {
        ArgumentNullException.ThrowIfNull(batchLookup);
        ArgumentNullException.ThrowIfNull(batchStore);
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(cronCache);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scheduleStateStores);
        ArgumentNullException.ThrowIfNull(logger);
        _batchLookup = batchLookup;
        _batchStore = batchStore;
        _runner = runner;
        _cronCache = cronCache;
        _clock = clock;
        _options = options.Value;
        // At most one durable watermark store is ever registered (the EF adapter); take the first if
        // present, else null — catch-up is then a no-op.
        _scheduleState = scheduleStateStores.FirstOrDefault();
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
        // Only the startup scan catches up missed occurrences; a later rescan must not (see
        // NotifyDefinitionChangedAsync). This is the one path that consults the durable watermarks.
        var entries = await ComputeScheduledEntriesAsync(catchUp: true, cancellationToken).ConfigureAwait(false);
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
    private async Task<List<ScheduledBatchEntry>> ComputeScheduledEntriesAsync(bool catchUp, CancellationToken cancellationToken)
    {
        var nowUtc = _clock.GetUtcNow();
        var entries = new List<ScheduledBatchEntry>();

        // Only the startup scan with a durable store present reads the watermarks (one round-trip). A
        // rescan, or an in-memory deployment, gets an empty map so every batch arms its next future
        // occurrence (no catch-up).
        IReadOnlyDictionary<string, DateTimeOffset> watermarks = EmptyWatermarks;
        if (catchUp && _scheduleState is not null)
        {
            try
            {
                watermarks = await _scheduleState.GetAllAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // First boot before migrations are applied throws a missing-table exception; that is
                // expected, not a warning. Skip catch-up this start (every batch arms its next future
                // occurrence) and let the host come up.
                // A genuine fault here (the schedule database is unreachable at startup) would otherwise
                // silently disable catch-up for every batch, so surface it at Information rather than bury
                // it at Debug. The benign first-boot case (the table does not exist yet, before migrations
                // run) lands here too and logs a single line — an acceptable cost for the visibility a real
                // outage needs.
                _logger.LogInformation(
                    ex, "Schedule catch-up skipped this start: the watermark store could not be read; arming next future occurrences only.");
                watermarks = EmptyWatermarks;
            }
        }

        // Code-defined batches (in-process registry, synchronous).
        foreach (var def in _batchLookup.All())
        {
            if (TryBuildEntry(def, nowUtc, watermarks, catchUp, out var entry))
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
                    if (TryBuildEntry(def, nowUtc, watermarks, catchUp, out var entry))
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

    private static readonly IReadOnlyDictionary<string, DateTimeOffset> EmptyWatermarks =
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    /// <summary>
    /// Builds a heap entry for a scheduled definition, or returns <c>false</c> for an unscheduled definition,
    /// a malformed cron expression, or an expression with no future occurrence. Pure (no heap mutation, no
    /// I/O) so it is safe to call from the rebuild path while holding nothing.
    /// </summary>
    private bool TryBuildEntry(
        BatchDefinition def,
        DateTimeOffset nowUtc,
        IReadOnlyDictionary<string, DateTimeOffset> watermarks,
        bool catchUp,
        out ScheduledBatchEntry entry)
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

        // Default arming: the next future occurrence (the established skip-on-downtime behavior). Used for
        // every batch without a catch-up window, on every rescan, and as the fallback when no missed
        // occurrence is fresh enough to replay.
        var next = expr.GetNextOccurrence(nowUtc.UtcDateTime, TimeZoneInfo.Utc);

        // Missed-fire catch-up (startup only, durable store present, per-batch window set). If a cron
        // occurrence was missed within the window since the last recorded fire, arm THAT past occurrence
        // so the loop fires it immediately; otherwise fall through to the next future occurrence.
        if (catchUp
            && _scheduleState is not null
            && def.ScheduleCatchUpWindow is { } window
            && window > TimeSpan.Zero
            && watermarks.TryGetValue(def.Id, out var lastFired))
        {
            var missed = LatestMissedOccurrence(expr, lastFired, nowUtc);
            // Replay only when the occurrence is fresh enough (now - O <= window). A stale gap (down longer
            // than the window) is intentionally NOT replayed — the run would be too late to be useful. The
            // `occurrence > lastFired` check is defense-in-depth: LatestMissedOccurrence walks forward from
            // lastFired via GetNextOccurrence, which is exclusive of its argument, so `missed` is already
            // strictly after the watermark — but the explicit guard documents and guarantees the
            // no-double-fire invariant even if that walk is ever changed to be inclusive.
            if (missed is { } occurrence && occurrence > lastFired && nowUtc - occurrence <= window)
            {
                entry = new ScheduledBatchEntry
                {
                    BatchDefinitionId = def.Id,
                    BatchName = def.Name,
                    CronExpression = expr,
                    NextFireUtc = occurrence,   // a PAST instant — the loop fires it immediately, then re-arms forward
                    CatchUpWindow = def.ScheduleCatchUpWindow,
                };
                return true;
            }
        }

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
            CatchUpWindow = def.ScheduleCatchUpWindow,
        };
        return true;
    }

    /// <summary>
    /// Returns the LATEST cron occurrence in the half-open interval <c>(lastFired, now]</c>, or
    /// <c>null</c> if there is none. Walks forward from <paramref name="lastFired"/>, keeping the last
    /// occurrence that is still at or before <paramref name="nowUtc"/>. The walk is capped so a very
    /// stale watermark (process down for a long time on a frequent schedule) cannot spin: past the cap we
    /// give up on catch-up (the caller arms the next future occurrence instead).
    /// </summary>
    private DateTimeOffset? LatestMissedOccurrence(CronExpression expr, DateTimeOffset lastFired, DateTimeOffset nowUtc)
    {
        // 100,000 occurrences covers ~1.1 days of an every-second cron (the worst realistic frequent
        // schedule) before giving up; a coarser cron exhausts its missed slots far sooner. Past the cap we
        // decline catch-up (the caller arms the next future occurrence) rather than spin on a stale watermark.
        const int maxIterations = 100_000;
        DateTimeOffset? latest = null;
        var cursor = lastFired.UtcDateTime;
        for (var i = 0; i < maxIterations; i++)
        {
            var nextUtc = expr.GetNextOccurrence(cursor, TimeZoneInfo.Utc);
            if (nextUtc is null)
            {
                return latest;   // schedule has no further occurrences — the last one we kept is the answer.
            }
            var candidate = new DateTimeOffset(nextUtc.Value, TimeSpan.Zero);
            if (candidate > nowUtc)
            {
                return latest;   // walked past now — the previous candidate (if any) is the latest missed one.
            }
            latest = candidate;
            cursor = nextUtc.Value;
        }

        // Hit the iteration cap without reaching now: the watermark is too stale to walk on a frequent
        // schedule. Decline catch-up so we cannot spin; the caller arms the next future occurrence.
        _logger.LogDebug(
            "Schedule catch-up walk exceeded {Cap} iterations for a stale watermark; skipping catch-up and arming the next future occurrence.",
            maxIterations);
        return null;
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
            // Persist the last-fire watermark BEFORE triggering, for catch-up-enabled batches with a
            // durable store. This is deliberately at-most-once: a crash in the tiny window between this
            // write and the trigger loses THIS fire rather than risking a double-fire (the watermark
            // already advanced, so a restart will not replay this occurrence). A watermark write failure
            // must never abort the fire — log at Debug and proceed.
            if (entry.CatchUpWindow is { } window && window > TimeSpan.Zero && _scheduleState is not null)
            {
                try
                {
                    await _scheduleState.RecordFiredAsync(entry.BatchDefinitionId, entry.NextFireUtc, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogDebug(
                        ex, "Could not persist the schedule watermark for '{Batch}' ({DefId}) before firing; proceeding.",
                        entry.BatchName, entry.BatchDefinitionId);
                }
            }

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
            // catchUp: false — a definition-change rescan must never replay a missed occurrence; only the
            // startup scan does. (A dashboard edit landing after a brief downtime must not retroactively
            // fire a past run.)
            var entries = await ComputeScheduledEntriesAsync(catchUp: false, cancellationToken).ConfigureAwait(false);
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
