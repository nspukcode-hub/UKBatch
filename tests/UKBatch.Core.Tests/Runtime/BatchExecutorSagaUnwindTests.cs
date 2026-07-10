using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Reverse-unwind (saga) semantics of per-step compensators under
/// <see cref="BatchFailurePolicy.Compensate"/>: compensators of COMPLETED steps run in reverse order,
/// the failed step's own compensator never runs, steps without a compensator are skipped, a throwing
/// compensator is logged and the unwind continues, the unwind fully precedes the batch-level
/// OnFailureSteps chain, a compensator receives the merged forwarded state, a parallel group
/// compensates as ONE unit, and a definition with neither compensators nor a chain degrades to
/// StopOnFailure with zero compensation-cursor writes. Cancellation stops the unwind: an
/// administrative cancel ends the run Cancelled; a graceful host shutdown leaves it in-flight with
/// the unwind cursor persisted.
/// </summary>
public class BatchExecutorSagaUnwindTests
{
    /// <summary>Every probe job appends its step id here, giving one global dispatch order per test.</summary>
    private static readonly ConcurrentQueue<string> Sequence = new();
    private static void ResetSequence() => Sequence.Clear();

    /// <summary>Returns the recorded entries that are compensator dispatches (derived ":comp" ids).</summary>
    private static List<string> CompensatorEntries()
        => Sequence.Where(id => id.EndsWith(CompensationStepIds.Suffix, StringComparison.Ordinal)).ToList();

    /// <summary>A main step that succeeds and records its step id.</summary>
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

    /// <summary>A compensator that records its step id, then throws.</summary>
    public sealed class ThrowingCompJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            throw new InvalidOperationException("intentional compensator failure");
        }
    }

    /// <summary>A failure-chain step that succeeds and records its step id.</summary>
    public sealed class ChainProbeJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>An output-emitting main step, for the forwarded-state merge test.</summary>
    public sealed class EmitOutputJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("out", "fromA");
            context.Outputs.Set("shared", "from-output");
            return Task.CompletedTask;
        }
    }

    /// <summary>A compensator that captures the parameter set it received.</summary>
    public sealed class CapturingCompJob : IJob
    {
        public static JobParameters? Captured;
        public static void Reset() => Captured = null;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured = context.Parameters;
            return Task.CompletedTask;
        }
    }

    /// <summary>A compensator that signals it started, then parks on its execution token.</summary>
    public sealed class ParkingCompJob : IJob
    {
        public static TaskCompletionSource Entered { get; private set; } = NewSignal();
        public static void Reset() => Entered = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A transparent spy over the real in-memory run store that records every compensation-cursor
    /// write, so tests can assert both the write sequence and its complete absence.
    /// </summary>
    private sealed class CursorSpyRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public ConcurrentQueue<int?> CompensationCursorWrites { get; } = new();

        public Task CreateAsync(BatchRun run, CancellationToken cancellationToken) => _inner.CreateAsync(run, cancellationToken);
        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
            => _inner.CompleteAsync(batchId, terminalStatus, counts, completedAtUtc, cancellationToken);
        public Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken cancellationToken)
            => _inner.UpdateCursorAsync(batchId, nextStepIndex, cancellationToken);
        public Task UpdateForwardedStateAsync(string batchId, IReadOnlyDictionary<string, object?> state, CancellationToken cancellationToken)
            => _inner.UpdateForwardedStateAsync(batchId, state, cancellationToken);
        public Task UpdateCompensationCursorAsync(string batchId, int? compensationStepIndex, CancellationToken cancellationToken)
        {
            CompensationCursorWrites.Enqueue(compensationStepIndex);
            return _inner.UpdateCompensationCursorAsync(batchId, compensationStepIndex, cancellationToken);
        }
        public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken) => _inner.GetAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);
    }

    /// <summary>Constructs the executor directly from the host's real internal seams (trigger-shaped, probe-less).</summary>
    private static BatchExecutor BuildExecutor(IHost host, Func<int, CancellationToken, Task>? onCompensationProgress = null)
        => new(
            host.Services.GetRequiredService<IJobRunnerInternal>(),
            host.Services.GetRequiredService<IApprovalGateCoordinator>(),
            host.Services.GetRequiredService<IJobExecutionAwaiter>(),
            host.Services.GetRequiredService<ITransport>(),
            thisServiceName: null,
            host.Services.GetRequiredService<TimeProvider>(),
            host.Services.GetRequiredService<ILogger<BatchExecutor>>(),
            onStepCompleted: null,
            onCompensationProgress: onCompensationProgress);

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

    private static string IdNew() => Guid.NewGuid().ToString("N");

    // ===== reverse-unwind ordering =====

    [Fact]
    public async Task Unwind_ReverseOrder_ThreeCompletedStepsFailAtFourth_RunsCompC_CompB_CompA()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.reverse", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.reverse")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>("a compensated batch still rethrows the original failure");

            CompensatorEntries().Should().Equal(
                new[]
                {
                    CompensationStepIds.For(def.Steps[2].StepId),
                    CompensationStepIds.For(def.Steps[1].StepId),
                    CompensationStepIds.For(def.Steps[0].StepId),
                },
                "compensators run in reverse order of the completed steps: most recently completed first");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_SkipsStepsWithoutCompensator_NoRowNoCursorWrite()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.skip.nocomp", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>()   // no compensator — "cannot be undone", skipped by the unwind
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.skip.nocomp")!;
            var cursorWrites = new ConcurrentQueue<int>();
            var executor = BuildExecutor(host, (index, _) =>
            {
                cursorWrites.Enqueue(index);
                return Task.CompletedTask;
            });

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[0].StepId) },
                "only the compensator-bearing completed step is unwound");
            cursorWrites.Should().Equal(new[] { 2, 0, 0 },
                "marker at the failed index (2), then the cursor after the only compensator (0), then the chain marker (0) — " +
                "no write for the compensator-less step at index 1");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_DoesNotCompensateTheFailedStep()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.failedstep.owncomp", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                // The failing step carries its OWN compensator — it must never run: a step that failed
                // part-way owns its partial rollback; the saga undoes only whole completed steps.
                .ThenRunJob<FailingStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.failedstep.owncomp")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[0].StepId) },
                "only the COMPLETED step is compensated; the failed step's own compensator never runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_FailAtIndexZero_NoUnwind_RunsChainOnly()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<FailingStepJob>();
            b.AddJob<OkStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ChainProbeJob>();
            b.AddBatch("saga.failatzero", x => x
                .RunJob<FailingStepJob>()
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<ChainProbeJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.failatzero")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().BeEmpty("no step completed before the failure, so there is nothing to unwind");
            Sequence.Should().Contain(def.OnFailureSteps[0].StepId, "the batch-level failure chain still runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_CompensatorThrows_LoggedAndContinues_RunStillFailed()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ThrowingCompJob>();
            b.AddBatch("saga.compthrows", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<ThrowingCompJob>(c => c.WithMaxRetries(0)))
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.compthrows")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(runStore, runId);

            run.Status.Should().Be(JobStatus.Failed, "a compensated run still ends Failed");
            CompensatorEntries().Should().Equal(
                new[]
                {
                    CompensationStepIds.For(def.Steps[1].StepId),   // the throwing compensator was dispatched...
                    CompensationStepIds.For(def.Steps[0].StepId),   // ...and the unwind continued past its failure
                },
                "a failing compensator is logged and the remaining unwind continues (best-effort)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_ThenChain_OrderIsUnwindThenChain()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ChainProbeJob>();
            b.AddBatch("saga.unwindthenchain", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<ChainProbeJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.unwindthenchain")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            var recorded = Sequence.ToList();
            var compIndex = recorded.IndexOf(CompensationStepIds.For(def.Steps[0].StepId));
            var chainIndex = recorded.IndexOf(def.OnFailureSteps[0].StepId);
            compIndex.Should().BeGreaterThanOrEqualTo(0, "the compensator must have run");
            chainIndex.Should().BeGreaterThanOrEqualTo(0, "the failure chain must have run");
            compIndex.Should().BeLessThan(chainIndex, "the reverse unwind fully precedes the batch-level failure chain");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Compensate_NoCompensatorNoChain_DegradesToStop_ZeroCompensationWrites()
    {
        ResetSequence();
        var spy = new CursorSpyRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<OkStepJob>();
                b.AddJob<FailingStepJob>();
                b.AddBatch("saga.degrade.stop", x => x
                    .RunJob<OkStepJob>()
                    .ThenRunJob<FailingStepJob>()
                    .FailurePolicy(BatchFailurePolicy.Compensate));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.degrade.stop")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Failed, "the run fails exactly as it would under StopOnFailure");
            spy.CompensationCursorWrites.Should().BeEmpty(
                "with no compensators and no failure chain the compensation route is never taken — " +
                "the run store must never see a compensation-cursor write");
            run.CompensationStepIndex.Should().BeNull("the unwind cursor is never touched on the degrade path");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ContinueOnFailure_IgnoresCompensators()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.continue.ignores", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .ThenRunJob<OkStepJob>()
                .FailurePolicy(BatchFailurePolicy.ContinueOnFailure));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.continue.ignores")!;
            var cursorWrites = new ConcurrentQueue<int>();
            var executor = BuildExecutor(host, (index, _) =>
            {
                cursorWrites.Enqueue(index);
                return Task.CompletedTask;
            });

            // ContinueOnFailure swallows the failed step and proceeds — no throw, no compensation.
            await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            CompensatorEntries().Should().BeEmpty("ContinueOnFailure never routes to compensation");
            cursorWrites.Should().BeEmpty("no compensation progress is ever recorded under ContinueOnFailure");
            Sequence.Should().Contain(def.Steps[2].StepId, "the step after the failure still runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== forwarded state =====

    [Fact]
    public async Task Unwind_ForwardsParams_InitialAccumulatedStatic_LaterWins()
    {
        CapturingCompJob.Reset();
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOutputJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CapturingCompJob>();
            b.AddBatch("saga.mergedparams", x => x
                .RunJob<EmitOutputJob>(s => s.CompensateWith<CapturingCompJob>(c => c.WithParameters(
                    new Dictionary<string, object?>
                    {
                        ["stat"] = "static-value",
                        ["shared"] = "from-static",
                    })))
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.mergedparams")!;
            var executor = BuildExecutor(host);
            var initial = new JobParameters(new Dictionary<string, object?>
            {
                ["init"] = "batch-initial",
                ["shared"] = "from-initial",
            });

            var act = async () => await executor.RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            var captured = CapturingCompJob.Captured;
            captured.Should().NotBeNull("the compensator must have run and captured its parameters");
            captured!.GetRequired<string>("init").Should().Be("batch-initial", "batch-initial values are forwarded");
            captured.GetRequired<string>("out").Should().Be("fromA", "accumulated step outputs are forwarded");
            captured.GetRequired<string>("stat").Should().Be("static-value", "the compensator's own static parameters apply");
            captured.GetRequired<string>("shared").Should().Be("from-static",
                "later sources win: the compensator's static parameter beats the forwarded output, which beats the initial value");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== parallel-group compensation unit =====

    [Fact]
    public async Task Unwind_GroupCompensator_VerdictSatisfiedGroup_RunsOnceAsUnit()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.groupcomp.unit", x => x
                .ThenInParallel(g => g
                    .RunJob<OkStepJob>()
                    .RunJob<OkStepJob>()
                    .CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.groupcomp.unit")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[0].StepId) },
                "a verdict-satisfied group compensates exactly ONCE, as one unit, under the GROUP step's derived id");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Unwind_GroupIsFailedStep_DoesNotCompensate()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.groupcomp.failed", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenInParallel(g => g
                    .RunJob<OkStepJob>()
                    .RunJob<FailingStepJob>()   // WaitAll → the group itself fails
                    .CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.groupcomp.failed")!;
            var executor = BuildExecutor(host);

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[0].StepId) },
                "the group that IS the failed step is never compensated; only the earlier completed step unwinds");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== cancellation =====

    [Fact]
    public async Task AdminCancel_MidUnwind_StopsUnwind_RunCancelled()
    {
        ResetSequence();
        ParkingCompJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ParkingCompJob>();
            b.AddBatch("saga.admincancel.unwind", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<ParkingCompJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var canceller = host.Services.GetRequiredService<IBatchRunCanceller>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.admincancel.unwind")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            // Wait until the FIRST compensator (the later step's) is genuinely mid-flight, then cancel.
            await ParkingCompJob.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var cancelled = await Waits.ForAsync(() => canceller.Cancel(runId), TimeSpan.FromSeconds(10));
            cancelled.Should().BeTrue("the run must be live and cancellable mid-unwind");

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Cancelled, "an administrative cancel mid-unwind ends the run Cancelled");
            CompensatorEntries().Should().NotContain(CompensationStepIds.For(def.Steps[0].StepId),
                "the cancel stops the unwind — the remaining (earlier) compensator never runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task HostShutdown_MidUnwind_LeftInFlight_CursorPersisted()
    {
        ResetSequence();
        ParkingCompJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ParkingCompJob>();
            b.AddBatch("saga.hoststop.unwind", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<ParkingCompJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });

        var runner = host.Services.GetRequiredService<IJobRunner>();
        var runStore = host.Services.GetRequiredService<IBatchRunStore>();
        var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.hoststop.unwind")!;

        var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

        // The unwind is mid-flight (the later step's compensator is parked); stop the host gracefully.
        await ParkingCompJob.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await TestHostBuilder.StopGracefullyAsync(host);

        // Left in-flight for recovery: no terminal status, and the unwind marker survives so the next
        // host resumes the unwind (not the forward walk) from the recorded point.
        var run = await runStore.GetAsync(runId, CancellationToken.None);
        run.Should().NotBeNull();
        run!.Status.Should().BeNull("a graceful host shutdown mid-unwind leaves the run in-flight, not Cancelled");
        run.CompensationStepIndex.Should().Be(2,
            "the unwind marker (the failed step's index) was persisted before the first compensator started");
    }
}
