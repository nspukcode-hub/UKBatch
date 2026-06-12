using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// The scheduler waits on the timer's clock but its deadlines live on the injected wall clock, so
/// the two can disagree at an occurrence boundary (timer rounding, NTP slew). These tests pin the
/// two defenses: an occurrence must never fire before its wall-clock deadline, and a fired
/// occurrence must never be re-armed — each cron occurrence produces at most one execution.
/// </summary>
public class JobSchedulerClockSkewTests
{
    /// <summary>
    /// FakeTimeProvider refuses to move backward, but the backward-step scenario is exactly what
    /// the re-arm anchor defends against — this minimal clock allows arbitrary repositioning.
    /// </summary>
    private sealed class SteppableClock : TimeProvider
    {
        private long _utcTicks;
        public SteppableClock(DateTimeOffset start) => _utcTicks = start.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Set(DateTimeOffset value) => Interlocked.Exchange(ref _utcTicks, value.UtcTicks);
    }

    private static JobDefinition Def(string name, string schedule) => new()
    {
        Name = name,
        ImplementationTypeName = typeof(object).AssemblyQualifiedName,
        IsPartitioned = false,
        Schedule = schedule,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        PartitionWorkerCount = 0,
        ItemErrorPolicy = ItemErrorPolicy.FailFast,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
        SourceService = null,
    };

    private static (JobScheduler Scheduler, InMemoryJobStore Store, TimeProvider Clock) Build(string cron, TimeProvider? clockOverride = null)
    {
        var clock = clockOverride ?? new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var registry = new JobDefinitionRegistry();
        registry.Register(Def("skew.job", cron), typeof(object), null);
        var options = Options.Create(new UKBatchOptions { CronFormat = Cronos.CronFormat.IncludeSeconds });
        var watchHub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var store = new InMemoryJobStore(clock, options, watchHub);
        var dispatcher = new JobDispatcher(options, NullLogger<JobDispatcher>.Instance);
        var scheduler = new JobScheduler(
            registry, dispatcher, store, new CronExpressionCache(), clock,
            options, NullLogger<JobScheduler>.Instance);
        return (scheduler, store, clock);
    }

    private static async Task<int> CountAsync(InMemoryJobStore store)
    {
        var page = await store.QueryAsync(new JobQuery { JobName = "skew.job" }, default).ConfigureAwait(false);
        return page.Count;
    }

    [Fact]
    public async Task Loop_TimerElapsesBeforeWallClockDeadline_DoesNotFireEarly_ThenFiresExactlyOnce()
    {
        // Every-second cron: the first occurrence is wall-clock 00:00:01. The loop's real
        // Task.Delay(1s) elapses while the fake wall clock stays frozen at 00:00:00 — exactly the
        // "timer finished before the wall-clock deadline" skew. Without the due-recheck the very
        // first delay completion fires the occurrence early.
        var (scheduler, store, clockBase) = Build("* * * * * *");
        var clock = (FakeTimeProvider)clockBase;
        await scheduler.StartAsync(default).ConfigureAwait(false);
        try
        {
            // Several real loop cycles elapse; the wall clock never reaches the deadline.
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            (await CountAsync(store).ConfigureAwait(false)).Should().Be(0,
                "an occurrence must not fire before its wall-clock deadline, no matter how often the timer wakes");

            // Reach the deadline: the next loop cycle (<= ~1s real) must fire it exactly once.
            clock.Advance(TimeSpan.FromSeconds(1));
            var fired = await Waits.ForAsync(
                async () => await CountAsync(store).ConfigureAwait(false) >= 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            fired.Should().BeTrue("the occurrence must fire once the wall clock reaches its deadline");

            // The wall clock is frozen at the fired occurrence; the re-arm anchor must point
            // STRICTLY AFTER it, so no second execution may appear for the same occurrence.
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            (await CountAsync(store).ConfigureAwait(false)).Should().Be(1,
                "a fired occurrence must never be re-armed (one execution per cron occurrence)");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Dispose_AfterStop_IsIdempotent_AndDisposeWithoutStopCancelsTheLoop()
    {
        // The stopping source is a LINKED token source: leaving it undisposed leaks its
        // registration on the host's stopping token. Dispose must be safe after StopAsync,
        // safe twice, and — at container teardown without a prior stop — must itself cancel
        // the loop without the loop tripping over the disposed source.
        var (scheduler, _, _) = Build("* * * * * *");
        await scheduler.StartAsync(default);
        await scheduler.StopAsync(CancellationToken.None);
        scheduler.Dispose();
        scheduler.Dispose();

        var (scheduler2, _, _) = Build("* * * * * *");
        await scheduler2.StartAsync(default);
        scheduler2.Dispose();
        await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
        // Reaching here without an unobserved crash is the assertion; the loop treats the
        // disposed source as cancellation and exits.
    }

    [Fact]
    public async Task StartAsync_CalledTwice_SecondStartIsIgnored_NoDuplicateFires()
    {
        // A second start would re-arm every definition into the same heap and launch a second
        // loop over it — each occurrence would then fire twice (and the first loop would become
        // unstoppable, its cancellation source overwritten).
        var (scheduler, store, clockBase) = Build("* * * * * *");
        var clock = (FakeTimeProvider)clockBase;
        await scheduler.StartAsync(default);
        await scheduler.StartAsync(default);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var fired = await Waits.ForAsync(
                async () => await CountAsync(store).ConfigureAwait(false) >= 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            fired.Should().BeTrue();

            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);
            (await CountAsync(store).ConfigureAwait(false)).Should().Be(1,
                "a duplicate StartAsync must be a no-op — one execution per cron occurrence");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Loop_WallClockStepsBackwardAfterFire_DoesNotRefireSameOccurrence()
    {
        var clock = new SteppableClock(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var (scheduler, store, _) = Build("* * * * * *", clock);
        await scheduler.StartAsync(default).ConfigureAwait(false);
        try
        {
            // Fire the 00:00:01 occurrence.
            clock.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
            var fired = await Waits.ForAsync(
                async () => await CountAsync(store).ConfigureAwait(false) >= 1,
                TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            fired.Should().BeTrue();

            // A backward wall-clock step (NTP correction) followed by re-reaching the same
            // wall-clock second must NOT replay the already-fired occurrence: the re-arm anchor
            // is the fired occurrence itself, never an earlier "now".
            clock.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 0, 500, TimeSpan.Zero));
            await Task.Delay(TimeSpan.FromSeconds(1.5)).ConfigureAwait(false);
            clock.Set(new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
            await Task.Delay(TimeSpan.FromSeconds(2.5)).ConfigureAwait(false);

            (await CountAsync(store).ConfigureAwait(false)).Should().Be(1,
                "stepping the wall clock backward must not re-arm an occurrence that already fired");
        }
        finally
        {
            await scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }
}
