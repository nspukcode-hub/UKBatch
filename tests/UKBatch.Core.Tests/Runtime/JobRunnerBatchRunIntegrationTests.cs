using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NSubstitute;
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
/// The run-store exists to give a batch RUN an authoritative terminal status that a roll-up over its
/// execution rows cannot supply — a gate-failed run leaves NO execution row, so a pure roll-up reads
/// Completed (green) for a run that actually failed. These end-to-end tests, over a real host, pin:
/// <list type="bullet">
///   <item><b>The headline:</b> a gate-failed run is stored <c>Failed</c>, not Completed.</item>
///   <item>A clean run stores <c>Completed</c> + the executed counts; a cancelled run stores <c>Cancelled</c>.</item>
///   <item>Counts include cross-service shadow rows; <c>StepCount</c> is the definition topology.</item>
///   <item>The run is created in-progress (Status null) before it runs.</item>
///   <item><b>Independence:</b> a throwing run-store must never break the proven SignalR completion path.</item>
/// </list>
/// </summary>
public class JobRunnerBatchRunIntegrationTests
{
    private sealed class NoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>A transparent spy over the real in-memory run store, recording the created run id.</summary>
    private sealed class SpyBatchRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public TaskCompletionSource<string> Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task CreateAsync(BatchRun run, CancellationToken cancellationToken)
        {
            Created.TrySetResult(run.BatchId);
            return _inner.CreateAsync(run, cancellationToken);
        }

        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
            => _inner.CompleteAsync(batchId, terminalStatus, counts, completedAtUtc, cancellationToken);

        public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken) => _inner.GetAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);
    }

    /// <summary>A run store whose CompleteAsync always throws, to prove the completion-signal path is independent.</summary>
    private sealed class ThrowingOnCompleteBatchRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public Task CreateAsync(BatchRun run, CancellationToken cancellationToken) => _inner.CreateAsync(run, cancellationToken);

        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
            => throw new InvalidOperationException("run-store CompleteAsync failure (intentional)");

        public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken) => _inner.GetAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);
    }

    /// <summary>Resolves the internal completion-signal singleton (the proven SignalR feed) via reflection.</summary>
    private static IBatchCompletionEvents ResolveSignal(IServiceProvider sp)
    {
        var coreAssembly = typeof(IJobRunner).Assembly;
        var signalType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignal")
            ?? throw new InvalidOperationException("BatchCompletionSignal type not found in UKBatch.Core.");
        return (IBatchCompletionEvents)sp.GetRequiredService(signalType);
    }

    /// <summary>Awaits a run to reach a non-null (terminal) stored status, or throws on a 60s deadlock backstop.</summary>
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
    public async Task GateFailedRun_StoredStatusIsFailed_NotCompleted()
    {
        // THE headline regression. A batch whose only meaningful step is an approval gate set to fail on a
        // short timeout ends in failure but leaves NO JobExecution row (a gate has no row). A roll-up over
        // rows would read Completed; the stored run status must be Failed, from the runtime's own verdict.
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                b.AddBatch("gate.fail.pipeline", x => x
                    .ThenWaitForApproval("Confirm", new[] { "admin" }, timeout: TimeSpan.FromMilliseconds(100), onTimeout: ApprovalTimeoutAction.Fail)
                    .FailurePolicy(BatchFailurePolicy.StopOnFailure));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("gate.fail.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Failed,
                "a gate-failed run must store Failed even though no execution row is Failed — this is the whole reason the run-store exists");
            run.CompletedAtUtc.Should().NotBeNull();
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task CleanRun_StoredStatusIsCompleted_WithCorrectCounts()
    {
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                b.AddBatch("clean.pipeline", x => x.RunJob<NoopJob>().ThenRunJob<NoopJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("clean.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Completed);
            run.Total.Should().Be(2, "two local job steps each produce one execution row");
            run.Succeeded.Should().Be(2);
            run.Failed.Should().Be(0);
            run.Cancelled.Should().Be(0);
            run.StepCount.Should().Be(2, "two main job steps");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task CancelledRun_StoredStatusIsCancelled()
    {
        // Park the run on an indefinite approval gate, then trip it via the administrative canceller. The
        // gate throws on cancellation, the executor propagates without compensation, and the run stores
        // Cancelled.
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                // A long Hold timeout keeps the run parked (in-progress) until it is cancelled.
                b.AddBatch("cancel.pipeline", x => x
                    .ThenWaitForApproval("Confirm", new[] { "admin" }, timeout: TimeSpan.FromMinutes(5), onTimeout: ApprovalTimeoutAction.Hold));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var canceller = host.Services.GetRequiredService<IBatchRunCanceller>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("cancel.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            // Wait until the run is registered live (its create landed), then cancel it.
            await spy.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var cancelled = await Waits.ForAsync(() => canceller.Cancel(runId), TimeSpan.FromSeconds(10));
            cancelled.Should().BeTrue("the run must be live and cancellable while parked on the gate");

            var run = await AwaitRunTerminalAsync(spy, runId);
            run.Status.Should().Be(JobStatus.Cancelled, "cancelling a gate-parked run stores Cancelled");
            run.CompletedAtUtc.Should().NotBeNull();
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task CrossServiceRun_CountsShadowRows_InTotal()
    {
        // A cross-service step produces a server-side shadow JobExecution (BatchId set). The run's count
        // query picks it up, so Total counts it — even though no LOCAL job ran.
        var spy = new SpyBatchRunStore();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-exec",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });

        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.Configure(o => o.ThisServiceName = "orchestrator");
                b.AddJob<NoopJob>();
                b.AddBatch("cross.pipeline", x => x.RunJob("RemoteJob", step => step.OnService("billing")));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
                services.RemoveAll<ITransport>();
                services.AddSingleton(transport);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("cross.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Completed);
            run.Total.Should().Be(1, "the cross-service shadow row is counted in Total");
            run.Succeeded.Should().Be(1, "the shadow row finished Completed");
            run.StepCount.Should().Be(1, "the cross-service step is one main step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task StepCount_CountsChildrenGateAndOnFailure_NotTheParallelGroupContainer()
    {
        // Topology: [Job, ParallelGroup(2 children), ApprovalGate, OnFailure(1)] → 1 + 2 + 1 + 1 = 5.
        // The ParallelGroup CONTAINER is not counted (its children are); the gate and the compensation step
        // ARE counted. This pins the StepCount derivation.
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                b.AddBatch("topology.pipeline", x => x
                    .RunJob<NoopJob>()
                    .ThenInParallel(g => g.RunJob<NoopJob>().RunJob<NoopJob>())
                    .ThenWaitForApproval("Confirm", new[] { "admin" }, timeout: TimeSpan.FromMilliseconds(100), onTimeout: ApprovalTimeoutAction.Fail)
                    .FailurePolicy(BatchFailurePolicy.Compensate)
                    .OnFailure(f => f.RunJob<NoopJob>()));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("topology.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            // The StepCount is stamped at create time — assert the created run directly (no need to await terminal).
            await spy.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var created = (await spy.GetAsync(runId, CancellationToken.None))!;
            created.StepCount.Should().Be(5,
                "1 job + 2 parallel children + 1 gate + 1 compensation step = 5 (the ParallelGroup container is not a step)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task StepCount_CountsPerStepCompensators_PlusOneEach()
    {
        // Topology: [Job(+compensator), ParallelGroup(2 children, +group compensator), OnFailure(1)]
        // → 1 + 1 + 2 + 1 + 1 = 6. Each per-step compensator is a distinct executable step, so the drift
        // tripwire notices a compensator being added or removed.
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                b.AddBatch("topology.compensators", x => x
                    .RunJob<NoopJob>(s => s.CompensateWith<NoopJob>())
                    .ThenInParallel(g => g
                        .RunJob<NoopJob>()
                        .RunJob<NoopJob>()
                        .CompensateWith<NoopJob>())
                    .FailurePolicy(BatchFailurePolicy.Compensate)
                    .OnFailure(f => f.RunJob<NoopJob>()));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("topology.compensators")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            await spy.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var created = (await spy.GetAsync(runId, CancellationToken.None))!;
            created.StepCount.Should().Be(6,
                "1 job + its compensator + 2 parallel children + the group compensator + 1 OnFailure step = 6");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunRecord_CreatedInProgress_BeforeItRuns()
    {
        // The create happens on the trigger thread; observe Status == null at create time (before completion).
        var spy = new SpyBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                // A long Hold gate keeps the run in-progress long enough to observe the null status.
                b.AddBatch("inprogress.pipeline", x => x
                    .ThenWaitForApproval("Confirm", new[] { "admin" }, timeout: TimeSpan.FromMinutes(5), onTimeout: ApprovalTimeoutAction.Hold));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("inprogress.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            await spy.Created.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var created = (await spy.GetAsync(runId, CancellationToken.None))!;
            created.Status.Should().BeNull("the run is created in-progress before the fire-and-forget run completes");
            created.CompletedAtUtc.Should().BeNull();
            created.TriggeredBy.Should().Be("tester");

            // Tear down via cancel so the gate-parked run terminalizes cleanly.
            host.Services.GetRequiredService<IBatchRunCanceller>().Cancel(runId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunStoreCompleteThrows_BatchCompletionSignalStillFires()
    {
        // Independence: even if the run-store CompleteAsync throws, the proven SignalR completion path must
        // still fire. The two terminal side-effects are independent statements in the runner's finally.
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<NoopJob>();
                b.AddBatch("independence.pipeline", x => x.RunJob<NoopJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(new ThrowingOnCompleteBatchRunStore());
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var signalEvents = ResolveSignal(host.Services);
            var def = lookup.TryGetByName("independence.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            BatchCompletionSignalPayload? observed = null;
            await foreach (var payload in signalEvents.CompletedBatchRunIds.ReadAllAsync(cts.Token))
            {
                if (payload.BatchRunId == runId)
                {
                    observed = payload;
                    break;
                }
            }

            observed.Should().NotBeNull(
                "the SignalR completion signal must fire even when the run-store CompleteAsync throws — the run-store write must never break the proven path");
            observed!.BatchDefinitionId.Should().Be(def.Id);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
