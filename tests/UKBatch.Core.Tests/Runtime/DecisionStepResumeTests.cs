using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
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
/// Durable-resume idempotency of a <see cref="BatchStepType.Decision"/> step. A fully-completed decision is
/// skipped entirely on a resume-forward. A resume that re-enters the decision re-evaluates deterministically
/// (the forwarded outputs are rehydrated, so the SAME branch wins), does NOT re-run a winner that already
/// completed before the crash (it reuses the winner's persisted outputs), does NOT re-record losers already
/// skipped, and DOES re-run a winner whose only prior row is a reaper-tombstoned Failed orphan (which does not
/// prove completion). The probe is consulted only on the resume path, so the plain trigger path stays
/// byte-for-byte.
/// </summary>
public class DecisionStepResumeTests
{
    /// <summary>Records every branch/step that actually ran, by its step id, with a fresh signal per test.</summary>
    private static readonly ConcurrentQueue<string> Ran = new();
    private static void ResetRan() => Ran.Clear();

    /// <summary>A branch winner that records its step id and emits a "shipped" output for the downstream step.</summary>
    public sealed class ExpressJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("shipped", "fresh-express");
            return Task.CompletedTask;
        }
    }

    /// <summary>The else-branch job (records its step id).</summary>
    public sealed class StandardJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
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

    private static string IdNew() => Guid.NewGuid().ToString("N");

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

    /// <summary>Seeds an in-progress run (Status null) at the given cursor with the supplied step count and forwarded state.</summary>
    private static async Task SeedInProgressRunAsync(
        IBatchRunStore runStore, string runId, BatchDefinition def, int cursor, int stepCount,
        IReadOnlyDictionary<string, object?> forwardedState)
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
        await runStore.UpdateCursorAsync(runId, cursor, CancellationToken.None);
        await runStore.UpdateForwardedStateAsync(runId, forwardedState, CancellationToken.None);
    }

    /// <summary>Inserts a terminal shadow execution row for a (run, branch) into the in-memory store.</summary>
    private static Task SeedBranchRowAsync(
        IJobStore jobStore, string runId, string branchStepId, JobStatus status,
        IReadOnlyDictionary<string, object?>? outputs = null)
    {
        var internalStore = (IJobStoreInternal)jobStore;
        var now = DateTimeOffset.UtcNow;
        return internalStore.InsertAsync(new JobExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            JobName = "SeededBranch",
            BatchId = runId,
            BatchStepId = branchStepId,
            BatchDefinitionId = null,
            Status = status,
            Parameters = new Dictionary<string, object?>(),
            Outputs = outputs,
            EnqueuedAtUtc = now.AddMinutes(-5),
            StartedAtUtc = status == JobStatus.Skipped ? null : now.AddMinutes(-5),
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

    /// <summary>
    /// Starts a two-step batch: a decision (branch 0 = <see cref="ExpressJob"/> when <c>tier==gold</c>, else
    /// <see cref="StandardJob"/>) followed by a capturing downstream step. Its topology count is 3 (two branch
    /// jobs + one downstream), which the seeds use so the resume drift guard does not trip.
    /// </summary>
    private static async Task<IHost> StartDecisionThenDownstreamHostAsync(string name)
    {
        ResetRan();
        DownstreamCaptureJob.Reset();
        return await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ExpressJob>();
            b.AddJob<StandardJob>();
            b.AddJob<DownstreamCaptureJob>();
            b.AddBatch(name, x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<ExpressJob>()
                    .Otherwise().RunJob<StandardJob>())
                .ThenRunJob<DownstreamCaptureJob>());
        });
    }

    [Fact]
    public async Task ResumeForward_CompletedDecision_SkippedEntirely()
    {
        // The decision (index 0) already completed before the crash (cursor advanced to 1). ResumeForward must
        // run only the downstream step; the decision is never re-entered and no branch re-runs or re-records.
        var host = await StartDecisionThenDownstreamHostAsync("decision.resume.forward");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.resume.forward")!;
            var branches = def.Steps[0].Decision!.Branches;
            var winnerId = branches[0].StepId;
            var loserId = branches[1].StepId;

            var runId = IdNew();
            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] = new Dictionary<string, object?> { ["tier"] = "gold" },
                [ForwardedStateKeys.ForwardedOutputs] = new Dictionary<string, object?> { ["shipped"] = "fresh-express" },
            };
            // Cursor 1 → the decision completed. Its winner + loser rows already exist from the prior attempt.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 3, forwardedState);
            await SeedBranchRowAsync(jobStore, runId, winnerId, JobStatus.Completed,
                outputs: new Dictionary<string, object?> { ["shipped"] = "fresh-express" });
            await SeedBranchRowAsync(jobStore, runId, loserId, JobStatus.Skipped);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            Ran.Should().BeEmpty("a fully-completed decision is skipped entirely on resume-forward; no branch re-runs");

            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Count(r => r.BatchStepId == winnerId).Should().Be(1, "the decision is not re-entered — the winner row is untouched");
            rows.Count(r => r.BatchStepId == loserId).Should().Be(1, "no duplicate skipped row is written on resume-forward");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeIntoDecision_WinnerAlreadyCompleted_NotReRun_LosersNotReRecorded()
    {
        // The crash landed while inside the decision (cursor still 0), AFTER the winner completed. On resume the
        // decision re-evaluates to the same branch, the completed winner is reused (NOT re-dispatched), and the
        // loser already skipped is NOT re-recorded — no duplicate rows, no double financial run.
        var host = await StartDecisionThenDownstreamHostAsync("decision.resume.into.completed");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.resume.into.completed")!;
            var branches = def.Steps[0].Decision!.Branches;
            var winnerId = branches[0].StepId;
            var loserId = branches[1].StepId;

            var runId = IdNew();
            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] = new Dictionary<string, object?> { ["tier"] = "gold" },
            };
            // Cursor 0 → the decision itself did not complete. The winner's Completed row (with outputs) and the
            // loser's Skipped row already exist from the interrupted attempt.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0, stepCount: 3, forwardedState);
            await SeedBranchRowAsync(jobStore, runId, winnerId, JobStatus.Completed,
                outputs: new Dictionary<string, object?> { ["shipped"] = "prior-express" });
            await SeedBranchRowAsync(jobStore, runId, loserId, JobStatus.Skipped);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            Ran.Should().NotContain(winnerId, "a winner proven Completed before the crash is reused, not re-dispatched");

            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Count(r => r.BatchStepId == winnerId).Should().Be(1, "the completed winner is reused — no new execution row");
            rows.Count(r => r.BatchStepId == loserId).Should().Be(1, "a loser already skipped is not re-recorded (no duplicate Skipped row)");

            DownstreamCaptureJob.Captured!.GetRequired<string>("shipped").Should().Be("prior-express",
                "the reused winner's persisted outputs still forward to the downstream step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeIntoDecision_WinnerReapedFailed_ReRuns()
    {
        // The winner's only prior row is a reaper-tombstoned Failed orphan — it does NOT prove the branch
        // finished. Resume must re-run the winner (the at-least-once replay), while the loser already skipped
        // is still not re-recorded.
        var host = await StartDecisionThenDownstreamHostAsync("decision.resume.into.reaped");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.resume.into.reaped")!;
            var branches = def.Steps[0].Decision!.Branches;
            var winnerId = branches[0].StepId;
            var loserId = branches[1].StepId;

            var runId = IdNew();
            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] = new Dictionary<string, object?> { ["tier"] = "gold" },
            };
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0, stepCount: 3, forwardedState);
            await SeedBranchRowAsync(jobStore, runId, winnerId, JobStatus.Failed);       // ambiguous orphan
            await SeedBranchRowAsync(jobStore, runId, loserId, JobStatus.Skipped);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            Ran.Should().Contain(winnerId, "an ambiguous Failed orphan does not prove completion — the winner re-runs");
            Ran.Should().NotContain(loserId, "the else branch is still the loser and never runs");

            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Count(r => r.BatchStepId == loserId).Should().Be(1, "the loser already skipped is not re-recorded on resume");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeIntoDecision_RehydratedForwardedOutput_SameBranchWins()
    {
        // Determinism: the decision routes on an EARLIER step's forwarded output. On resume that output is
        // rehydrated from the run's forwarded state, so the SAME branch wins. Here the forwarded amount selects
        // the conditional branch; if the forwarded state were NOT rehydrated the amount would be absent and the
        // else branch would win instead — re-running the wrong job and re-recording the wrong loser.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ExpressJob>();
            b.AddJob<StandardJob>();
            b.AddBatch("decision.resume.deterministic", x => x
                .Decide(d => d
                    .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<ExpressJob>()
                    .Otherwise().RunJob<StandardJob>()));
        });
        try
        {
            ResetRan();
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.resume.deterministic")!;
            var branches = def.Steps[0].Decision!.Branches;
            var conditionalId = branches[0].StepId;   // wins only when amount > 1000 is visible
            var elseId = branches[1].StepId;

            var runId = IdNew();
            var forwardedState = new Dictionary<string, object?>
            {
                // amount lives among the forwarded outputs (as if an earlier step produced it).
                [ForwardedStateKeys.ForwardedOutputs] = new Dictionary<string, object?> { ["amount"] = 5000 },
            };
            // Cursor 0 → re-enter the decision. The conditional branch already completed; the else was skipped.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0, stepCount: 2, forwardedState);
            await SeedBranchRowAsync(jobStore, runId, conditionalId, JobStatus.Completed);
            await SeedBranchRowAsync(jobStore, runId, elseId, JobStatus.Skipped);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            Ran.Should().NotContain(elseId,
                "the rehydrated forwarded amount (5000 > 1000) re-selects the conditional branch, so the else never runs");
            Ran.Should().NotContain(conditionalId, "the re-selected winner already completed, so it is reused rather than re-run");

            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            rows.Count(r => r.BatchStepId == elseId).Should().Be(1,
                "the else stays the loser: no second Skipped row, and it is not promoted to a run");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
