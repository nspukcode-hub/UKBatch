using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Saga (reverse-unwind) semantics of a <see cref="BatchStepType.Decision"/> step under
/// <see cref="BatchFailurePolicy.Compensate"/>. A decision is ONE compensation unit: a decision-level
/// compensator runs when a LATER step fails (undoing whichever branch won), the winning branch's job is never
/// individually compensated, and the losing branches (which never ran) are never compensated. When the
/// decision itself is the failed step (its winner failed) it is not compensated — only earlier steps unwind.
/// A decision skipped as a whole by its own run-if condition is excluded from the unwind, exactly like any
/// other skipped step.
/// </summary>
public class DecisionStepSagaTests
{
    private static readonly ConcurrentQueue<string> Sequence = new();
    private static void ResetSequence() => Sequence.Clear();

    /// <summary>Returns the recorded entries that are compensator dispatches (derived ":comp" ids).</summary>
    private static List<string> CompensatorEntries()
        => Sequence.Where(id => id.EndsWith(CompensationStepIds.Suffix, StringComparison.Ordinal)).ToList();

    /// <summary>A main/branch step that succeeds and records its step id.</summary>
    public sealed class OkStepJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A registered job that always throws, forcing a Failed terminal on its step.</summary>
    public sealed class FailingStepJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional step failure");
    }

    /// <summary>A compensator that succeeds and records its (derived) step id.</summary>
    public sealed class CompProbeJob : IJob
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

    [Fact]
    public async Task DecisionLevelCompensator_RunsWhenLaterStepFails_BranchNotIndividuallyCompensated()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("decision.saga.compunit", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<OkStepJob>()
                    .Otherwise().RunJob<OkStepJob>()
                    .CompensateWith<CompProbeJob>())   // decision-level compensator
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.saga.compunit")!;
            var decisionStepId = def.Steps[0].StepId;
            var branches = def.Steps[0].Decision!.Branches;
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "gold" });

            var act = async () => await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>("a compensated batch still rethrows the original failure");

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(decisionStepId) },
                "the decision compensates exactly ONCE as one unit, under the DECISION step's derived id — " +
                "not under the winning branch's id, and not once per branch");
            Sequence.Should().NotContain(CompensationStepIds.For(branches[0].StepId),
                "the winning branch's job is never individually compensated");
            Sequence.Should().NotContain(CompensationStepIds.For(branches[1].StepId),
                "the losing branch (which never ran) is never compensated");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task WinnerFails_DecisionIsFailedStep_NotCompensated_EarlierStepUnwinds()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("decision.saga.winnerfailed", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                // The winning branch fails, so the decision IS the failed step: it owns its partial rollback and
                // must never be compensated — only the earlier completed step unwinds.
                .ThenDecide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<FailingStepJob>()
                    .Otherwise().RunJob<OkStepJob>()
                    .CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.saga.winnerfailed")!;
            var okStepId = def.Steps[0].StepId;
            var decisionStepId = def.Steps[1].StepId;
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "gold" });

            var act = async () => await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(okStepId) },
                "only the earlier COMPLETED step is compensated; the decision that IS the failed step is not");
            Sequence.Should().NotContain(CompensationStepIds.For(decisionStepId),
                "the failed decision owns its partial rollback — its own compensator never runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task WholeDecisionSkippedByRunIf_IsNotCompensated_DuringUnwind()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("decision.saga.skipped", x => x
                // The whole decision is skipped by its own run-if (ship absent → IsTrue false), so its
                // compensator must NEVER run — a skipped step never ran, so there is nothing to undo.
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<OkStepJob>()
                    .Otherwise().RunJob<OkStepJob>()
                    .CompensateWith<CompProbeJob>()
                    .RunIf("ship", ConditionOperator.IsTrue))
                // A step that DOES run and carries a compensator that SHOULD run on the later failure.
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.saga.skipped")!;
            var decisionStepId = def.Steps[0].StepId;
            var ranStepId = def.Steps[1].StepId;
            var branches = def.Steps[0].Decision!.Branches;

            // "ship" is absent → IsTrue is false → the whole decision is skipped.
            var act = async () => await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            Sequence.Should().NotContain(branches[0].StepId, "no branch of a skipped decision runs");
            Sequence.Should().NotContain(branches[1].StepId);
            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(ranStepId) },
                "only the step that actually ran is compensated; the skipped decision's compensator must not run");
            Sequence.Should().NotContain(CompensationStepIds.For(decisionStepId),
                "a decision skipped as a whole is excluded from the unwind");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
