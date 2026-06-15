using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <see cref="BatchScheduler"/> drives whole-batch runs off a cron heap, scanning BOTH the code-defined
/// lookup and the stored definitions. Driven by <see cref="FakeTimeProvider"/> so the timing is
/// deterministic. The headline guards: exactly one trigger per cron occurrence; a definition-change rescan
/// that lands while a fire is in flight must NOT leave a duplicate heap entry (the double-fire fix); a
/// faulted rescan is swallowed (no unobserved task exception); and de-scheduling a batch stops it.
/// </summary>
public class BatchSchedulerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Records every TriggerBatchAsync by definition id; optionally blocks the first fire on a gate.</summary>
    private sealed class RecordingRunner : IJobRunner
    {
        private readonly object _lock = new();
        private readonly List<string> _triggered = new();
        private readonly TaskCompletionSource? _firstFireGate;
        private int _fireCount;

        public RecordingRunner(TaskCompletionSource? firstFireGate = null) => _firstFireGate = firstFireGate;

        /// <summary>When set, every fire throws <see cref="BatchDefinitionNotFoundException"/> — a definition deleted while armed.</summary>
        public bool ThrowNotFound { get; init; }

        /// <summary>Signalled (set to the firing definition id) the first time a fire is observed.</summary>
        public TaskCompletionSource<string> FirstFireObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Triggered
        {
            get { lock (_lock) { return _triggered.ToList(); } }
        }

        public int CountFor(string definitionId)
        {
            lock (_lock) { return _triggered.Count(d => d == definitionId); }
        }

        public async Task<string> TriggerBatchAsync(string batchDefinitionId, JobParameters? initialParameters, string? triggeredBy, CancellationToken cancellationToken)
        {
            lock (_lock) { _triggered.Add(batchDefinitionId); }
            FirstFireObserved.TrySetResult(batchDefinitionId);
            if (ThrowNotFound)
            {
                throw new BatchDefinitionNotFoundException($"BatchDefinition {batchDefinitionId} not found.")
                {
                    BatchDefinitionId = batchDefinitionId,
                };
            }
            // The first fire optionally parks on a gate so a rescan can be injected while it is in flight.
            if (Interlocked.Increment(ref _fireCount) == 1 && _firstFireGate is not null)
            {
                await _firstFireGate.Task.ConfigureAwait(false);
            }
            return Guid.NewGuid().ToString("N");
        }

        public Task<JobExecution> TriggerAsync(string jobName, JobParameters parameters, string? triggeredBy, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task CancelAsync(string executionId, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    /// <summary>A lookup over a fixed set of code-defined batch definitions.</summary>
    private sealed class FakeLookup : IBatchDefinitionLookup
    {
        private readonly List<BatchDefinition> _defs;
        public FakeLookup(params BatchDefinition[] defs) => _defs = defs.ToList();
        public BatchDefinition? TryGetByName(string name) => _defs.FirstOrDefault(d => d.Name == name);
        public BatchDefinition? TryGetById(string id) => _defs.FirstOrDefault(d => d.Id == id);
        public IReadOnlyList<BatchDefinition> All() => _defs.ToList();
    }

    /// <summary>A mutable store whose Dashboard/Api page snapshots the scheduler scans and rescans.</summary>
    private sealed class FakeStore : IBatchDefinitionStore
    {
        private readonly object _lock = new();
        private readonly List<BatchDefinition> _defs = new();

        public void Set(params BatchDefinition[] defs)
        {
            lock (_lock) { _defs.Clear(); _defs.AddRange(defs); }
        }

        public Task<IReadOnlyList<BatchDefinition>> ListAsync(BatchSource source, int offset, int limit, CancellationToken cancellationToken)
        {
            lock (_lock)
            {
                var page = _defs.Where(d => d.Source == source).Skip(offset).Take(limit).ToList();
                return Task.FromResult<IReadOnlyList<BatchDefinition>>(page);
            }
        }

        public Task<BatchDefinition> CreateAsync(BatchDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition> UpdateAsync(BatchDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string batchDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition?> GetAsync(string batchDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition?> GetByNameAsync(string name, BatchSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> CountAsync(BatchSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    /// <summary>A store that lists empty until <see cref="ShouldFault"/> is set, then faults — to prove a
    /// rescan failure is swallowed without breaking the initial start scan.</summary>
    private sealed class FaultingStore : IBatchDefinitionStore
    {
        /// <summary>When true, ListAsync faults (set AFTER StartAsync so only the rescan hits it).</summary>
        public volatile bool ShouldFault;

        public Task<IReadOnlyList<BatchDefinition>> ListAsync(BatchSource source, int offset, int limit, CancellationToken cancellationToken)
            => ShouldFault
                ? Task.FromException<IReadOnlyList<BatchDefinition>>(new InvalidOperationException("store unavailable"))
                : Task.FromResult<IReadOnlyList<BatchDefinition>>(Array.Empty<BatchDefinition>());

        public Task<BatchDefinition> CreateAsync(BatchDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition> UpdateAsync(BatchDefinition definition, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteAsync(string batchDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition?> GetAsync(string batchDefinitionId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BatchDefinition?> GetByNameAsync(string name, BatchSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<long> CountAsync(BatchSource source, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingLogger : ILogger<BatchScheduler>
    {
        private readonly object _lock = new();
        public List<(LogLevel Level, string Message)> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lock) { Entries.Add((logLevel, formatter(state, exception))); }
        }

        public bool Any(LogLevel level, string contains)
        {
            lock (_lock)
            {
                return Entries.Any(e => e.Level == level && e.Message.Contains(contains, StringComparison.Ordinal));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    private static BatchDefinition Def(string id, string name, string? schedule, BatchSource source = BatchSource.Code) => new()
    {
        Id = id,
        Name = name,
        Source = source,
        Schedule = schedule,
        Steps = Array.Empty<BatchStep>(),
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        OnFailureSteps = Array.Empty<BatchStep>(),
        CreatedAtUtc = T0,
        Version = 0,
    };

    private static (BatchScheduler Scheduler, RecordingRunner Runner, FakeStore Store, CapturingLogger Logger) Build(
        FakeTimeProvider clock,
        IBatchDefinitionLookup? lookup = null,
        IBatchDefinitionStore? store = null,
        RecordingRunner? runner = null)
    {
        runner ??= new RecordingRunner();
        var fakeStore = store as FakeStore ?? new FakeStore();
        var options = Options.Create(new UKBatchOptions
        {
            CronFormat = Cronos.CronFormat.IncludeSeconds,
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        });
        var logger = new CapturingLogger();
        var scheduler = new BatchScheduler(
            lookup ?? new FakeLookup(),
            store ?? fakeStore,
            runner,
            new CronExpressionCache(),
            clock,
            options,
            logger);
        return (scheduler, runner, fakeStore, logger);
    }

    [Fact]
    public async Task SingleFirePerSlot_AdvancingPastOneOccurrence_FiresExactlyOnce()
    {
        var clock = new FakeTimeProvider(T0);
        var lookup = new FakeLookup(Def("def-1", "every-second", "* * * * * *"));
        var (scheduler, runner, _, _) = Build(clock, lookup: lookup);
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var fired = await Waits.ForAsync(() => runner.CountFor("def-1") >= 1, TimeSpan.FromSeconds(10));
            fired.Should().BeTrue("the cron occurrence must fire once the clock reaches its deadline");

            // Hold the wall clock at the fired occurrence: the re-arm anchor must point strictly after it,
            // so no second trigger may appear for the same slot.
            await Task.Delay(TimeSpan.FromSeconds(1));
            runner.CountFor("def-1").Should().Be(1, "exactly one trigger per cron occurrence");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task ConcurrentNotifyDuringFire_DoesNotDoubleFire()
    {
        // The double-fire fix: a definition-change rescan (NotifyDefinitionChangedAsync) can land while a
        // fire is in flight. Its fresh scan still includes the firing definition; without the heap-dedupe
        // guard the loop's reschedule would then add a SECOND heap entry for that definition, firing it
        // twice in the next slot. Arrange a fire that parks, inject the rescan while parked, release, then
        // advance to the next slot and assert exactly ONE trigger lands there.
        var clock = new FakeTimeProvider(T0);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner(firstFireGate: gate);
        var scheduledDef = Def("def-1", "every-second", "* * * * * *");
        var lookup = new FakeLookup(scheduledDef);
        var (scheduler, _, _, _) = Build(clock, lookup: lookup, runner: runner);
        await scheduler.StartAsync(default);
        try
        {
            // Fire the first occurrence; the runner parks on the gate.
            clock.Advance(TimeSpan.FromSeconds(1));
            (await runner.FirstFireObserved.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("def-1");

            // Inject a rescan WHILE the fire is parked — the rebuild re-arms def-1 (its cron is unchanged).
            await scheduler.NotifyDefinitionChangedAsync(CancellationToken.None);

            // Release the fire so the loop's reschedule runs (and must dedupe against the rebuilt entry).
            gate.SetResult();

            // Advance to the next slot. With a single heap entry, exactly one more trigger fires.
            clock.Advance(TimeSpan.FromSeconds(1));
            var secondFired = await Waits.ForAsync(() => runner.CountFor("def-1") >= 2, TimeSpan.FromSeconds(10));
            secondFired.Should().BeTrue("the next slot fires the single armed entry");

            // Hold the clock and confirm the count does not jump to 3 — proving no duplicate heap entry.
            await Task.Delay(TimeSpan.FromSeconds(1));
            runner.CountFor("def-1").Should().Be(2,
                "a rescan landing during a fire must not leave a duplicate heap entry that double-fires the next slot");
        }
        finally
        {
            // The gate is already released; stop normally.
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_ScansBothCodeAndStoreSources_BothFire()
    {
        var clock = new FakeTimeProvider(T0);
        var lookup = new FakeLookup(Def("code-def", "code-batch", "* * * * * *", BatchSource.Code));
        var store = new FakeStore();
        store.Set(Def("store-def", "store-batch", "* * * * * *", BatchSource.Dashboard));
        var (scheduler, runner, _, _) = Build(clock, lookup: lookup, store: store);
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var bothFired = await Waits.ForAsync(
                () => runner.CountFor("code-def") >= 1 && runner.CountFor("store-def") >= 1,
                TimeSpan.FromSeconds(10));
            bothFired.Should().BeTrue("both the code-defined and the store-defined scheduled batch must arm and fire");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StartAsync_MalformedCron_SkipsOnlyThatBatch_AndArmsTheRest()
    {
        var clock = new FakeTimeProvider(T0);
        var lookup = new FakeLookup(
            Def("good", "good-batch", "* * * * * *"),
            Def("bad", "bad-batch", "not a cron at all"));
        var (scheduler, runner, _, logger) = Build(clock, lookup: lookup);

        // Start must NOT throw despite the bad-cron definition.
        var start = () => scheduler.StartAsync(default);
        await start.Should().NotThrowAsync();
        try
        {
            logger.Any(LogLevel.Error, "bad-batch").Should().BeTrue("the bad cron is logged at Error naming the batch");

            clock.Advance(TimeSpan.FromSeconds(1));
            var goodFired = await Waits.ForAsync(() => runner.CountFor("good") >= 1, TimeSpan.FromSeconds(10));
            goodFired.Should().BeTrue("the valid scheduled batch must still arm after the bad one is skipped");
            runner.CountFor("bad").Should().Be(0, "the malformed-cron batch never fires");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotifyDefinitionChanged_ArmsNewlyScheduledBatch_AfterStart()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new FakeStore();   // empty at start
        var (scheduler, runner, _, _) = Build(clock, store: store);
        await scheduler.StartAsync(default);
        try
        {
            // No scheduled batch yet — advancing fires nothing.
            clock.Advance(TimeSpan.FromSeconds(2));
            await Task.Delay(TimeSpan.FromMilliseconds(200));
            runner.Triggered.Should().BeEmpty("nothing is scheduled before the store gains a batch");

            // Add a scheduled batch and notify; the rescan arms it.
            store.Set(Def("new-def", "new-batch", "* * * * * *", BatchSource.Dashboard));
            await scheduler.NotifyDefinitionChangedAsync(CancellationToken.None);

            clock.Advance(TimeSpan.FromSeconds(1));
            var fired = await Waits.ForAsync(() => runner.CountFor("new-def") >= 1, TimeSpan.FromSeconds(10));
            fired.Should().BeTrue("a newly-scheduled batch arms via NotifyDefinitionChangedAsync without a restart");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotifyDefinitionChanged_PrunesDeScheduledBatch_StopsFiring()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new FakeStore();
        store.Set(Def("def-1", "batch", "* * * * * *", BatchSource.Dashboard));
        var (scheduler, runner, _, _) = Build(clock, store: store);
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            (await Waits.ForAsync(() => runner.CountFor("def-1") >= 1, TimeSpan.FromSeconds(10))).Should().BeTrue();

            // De-schedule the batch (definition kept, Schedule cleared) and rescan — the rebuild drops it.
            store.Set(Def("def-1", "batch", schedule: null, BatchSource.Dashboard));
            await scheduler.NotifyDefinitionChangedAsync(CancellationToken.None);

            var countAfterPrune = runner.CountFor("def-1");
            // Advance several seconds; a pruned batch must not fire again.
            clock.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            runner.CountFor("def-1").Should().Be(countAfterPrune,
                "a de-scheduled batch is dropped from the heap and stops firing");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotifyDefinitionChanged_FaultedScan_IsSwallowed_AndLogged()
    {
        // A rescan runs fire-and-forget off the request thread; a faulted scan (store momentarily down)
        // must NOT become an unobserved task exception — it is swallowed and logged, the schedule unchanged.
        var clock = new FakeTimeProvider(T0);
        var lookup = new FakeLookup(Def("code-def", "code-batch", "* * * * * *"));
        var faultingStore = new FaultingStore();
        var (scheduler, _, _, logger) = Build(clock, lookup: lookup, store: faultingStore);
        await scheduler.StartAsync(default);
        try
        {
            // Start scanned cleanly; now make the store fault so ONLY the rescan hits the failure.
            faultingStore.ShouldFault = true;

            var act = async () => await scheduler.NotifyDefinitionChangedAsync(CancellationToken.None);
            await act.Should().NotThrowAsync("a faulted rescan must be swallowed, not surfaced to the caller");

            logger.Any(LogLevel.Warning, "rescan").Should().BeTrue("the swallowed rescan failure is logged at Warning");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task NotifyDefinitionChanged_BeforeStart_IsNoOp()
    {
        var clock = new FakeTimeProvider(T0);
        var store = new FakeStore();
        store.Set(Def("def-1", "batch", "* * * * * *", BatchSource.Dashboard));
        var (scheduler, _, _, _) = Build(clock, store: store);

        // Not started yet — the notify is a no-op (StartAsync will do the full scan).
        var act = async () => await scheduler.NotifyDefinitionChangedAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_CalledTwice_SecondStartIsIgnored_NoDoubleFire()
    {
        var clock = new FakeTimeProvider(T0);
        var lookup = new FakeLookup(Def("def-1", "batch", "* * * * * *"));
        var (scheduler, runner, _, logger) = Build(clock, lookup: lookup);
        await scheduler.StartAsync(default);
        await scheduler.StartAsync(default);
        try
        {
            logger.Any(LogLevel.Warning, "more than once").Should().BeTrue("a duplicate start warns");

            clock.Advance(TimeSpan.FromSeconds(1));
            (await Waits.ForAsync(() => runner.CountFor("def-1") >= 1, TimeSpan.FromSeconds(10))).Should().BeTrue();

            await Task.Delay(TimeSpan.FromSeconds(1));
            runner.CountFor("def-1").Should().Be(1, "a duplicate StartAsync must not double-arm the heap");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopAsync_WedgedFire_DoesNotExceedShutdownTimeout()
    {
        // A wedged in-flight fire (the runner parks forever) must not hold shutdown hostage past the
        // ShutdownTimeout — StopAsync abandons the wait with a warning.
        var clock = new FakeTimeProvider(T0);
        var neverReleases = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new RecordingRunner(firstFireGate: neverReleases);
        var lookup = new FakeLookup(Def("def-1", "batch", "* * * * * *"));
        var (scheduler, _, _, logger) = Build(clock, lookup: lookup, runner: runner);
        await scheduler.StartAsync(default);

        // Fire and let the runner wedge.
        clock.Advance(TimeSpan.FromSeconds(1));
        (await runner.FirstFireObserved.Task.WaitAsync(TimeSpan.FromSeconds(10))).Should().Be("def-1");

        // Stop with a bounded wall-clock budget well above the 5s ShutdownTimeout; it must return promptly.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await scheduler.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        sw.Stop();

        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20),
            "StopAsync abandons a wedged fire after the ShutdownTimeout instead of blocking forever");
        logger.Any(LogLevel.Warning, "did not stop").Should().BeTrue("abandoning the wedged loop is logged");

        neverReleases.SetResult();   // unblock the wedged fire so the test task can drain
    }

    [Fact]
    public async Task FireFindsDefinitionDeleted_IsPrunedAtThatFire_NeverReArms()
    {
        // A definition deleted while armed: the fire raises BatchDefinitionNotFoundException. The loop must
        // drop it from the schedule at THAT fire — not re-arm a zombie entry that fires-then-404s every
        // occurrence until an unrelated rescan happens to rebuild the heap.
        var clock = new FakeTimeProvider(T0);
        var runner = new RecordingRunner { ThrowNotFound = true };
        var lookup = new FakeLookup(Def("gone", "deleted-batch", "* * * * * *"));
        var (scheduler, _, _, _) = Build(clock, lookup: lookup, runner: runner);
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            (await Waits.ForAsync(() => runner.CountFor("gone") >= 1, TimeSpan.FromSeconds(10)))
                .Should().BeTrue("the armed occurrence fires once and discovers the deletion");

            var countAtPrune = runner.CountFor("gone");
            // Advance several occurrences; a pruned definition must never fire again.
            clock.Advance(TimeSpan.FromSeconds(3));
            await Task.Delay(TimeSpan.FromMilliseconds(300));
            runner.CountFor("gone").Should().Be(countAtPrune,
                "a definition found deleted at fire time is pruned from the heap, not re-armed as a zombie");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None);
        }
    }
}
