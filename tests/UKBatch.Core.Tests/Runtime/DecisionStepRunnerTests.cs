using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
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
/// Routing semantics of a <see cref="BatchStepType.Decision"/> step: the branches are evaluated in order and
/// the FIRST whose condition holds runs (an else/default branch is the fallback); every other branch is
/// recorded <see cref="JobStatus.Skipped"/> under its own branch id and is never dispatched. When no branch
/// matches and there is no else the decision passes through, forwarding nothing, and the batch proceeds. The
/// winner runs like a Job step — its outputs fold into the run accumulator, it routes off earlier steps'
/// forwarded outputs, a cross-service branch runs through the transport, and a Failed winner routes through
/// the batch failure policy.
/// </summary>
public class DecisionStepRunnerTests
{
    private static readonly ConcurrentQueue<string> Sequence = new();
    private static void ResetSequence() => Sequence.Clear();

    /// <summary>A branch/step job that records the step id it ran under.</summary>
    public sealed class RecordingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A branch winner that records its step id and emits a "shipped" output for the downstream step.</summary>
    public sealed class ShipExpressJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("shipped", "express");
            return Task.CompletedTask;
        }
    }

    /// <summary>An earlier step that emits an "amount" output the decision routes on.</summary>
    public sealed class EmitAmountJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            context.Outputs.Set("amount", 5000);
            return Task.CompletedTask;
        }
    }

    /// <summary>A downstream step that captures the parameters it received (to observe folded outputs).</summary>
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

    /// <summary>A branch winner that always fails, forcing a Failed terminal on the decision step.</summary>
    public sealed class FailingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional branch failure");
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

    private static DecisionStepData Decision(BatchDefinition def) => def.Steps[0].Decision!;

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

    // ===== routing =====

    [Fact]
    public async Task Decision_FirstMatchingBranchWins_EarlierBeatsLaterOverlappingMatch()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("decision.firstmatch", x => x
                .Decide(d => d
                    .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<RecordingJob>()   // branch 0 — also matches
                    .When("amount", ConditionOperator.GreaterThan, 100).RunJob<RecordingJob>()     // branch 1 — also matches
                    .Otherwise().RunJob<RecordingJob>()));                                          // branch 2 — else
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.firstmatch")!;
            var branches = Decision(def).Branches;
            var runId = IdNew();
            var initial = new JobParameters(new Dictionary<string, object?> { ["amount"] = 5000 });
            await BuildExecutor(host).RunAsync(def, runId, initial, "tester", CancellationToken.None);

            Sequence.Should().ContainSingle().Which.Should().Be(branches[0].StepId,
                "both branch 0 and branch 1 conditions hold, but the FIRST matching branch wins");
            Sequence.Should().NotContain(branches[1].StepId, "a later branch that also matches does not run");
            Sequence.Should().NotContain(branches[2].StepId, "the else branch does not run when an earlier condition holds");

            var rows = await RowsAsync(host, runId);
            rows.Should().Contain(r => r.BatchStepId == branches[1].StepId && r.Status == JobStatus.Skipped, "branch 1 is a loser → skipped");
            rows.Should().Contain(r => r.BatchStepId == branches[2].StepId && r.Status == JobStatus.Skipped, "the else branch is a loser → skipped");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_ElseBranchWins_WhenNoConditionMatches()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("decision.elsewins", x => x
                .Decide(d => d
                    .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<RecordingJob>()
                    .When("amount", ConditionOperator.GreaterThan, 500).RunJob<RecordingJob>()
                    .Otherwise().RunJob<RecordingJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.elsewins")!;
            var branches = Decision(def).Branches;
            var initial = new JobParameters(new Dictionary<string, object?> { ["amount"] = 50 });
            await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            Sequence.Should().ContainSingle().Which.Should().Be(branches[2].StepId,
                "no conditional branch matches, so the else/default branch wins");
            Sequence.Should().NotContain(branches[0].StepId);
            Sequence.Should().NotContain(branches[1].StepId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_NoMatchNoElse_AllBranchesSkipped_PassesThrough_NextStepRuns()
    {
        ResetSequence();
        CapturingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddJob<CapturingJob>();
            b.AddBatch("decision.passthrough", x => x
                .Decide(d => d
                    .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<RecordingJob>()
                    .When("amount", ConditionOperator.GreaterThan, 500).RunJob<RecordingJob>())   // no else
                .ThenRunJob<CapturingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.passthrough")!;
            var branches = Decision(def).Branches;
            var downstreamId = def.Steps[1].StepId;
            var runId = IdNew();
            var initial = new JobParameters(new Dictionary<string, object?> { ["amount"] = 100 });
            await BuildExecutor(host).RunAsync(def, runId, initial, "tester", CancellationToken.None);

            Sequence.Should().NotContain(branches[0].StepId, "no branch matches and there is no else — nothing runs");
            Sequence.Should().NotContain(branches[1].StepId);
            Sequence.Should().Contain(downstreamId, "the decision passes through and the batch proceeds to the next step");

            var rows = await RowsAsync(host, runId);
            rows.Should().Contain(r => r.BatchStepId == branches[0].StepId && r.Status == JobStatus.Skipped);
            rows.Should().Contain(r => r.BatchStepId == branches[1].StepId && r.Status == JobStatus.Skipped);

            CapturingJob.Captured!.Contains("shipped").Should().BeFalse(
                "a pass-through decision forwards nothing to the next step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_Losers_RecordedSkipped_KeyedByBranchStepId_Terminal()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("decision.losers", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<RecordingJob>()
                    .Otherwise().RunJob<RecordingJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.losers")!;
            var branches = Decision(def).Branches;
            var runId = IdNew();
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "gold" });
            await BuildExecutor(host).RunAsync(def, runId, initial, "tester", CancellationToken.None);

            var rows = await RowsAsync(host, runId);
            var loserRow = rows.SingleOrDefault(r => r.BatchStepId == branches[1].StepId);
            loserRow.Should().NotBeNull("every losing branch records a visible execution row keyed by its branch id");
            loserRow!.Status.Should().Be(JobStatus.Skipped);
            JobStatusTransitions.IsTerminal(loserRow.Status).Should().BeTrue(
                "a Skipped row is terminal, so the orphan reaper never reaps it");

            var winnerRow = rows.SingleOrDefault(r => r.BatchStepId == branches[0].StepId);
            winnerRow.Should().NotBeNull();
            winnerRow!.Status.Should().Be(JobStatus.Completed, "the winner produces its normal completed row");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_WinnerOutputs_FoldForward_LaterStepSees()
    {
        ResetSequence();
        CapturingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ShipExpressJob>();
            b.AddJob<RecordingJob>();
            b.AddJob<CapturingJob>();
            b.AddBatch("decision.fold", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<ShipExpressJob>()
                    .Otherwise().RunJob<RecordingJob>())
                .ThenRunJob<CapturingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.fold")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "gold" });
            await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            CapturingJob.Captured.Should().NotBeNull("the downstream step ran after the decision");
            CapturingJob.Captured!.GetRequired<string>("shipped").Should().Be("express",
                "the winning branch's outputs fold into the run accumulator and reach the next step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_RoutesOnForwardedOutput_FromEarlierStep()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitAmountJob>();
            b.AddJob<RecordingJob>();
            b.AddBatch("decision.routesonoutput", x => x
                // step 0 emits amount=5000; the decision routes on that forwarded output (no trigger param).
                .RunJob<EmitAmountJob>()
                .ThenDecide(d => d
                    .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<RecordingJob>()   // wins ONLY if the output is read
                    .Otherwise().RunJob<RecordingJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.routesonoutput")!;
            var branches = def.Steps[1].Decision!.Branches;
            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            Sequence.Should().Contain(branches[0].StepId,
                "the decision reads the earlier step's forwarded amount (5000 > 1000), so the conditional branch wins");
            Sequence.Should().NotContain(branches[1].StepId,
                "the else branch would only win if the forwarded output were NOT read");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_LocalWinnerFails_RoutesThroughFailurePolicy_StopRethrows()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<FailingJob>();
            b.AddJob<RecordingJob>();
            b.AddBatch("decision.winnerfails", x => x
                .Decide(d => d
                    .When("tier", ConditionOperator.Equals, "gold").RunJob<FailingJob>()
                    .Otherwise().RunJob<RecordingJob>())
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.winnerfails")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["tier"] = "gold" });
            var act = async () => await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            await act.Should().ThrowAsync<BatchStepFailureException>(
                "a Failed decision winner throws a step-failure that StopOnFailure rethrows");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== cross-service winner =====

    private static ITransport SubstituteTransport(JobStatus replyStatus)
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-exec",
                Status = replyStatus,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        return transport;
    }

    /// <summary>Starts a host whose decision routes to a cross-service branch (RemoteJob on "billing") or a local else.</summary>
    private static Task<IHost> StartCrossServiceDecisionHostAsync(string name, ITransport transport)
        => TestHostBuilder.StartAsync(
            b =>
            {
                b.Configure(o => o.ThisServiceName = "orchestrator");
                b.AddJob<RecordingJob>();
                b.AddBatch(name, x => x
                    .Decide(d => d
                        .When("region", ConditionOperator.Equals, "EU").RunJob("RemoteJob", step => step.OnService("billing"))
                        .Otherwise().RunJob<RecordingJob>()));
            },
            services =>
            {
                services.RemoveAll<ITransport>();
                services.AddSingleton(transport);
            });

    [Fact]
    public async Task Decision_CrossServiceWinner_RunsViaTransport_LoserSkipped()
    {
        ResetSequence();
        var transport = SubstituteTransport(JobStatus.Completed);
        var host = await StartCrossServiceDecisionHostAsync("decision.cross.win", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.cross.win")!;
            var elseBranchId = Decision(def).Branches[1].StepId;

            var runId = await runner.TriggerBatchAsync(
                def.Id, new JobParameters(new Dictionary<string, object?> { ["region"] = "EU" }), "tester", default);
            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            await transport.Received(1).RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            Sequence.Should().NotContain(elseBranchId, "the local else branch is the loser — it never dispatches");

            var rows = await RowsAsync(host, runId);
            rows.Should().Contain(r => r.BatchStepId == elseBranchId && r.Status == JobStatus.Skipped,
                "the losing local branch is recorded skipped");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Decision_CrossServiceWinner_Failed_FailsBatch()
    {
        ResetSequence();
        var transport = SubstituteTransport(JobStatus.Failed);
        var host = await StartCrossServiceDecisionHostAsync("decision.cross.fail", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("decision.cross.fail")!;

            var runId = await runner.TriggerBatchAsync(
                def.Id, new JobParameters(new Dictionary<string, object?> { ["region"] = "EU" }), "tester", default);
            var run = await AwaitRunTerminalAsync(runStore, runId);

            run.Status.Should().Be(JobStatus.Failed, "a Failed cross-service winner fails the decision step per StopOnFailure");
            await transport.Received(1).RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
