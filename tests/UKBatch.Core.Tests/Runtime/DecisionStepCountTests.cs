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
/// The run record's <c>StepCount</c> is the topology number the resume/retry drift tripwire compares against.
/// A decision counts as its BRANCH count (one execution row per branch — the winner plus the skipped rest),
/// mirroring the parallel-group-child precedent. This pins the count value and proves the tripwire does not
/// false-fire on an unchanged definition across a resume (the stored count equals the re-computed count).
/// </summary>
public class DecisionStepCountTests
{
    private static readonly ConcurrentQueue<string> Ran = new();
    private static void ResetRan() => Ran.Clear();

    public sealed class RecordingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
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

    [Fact]
    public async Task StepCount_CountsDecisionAsBranchCount()
    {
        ResetRan();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("count.decisiononly", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<RecordingJob>()
                    .When("tier", ConditionOperator.Equals, "silver").RunJob<RecordingJob>()
                    .Otherwise().RunJob<RecordingJob>()));   // 3 branches
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("count.decisiononly")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(runStore, runId);

            run.StepCount.Should().Be(3, "a decision counts as its branch count (winner + skipped rest), not as one step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task StepCount_AddingADecision_ChangesTheCount()
    {
        ResetRan();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("count.jobonly", x => x.RunJob<RecordingJob>());
            b.AddBatch("count.jobplusdecision", x => x
                .RunJob<RecordingJob>()
                .ThenDecide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<RecordingJob>()
                    .Otherwise().RunJob<RecordingJob>()));   // 1 job + 2 branches = 3
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();

            var jobOnlyRun = await AwaitRunTerminalAsync(runStore,
                await runner.TriggerBatchAsync(lookup.TryGetByName("count.jobonly")!.Id, null, "tester", default));
            var jobPlusDecisionRun = await AwaitRunTerminalAsync(runStore,
                await runner.TriggerBatchAsync(lookup.TryGetByName("count.jobplusdecision")!.Id, null, "tester", default));

            jobOnlyRun.StepCount.Should().Be(1, "a single job step counts as one");
            jobPlusDecisionRun.StepCount.Should().Be(3,
                "adding a 2-branch decision after the job raises the topology count from 1 to 3");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task StepCountTripwire_UnchangedDefinition_DoesNotFalseFire_OnResume()
    {
        // A resume compares the run's stored StepCount to the definition's re-computed count; a mismatch
        // degrades ResumeForward to RestartAll. Trigger the batch to learn the authoritative StepCount, then
        // seed a fresh in-progress run of the SAME definition with that count and a cursor PAST the decision.
        // Because the stored count matches the re-computation (no drift), ResumeForward is honored and the
        // decision is not re-entered — a false-fire would degrade to RestartAll and re-run the branch.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("count.tripwire", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<RecordingJob>()
                    .Otherwise().RunJob<RecordingJob>())
                .ThenRunJob<RecordingJob>());   // decision (2 branches) + downstream = StepCount 3
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("count.tripwire")!;
            var branches = def.Steps[0].Decision!.Branches;
            var downstreamId = def.Steps[1].StepId;

            // Learn the authoritative StepCount the creation path stamps.
            var freshRun = await AwaitRunTerminalAsync(runStore, await runner.TriggerBatchAsync(def.Id, null, "tester", default));
            freshRun.StepCount.Should().Be(3, "decision (2 branches) + downstream = 3");

            // Seed a NEW in-progress run with that exact count and the decision already completed (cursor 1).
            ResetRan();
            var runId = IdNew();
            await runStore.CreateAsync(new BatchRun
            {
                BatchId = runId,
                BatchDefinitionId = def.Id,
                BatchName = def.Name,
                Status = null,
                TriggeredBy = "tester",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = null,
                StepCount = freshRun.StepCount,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Cancelled = 0,
            }, CancellationToken.None);
            await runStore.UpdateCursorAsync(runId, 1, CancellationToken.None);
            var internalStore = (IJobStoreInternal)jobStore;
            foreach (var (stepId, status) in new[] { (branches[0].StepId, JobStatus.Completed), (branches[1].StepId, JobStatus.Skipped) })
            {
                await internalStore.InsertAsync(new JobExecution
                {
                    ExecutionId = IdNew(),
                    JobName = "SeededBranch",
                    BatchId = runId,
                    BatchStepId = stepId,
                    BatchDefinitionId = null,
                    Status = status,
                    Parameters = new Dictionary<string, object?>(),
                    EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
                    StartedAtUtc = status == JobStatus.Skipped ? null : DateTimeOffset.UtcNow.AddMinutes(-5),
                    CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
                    AttemptNumber = 1,
                    MaxRetries = 0,
                    LastError = null,
                    Processed = 0,
                    Failed = 0,
                    Total = null,
                    TriggeredBy = "tester",
                    WorkerName = null,
                }, CancellationToken.None);
            }

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);
            var resumedRun = await AwaitRunTerminalAsync(runStore, runId);

            resumedRun.Status.Should().Be(JobStatus.Completed);
            Ran.Should().ContainSingle().Which.Should().Be(downstreamId,
                "with no drift, ResumeForward is honored: only the downstream step runs and the decision is not re-entered");
            Ran.Should().NotContain(branches[0].StepId, "a false-firing tripwire would degrade to RestartAll and re-run the branch");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
