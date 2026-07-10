using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Parallel-group LOCAL-child resume idempotency: a resumed run re-entering a half-finished group must
/// NOT re-dispatch a local child whose execution row already proves it Completed before the crash — a
/// successful financial child may have advanced the ledger. Its prior outputs still fold into the join
/// as a fresh success would; children without a Completed row (none, or a reaper-tombstoned Failed
/// orphan) DO run. On the TRIGGER path the probe is never consulted, keeping the first-pass child
/// dispatch identical to a probe-less build.
/// </summary>
public class ParallelGroupRunnerResumeSkipTests
{
    /// <summary>Every probe job appends its step id here, giving one dispatch record per test.</summary>
    private static readonly ConcurrentQueue<string> Ran = new();
    private static void ResetRan() => Ran.Clear();

    /// <summary>Child A: records its step id and emits {a: "fresh-a"}.</summary>
    public sealed class ChildAJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("a", "fresh-a");
            return Task.CompletedTask;
        }
    }

    /// <summary>Child B: records its step id and emits {b: "fresh-b"}.</summary>
    public sealed class ChildBJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("b", "fresh-b");
            return Task.CompletedTask;
        }
    }

    /// <summary>A downstream step that captures the parameter set it received (the folded outputs).</summary>
    public sealed class DownstreamCaptureJob : IJob
    {
        public static JobParameters? Captured;
        public static void Reset() => Captured = null;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured = context.Parameters;
            return Task.CompletedTask;
        }
    }

    /// <summary>A local child that parks on its cancellation token (the never-finishing sibling).</summary>
    public sealed class ParkingChildJob : IJob
    {
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A counting spy over the internal resume probe, to prove trigger-path silence.</summary>
    private sealed class CountingShadowProbe : IResumeShadowProbe
    {
        private readonly IResumeShadowProbe _inner;
        private int _queries;
        public CountingShadowProbe(IResumeShadowProbe inner) => _inner = inner;
        public int Queries => Volatile.Read(ref _queries);
        public Task<ResumeShadowCompletion?> TryGetCompletedStatusAsync(string batchId, string stepId, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _queries);
            return _inner.TryGetCompletedStatusAsync(batchId, stepId, cancellationToken);
        }
    }

    private static async Task<BatchRun> AwaitRunTerminalAsync(IBatchRunStore store, string runId)
    {
        BatchRun? run = null;
        var ok = await Waits.ForAsync(async () =>
        {
            run = await store.GetAsync(runId, CancellationToken.None);
            return run is { Status: not null };
        }, TimeSpan.FromSeconds(60));
        ok.Should().BeTrue("the run must reach a terminal stored status (60s deadlock backstop).");
        return run!;
    }

    /// <summary>Seeds an in-progress run (Status null) whose group step has not completed (cursor 0).</summary>
    private static async Task SeedInProgressRunAsync(IBatchRunStore runStore, string runId, BatchDefinition def, int stepCount)
    {
        await runStore.CreateAsync(new BatchRun
        {
            BatchId = runId,
            BatchDefinitionId = def.Id,
            BatchName = def.Name,
            Status = null,
            TriggeredBy = "tester",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = null,
            StepCount = stepCount,
            Total = 0,
            Succeeded = 0,
            Failed = 0,
            Cancelled = 0,
        }, CancellationToken.None);
        await runStore.UpdateCursorAsync(runId, 0, CancellationToken.None);
    }

    /// <summary>Inserts a terminal execution row for a (run, step id) into the in-memory store.</summary>
    private static Task SeedChildRowAsync(
        IJobStore jobStore, string runId, string stepId, JobStatus status,
        IReadOnlyDictionary<string, object?>? outputs = null)
    {
        var internalStore = (IJobStoreInternal)jobStore;
        var now = DateTimeOffset.UtcNow;
        return internalStore.InsertAsync(new JobExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            JobName = "SeededChild",
            BatchId = runId,
            BatchStepId = stepId,
            BatchDefinitionId = null,
            Status = status,
            Parameters = new Dictionary<string, object?>(),
            Outputs = outputs,
            EnqueuedAtUtc = now.AddMinutes(-5),
            StartedAtUtc = now.AddMinutes(-5),
            CompletedAtUtc = now.AddMinutes(-4),
            AttemptNumber = 1,
            MaxRetries = 0,
            LastError = status == JobStatus.Failed ? "orphaned" : null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = "tester",
            WorkerName = null,
        }, CancellationToken.None);
    }

    private static string IdNew() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task Resume_HalfDoneGroup_CompletedLocalChild_NotReDispatched_NoNewRow()
    {
        ResetRan();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ChildAJob>();
            b.AddJob<ChildBJob>();
            b.AddBatch("groupskip.completed", x => x
                .ThenInParallel(g => g.RunJob<ChildAJob>().RunJob<ChildBJob>()));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.completed")!;
            var childAId = def.Steps[0].ParallelGroup!.Steps[0].StepId;
            var childBId = def.Steps[0].ParallelGroup!.Steps[1].StepId;

            // Child A completed before the crash; the WaitAll join never satisfied (B unfinished).
            var runId = IdNew();
            await SeedInProgressRunAsync(runStore, runId, def, stepCount: 2);
            await SeedChildRowAsync(jobStore, runId, childAId, JobStatus.Completed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            Ran.Should().Equal(new[] { childBId },
                "the child with a prior Completed row is NOT re-dispatched; the unfinished child runs");

            // No NEW row for the skipped child: the seeded row remains its only execution.
            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Count(r => r.BatchStepId == childAId).Should().Be(1, "the skip path mints no new execution row");
            rows.Count(r => r.BatchStepId == childBId).Should().Be(1, "the re-run child produced exactly one fresh row");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_HalfDoneGroup_TombstonedFailedChild_ReRuns()
    {
        ResetRan();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ChildAJob>();
            b.AddJob<ChildBJob>();
            b.AddBatch("groupskip.tombstone", x => x
                .ThenInParallel(g => g.RunJob<ChildAJob>().RunJob<ChildBJob>()));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.tombstone")!;
            var childAId = def.Steps[0].ParallelGroup!.Steps[0].StepId;
            var childBId = def.Steps[0].ParallelGroup!.Steps[1].StepId;

            // A reaper-tombstoned Failed orphan does NOT prove the child finished — only Completed does —
            // so the resumed group must re-dispatch it (the at-least-once replay).
            var runId = IdNew();
            await SeedInProgressRunAsync(runStore, runId, def, stepCount: 2);
            await SeedChildRowAsync(jobStore, runId, childAId, JobStatus.Failed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            Ran.Should().Contain(childAId, "an ambiguous Failed orphan re-dispatches — it does not prove completion");
            Ran.Should().Contain(childBId, "the child with no prior row runs normally");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_WaitAll_PriorCompletedChildOutputs_FoldIntoJoin()
    {
        ResetRan();
        DownstreamCaptureJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ChildAJob>();
            b.AddJob<ChildBJob>();
            b.AddJob<DownstreamCaptureJob>();
            b.AddBatch("groupskip.fold.waitall", x => x
                .ThenInParallel(g => g.RunJob<ChildAJob>().RunJob<ChildBJob>())
                .ThenRunJob<DownstreamCaptureJob>());
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.fold.waitall")!;
            var childAId = def.Steps[0].ParallelGroup!.Steps[0].StepId;

            // Child A completed before the crash WITH outputs persisted on its row; the fold must carry
            // those outputs into the join exactly as a freshly-run child's would.
            var runId = IdNew();
            await SeedInProgressRunAsync(runStore, runId, def, stepCount: 3);
            await SeedChildRowAsync(jobStore, runId, childAId, JobStatus.Completed,
                outputs: new Dictionary<string, object?> { ["a"] = "prior-a" });

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the prior-Completed child satisfies the WaitAll join");

            var captured = DownstreamCaptureJob.Captured;
            captured.Should().NotBeNull("the downstream step must have run after the group joined");
            captured!.GetRequired<string>("a").Should().Be("prior-a",
                "the skipped child's persisted outputs fold into the join like a fresh success");
            captured.GetRequired<string>("b").Should().Be("fresh-b", "the re-run child's fresh outputs fold too");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_WaitAny_PriorCompletedChild_SatisfiesJoin()
    {
        ResetRan();
        DownstreamCaptureJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ChildAJob>();
            b.AddJob<ParkingChildJob>();
            b.AddJob<DownstreamCaptureJob>();
            b.AddBatch("groupskip.fold.waitany", x => x
                .ThenInParallel(g => g
                    .RunJob<ChildAJob>()
                    .RunJob<ParkingChildJob>()
                    .JoinPolicy(ParallelJoinPolicy.WaitAny))
                .ThenRunJob<DownstreamCaptureJob>());
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.fold.waitany")!;
            var childAId = def.Steps[0].ParallelGroup!.Steps[0].StepId;

            // The prior-Completed child resolves instantly on resume and wins the WaitAny join — the run
            // completes even though the sibling would never finish on its own.
            var runId = IdNew();
            await SeedInProgressRunAsync(runStore, runId, def, stepCount: 3);
            await SeedChildRowAsync(jobStore, runId, childAId, JobStatus.Completed,
                outputs: new Dictionary<string, object?> { ["a"] = "prior-a" });

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the prior-Completed child counts as the WaitAny winner");
            Ran.Should().NotContain(childAId, "the winning child was skipped, not re-run");
            DownstreamCaptureJob.Captured!.GetRequired<string>("a").Should().Be("prior-a",
                "the winner's persisted outputs are the join's folded outputs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_WaitMajority_TwoPriorCompletedChildren_ReachQuorum()
    {
        ResetRan();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ChildAJob>();
            b.AddJob<ChildBJob>();
            b.AddJob<ParkingChildJob>();
            b.AddBatch("groupskip.fold.majority", x => x
                .ThenInParallel(g => g
                    .RunJob<ChildAJob>()
                    .RunJob<ChildBJob>()
                    .RunJob<ParkingChildJob>()
                    .JoinPolicy(ParallelJoinPolicy.WaitMajority)));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.fold.majority")!;
            var group = def.Steps[0].ParallelGroup!;

            // Two of three children completed before the crash: their prior rows alone reach the 2/3
            // quorum on resume, so the run completes without waiting on the never-finishing third.
            var runId = IdNew();
            await SeedInProgressRunAsync(runStore, runId, def, stepCount: 3);
            await SeedChildRowAsync(jobStore, runId, group.Steps[0].StepId, JobStatus.Completed);
            await SeedChildRowAsync(jobStore, runId, group.Steps[1].StepId, JobStatus.Completed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "two prior-Completed children satisfy the WaitMajority quorum");
            Ran.Should().NotContain(group.Steps[0].StepId, "a quorum member with a prior row is not re-run");
            Ran.Should().NotContain(group.Steps[1].StepId, "a quorum member with a prior row is not re-run");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Trigger_ProbeNeverConsulted_ResumeConsults()
    {
        ResetRan();
        CountingShadowProbe? counting = null;
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<ChildAJob>();
                b.AddJob<ChildBJob>();
                b.AddBatch("groupskip.triggersilence", x => x
                    .ThenInParallel(g => g.RunJob<ChildAJob>().RunJob<ChildBJob>()));
            },
            services =>
            {
                // Wrap the real probe in a counting decorator so both silence AND consultation are provable.
                services.RemoveAll<IResumeShadowProbe>();
                services.AddSingleton<IResumeShadowProbe>(sp =>
                {
                    counting = new CountingShadowProbe(new ResumeShadowProbe(sp.GetRequiredService<IJobExecutionReader>()));
                    return counting;
                });
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("groupskip.triggersilence")!;

            // Materialize the DI registration up front (the trigger path never resolves it).
            _ = host.Services.GetRequiredService<IResumeShadowProbe>();

            // Plain trigger: the probe must never be queried, and each child dispatches exactly once.
            var triggerRunId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var triggerRun = await AwaitRunTerminalAsync(runStore, triggerRunId);
            triggerRun.Status.Should().Be(JobStatus.Completed);

            counting!.Queries.Should().Be(0,
                "the trigger path binds no resume probe, so the first-pass child dispatch never consults it");
            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = triggerRunId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Should().HaveCount(2, "each child dispatched exactly once on the trigger path");

            // Teeth: a RESUME of an in-progress run DOES consult the probe (once per local child).
            var resumeRunId = IdNew();
            await SeedInProgressRunAsync(runStore, resumeRunId, def, stepCount: 2);
            await runner.ResumeBatchAsync(resumeRunId, ResumePolicy.ResumeForward, CancellationToken.None);
            (await AwaitRunTerminalAsync(runStore, resumeRunId)).Status.Should().Be(JobStatus.Completed);

            counting.Queries.Should().BeGreaterThanOrEqualTo(2,
                "the resume path binds the probe and consults it for every local child");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
