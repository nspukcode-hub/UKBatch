using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Run-if conditions in the executor: a satisfied condition runs the step; an unmet one skips it (recorded
/// as <see cref="JobStatus.Skipped"/>, producing no forwarded output), and a skipped step is never
/// compensated during a saga unwind. A parallel group is skipped as one unit.
/// </summary>
public class BatchExecutorConditionTests
{
    private static readonly ConcurrentQueue<string> Sequence = new();
    private static void ResetSequence() => Sequence.Clear();

    private static List<string> CompensatorEntries()
        => Sequence.Where(id => id.EndsWith(CompensationStepIds.Suffix, StringComparison.Ordinal)).ToList();

    /// <summary>A step that succeeds and records its step id; can emit an output.</summary>
    public sealed class RecordingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A step that records its id and emits an "amount" output for downstream conditions.</summary>
    public sealed class EmitAmountJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("amount", 500);
            return Task.CompletedTask;
        }
    }

    /// <summary>A step that captures the parameters it received (to prove a skipped step's output is absent).</summary>
    public sealed class CapturingJob : IJob
    {
        public static JobParameters? Captured;
        public static void Reset() => Captured = null;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            Captured = context.Parameters;
            return Task.CompletedTask;
        }
    }

    public sealed class FailingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional step failure");
    }

    public sealed class CompJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    private static BatchExecutor BuildExecutor(IHost host)
        => new(
            host.Services.GetRequiredService<IJobRunnerInternal>(),
            host.Services.GetRequiredService<IApprovalGateCoordinator>(),
            host.Services.GetRequiredService<IJobExecutionAwaiter>(),
            host.Services.GetRequiredService<ITransport>(),
            thisServiceName: null,
            host.Services.GetRequiredService<TimeProvider>(),
            host.Services.GetRequiredService<ILogger<BatchExecutor>>());

    private static string IdNew() => Guid.NewGuid().ToString("N");

    private static async Task<IReadOnlyList<JobExecution>> RowsAsync(IHost host, string runId)
    {
        var reader = host.Services.GetRequiredService<IJobExecutionReader>();
        return await reader.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
    }

    [Fact]
    public async Task Condition_Satisfied_StepRuns()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("cond.true", x => x
                .RunJob<RecordingJob>(s => s.RunIf("tier", ConditionOperator.Equals, "premium")));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("cond.true")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "premium" });
            await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            Sequence.Should().Contain(def.Steps[0].StepId, "the condition holds, so the step runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Condition_NotSatisfied_StepSkipped_NextStepRuns_SkippedRowRecorded()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("cond.skip", x => x
                .RunJob<RecordingJob>(s => s.RunIf("tier", ConditionOperator.Equals, "premium"))   // tier=basic → skip
                .ThenRunJob<RecordingJob>());                                                        // unconditional → runs
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("cond.skip")!;
            var runId = IdNew();
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "basic" });
            await BuildExecutor(host).RunAsync(def, runId, initial, "tester", CancellationToken.None);

            Sequence.Should().NotContain(def.Steps[0].StepId, "the condition is not met, so step 0 is skipped");
            Sequence.Should().Contain(def.Steps[1].StepId, "the batch proceeds to the next step after a skip");

            var rows = await RowsAsync(host, runId);
            var skippedRow = rows.SingleOrDefault(r => r.BatchStepId == def.Steps[0].StepId);
            skippedRow.Should().NotBeNull("a skipped step records a visible execution row");
            skippedRow!.Status.Should().Be(JobStatus.Skipped);
            rows.Should().Contain(r => r.BatchStepId == def.Steps[1].StepId && r.Status == JobStatus.Completed);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task SkippedStep_IsNotCompensated_DuringUnwind()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddJob<FailingJob>();
            b.AddJob<CompJob>();
            b.AddBatch("cond.saga", x => x
                // step 0: skipped by an unmet condition — its compensator must NEVER run.
                .RunJob<RecordingJob>(s => s.CompensateWith<CompJob>().RunIf("ship", ConditionOperator.IsTrue))
                // step 1: runs unconditionally — its compensator SHOULD run on the later failure.
                .ThenRunJob<RecordingJob>(s => s.CompensateWith<CompJob>())
                // step 2: fails, triggering the reverse unwind.
                .ThenRunJob<FailingJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("cond.saga")!;
            var runId = IdNew();
            // "ship" is absent → IsTrue is false → step 0 is skipped.
            var act = async () => await BuildExecutor(host).RunAsync(def, runId, JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>("a compensated batch still rethrows the original failure");

            Sequence.Should().NotContain(def.Steps[0].StepId, "step 0's condition was not met, so it never ran");
            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[1].StepId) },
                "only the step that actually ran is compensated; the skipped step's compensator must not run");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task SkippedStep_ProducesNoForwardedOutput()
    {
        ResetSequence();
        CapturingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitAmountJob>();
            b.AddJob<CapturingJob>();
            b.AddBatch("cond.noforward", x => x
                // step 0 emits "amount" but is SKIPPED, so "amount" must not reach step 1.
                .RunJob<EmitAmountJob>(s => s.RunIf("run", ConditionOperator.IsTrue))
                .ThenRunJob<CapturingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("cond.noforward")!;
            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            Sequence.Should().NotContain(def.Steps[0].StepId, "step 0 is skipped");
            CapturingJob.Captured.Should().NotBeNull();
            CapturingJob.Captured!.Contains("amount").Should().BeFalse(
                "a skipped step emits no output, so nothing is forwarded to the next step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ParallelGroup_ConditionNotSatisfied_WholeGroupSkipped()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("cond.group", x => x
                .ThenInParallel(g => g
                    .RunJob<RecordingJob>()
                    .RunJob<RecordingJob>()
                    .RunIf("enabled", ConditionOperator.IsTrue))   // absent → whole group skipped
                .ThenRunJob<RecordingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("cond.group")!;
            var runId = IdNew();
            await BuildExecutor(host).RunAsync(def, runId, JobParameters.Empty, "tester", CancellationToken.None);

            var groupStep = def.Steps[0];
            foreach (var child in groupStep.ParallelGroup!.Steps)
            {
                Sequence.Should().NotContain(child.StepId, "no child of a skipped parallel group runs");
            }
            Sequence.Should().Contain(def.Steps[1].StepId, "the step after the skipped group still runs");

            var rows = await RowsAsync(host, runId);
            rows.Should().Contain(r => r.BatchStepId == groupStep.StepId && r.Status == JobStatus.Skipped,
                "the group records a single Skipped row under its own step id");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
