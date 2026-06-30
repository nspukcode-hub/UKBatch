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

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// Drives <see cref="BatchExecutor"/> directly to pin local step-output forwarding: a step records output
/// values via <see cref="JobContext.Outputs"/>, and a later step reads them back through its
/// <see cref="JobContext.Parameters"/>. Covers the sequential happy path, the precedence rules between a
/// batch-initial value / a forwarded output / a step's own static parameter, and the parallel-group
/// fan-in semantics (children see the group-entry snapshot, not one another's output; the join folds the
/// satisfying children by ascending order).
/// </summary>
public class StepOutputForwardingTests
{
    // ===== probe jobs =====
    // Each probe captures the parameter set it received (keyed by job name) and may emit an output. Distinct
    // job types are needed because the registry keys jobs by type.

    /// <summary>Captures every parameter set a job received, keyed by job name, across all probe jobs.</summary>
    private static readonly ConcurrentDictionary<string, JobParameters> Received = new(StringComparer.Ordinal);
    private static void ResetReceived() => Received.Clear();

    /// <summary>Step that emits a single output {orderId: 5}; captures nothing of interest.</summary>
    private sealed class EmitOrderIdJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            context.Outputs.Set("orderId", 5);
            return Task.CompletedTask;
        }
    }

    /// <summary>Step that emits {region: "EU"}.</summary>
    private sealed class EmitRegionJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            context.Outputs.Set("region", "EU");
            return Task.CompletedTask;
        }
    }

    /// <summary>A pure recorder: captures the parameters it received and emits nothing.</summary>
    private sealed class RecorderJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            return Task.CompletedTask;
        }
    }

    /// <summary>Parallel child A: emits {a: 1}, and also records whether it observed a sibling's key "b".</summary>
    private sealed class ParallelEmitAJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            context.Outputs.Set("a", 1);
            return Task.CompletedTask;
        }
    }

    /// <summary>Parallel child B: emits {b: 2}.</summary>
    private sealed class ParallelEmitBJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            context.Outputs.Set("b", 2);
            return Task.CompletedTask;
        }
    }

    /// <summary>Parallel child with low order (Order 0): emits {shared: "low"}.</summary>
    private sealed class ParallelLowOrderJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("shared", "low");
            return Task.CompletedTask;
        }
    }

    /// <summary>Parallel child with high order (Order 1): emits {shared: "high"}.</summary>
    private sealed class ParallelHighOrderJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("shared", "high");
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

    // ===== sequential forwarding =====

    [Fact]
    public async Task Sequential_StepOutput_VisibleToNextStepParameters()
    {
        // Step 0 records {orderId: 5}; step 1 (a recorder) must see orderId in its parameters.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.seq.basic", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.seq.basic")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            var recorderParams = Received[typeof(RecorderJob).FullName!];
            recorderParams.TryGet<int>("orderId", out var orderId).Should().BeTrue("the next step sees the prior step's output");
            orderId.Should().Be(5);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Sequential_OutputsAccumulate_AcrossMultipleSteps()
    {
        // Step 0 emits orderId, step 1 emits region; step 2 (a recorder) must see BOTH.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<EmitRegionJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.seq.accumulate", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenRunJob<EmitRegionJob>()
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.seq.accumulate")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            var recorderParams = Received[typeof(RecorderJob).FullName!];
            recorderParams.GetRequired<int>("orderId").Should().Be(5, "the earliest step's output is still present at the end");
            recorderParams.GetRequired<string>("region").Should().Be("EU", "each step's output accumulates forward");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== precedence: initial < forwarded < step-static =====

    [Fact]
    public async Task Precedence_StepStaticParameter_BeatsForwardedOutput_BeatsInitial()
    {
        // The same key "v" is set three ways: batch-initial parameter ("init"), a forwarded output ("fwd"),
        // and the consuming step's own static parameter ("static"). The step's static value must win; and a
        // SECOND recorder with no static parameter for "v" must see the forwarded value (forwarded beats
        // initial). This is the full three-way precedence proof in one batch.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitVJob>();
            b.AddJob<RecorderWithStaticVJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.precedence", x => x
                .RunJob<EmitVJob>()                                                   // forwards {v: "fwd"}
                .ThenRunJob<RecorderWithStaticVJob>(s => s.WithParameters(            // static {v: "static"} wins
                    new Dictionary<string, object?> { ["v"] = "static" }))
                .ThenRunJob<RecorderJob>());                                          // no static → sees forwarded "fwd"
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.precedence")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["v"] = "init" });

            await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            Received[typeof(RecorderWithStaticVJob).FullName!].GetRequired<string>("v")
                .Should().Be("static", "a step's own static parameter beats a forwarded output and the batch-initial value");
            Received[typeof(RecorderJob).FullName!].GetRequired<string>("v")
                .Should().Be("fwd", "with no static override, a forwarded output beats the batch-initial value");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    /// <summary>Forwards {v: "fwd"} (does NOT overwrite from initial — it sets its own key value).</summary>
    private sealed class EmitVJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("v", "fwd");
            return Task.CompletedTask;
        }
    }

    /// <summary>Recorder used by the precedence test (a distinct type so it captures under its own name).</summary>
    private sealed class RecorderWithStaticVJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Received[context.JobName] = context.Parameters;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Initial_VisibleToFirstStep_WhenNoOutputForwardedYet()
    {
        // A batch-initial parameter is visible to the very first step (nothing forwarded yet).
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.initial.first", x => x.RunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.initial.first")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["region"] = "AP" });

            await BuildExecutor(host).RunAsync(def, IdNew(), initial, "tester", CancellationToken.None);

            Received[typeof(RecorderJob).FullName!].GetRequired<string>("region").Should().Be("AP");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== parallel fan-in =====

    [Fact]
    public async Task Parallel_TwoChildrenEmitDistinctKeys_FollowingStepSeesBoth()
    {
        // A parallel group: child A emits {a:1}, child B emits {b:2}. A sequential recorder after the group
        // must see BOTH keys merged from the join.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();   // a leading job so the group is not the first step
            b.AddJob<ParallelEmitAJob>();
            b.AddJob<ParallelEmitBJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.parallel.bothkeys", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitAll)
                    .RunJob<ParallelEmitAJob>()
                    .RunJob<ParallelEmitBJob>())
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.parallel.bothkeys")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            var recorderParams = Received[typeof(RecorderJob).FullName!];
            recorderParams.GetRequired<int>("a").Should().Be(1, "the join merges output from every WaitAll child");
            recorderParams.GetRequired<int>("b").Should().Be(2);
            // The leading step's own output is also still forwarded past the group.
            recorderParams.GetRequired<int>("orderId").Should().Be(5);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Parallel_SameKeyCollision_HighestOrderChildWins()
    {
        // Two children both emit the key "shared". The join folds the satisfying children in ASCENDING order,
        // so the highest-order child's value wins (deterministic last-writer-wins). Child order is the
        // declaration order inside the group: low-order job declared first (Order 0), high-order second (Order 1).
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<ParallelLowOrderJob>();
            b.AddJob<ParallelHighOrderJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.parallel.collision", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitAll)
                    .RunJob<ParallelLowOrderJob>()    // Order 0
                    .RunJob<ParallelHighOrderJob>())  // Order 1
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.parallel.collision")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            Received[typeof(RecorderJob).FullName!].GetRequired<string>("shared")
                .Should().Be("high", "the highest-order child wins a key collision in the deterministic fold");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Parallel_ChildDoesNotSeeSiblingOutput_OnlyGroupEntrySnapshot()
    {
        // Children all receive the SAME accumulated-output snapshot captured at group entry; they do NOT
        // observe one another's output (concurrent children have no defined order). The leading step forwards
        // {orderId:5}; both children must see orderId (the group-entry snapshot) but child A must NOT see
        // child B's key "b" in its parameters.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<ParallelEmitAJob>();
            b.AddJob<ParallelEmitBJob>();
            b.AddBatch("fwd.parallel.isolation", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitAll)
                    .RunJob<ParallelEmitAJob>()
                    .RunJob<ParallelEmitBJob>()));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.parallel.isolation")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            var childAParams = Received[typeof(ParallelEmitAJob).FullName!];
            childAParams.GetRequired<int>("orderId").Should().Be(5, "every child sees the group-entry accumulator snapshot");
            childAParams.Contains("b").Should().BeFalse("a child must not observe a sibling's output (no cross-child visibility)");

            var childBParams = Received[typeof(ParallelEmitBJob).FullName!];
            childBParams.Contains("a").Should().BeFalse("a child must not observe a sibling's output (no cross-child visibility)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== loser-exclusion: only join-satisfying children are folded =====

    [Fact]
    public async Task Parallel_WaitAny_OnlyWinnerOutputFolded_LoserExcluded()
    {
        // WaitAny folds ONLY the first Completed child. A fast winner emits {w:"winner"}; a slow child would
        // emit {w:"loser"} but is cancelled when the winner resolves, so the following step must see only the
        // winner's value — proving the fold set matches the join verdict (winner only).
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<ParallelFastWinnerJob>();
            b.AddJob<ParallelSlowLoserJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.parallel.waitany.loser", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitAny)
                    .RunJob<ParallelFastWinnerJob>()
                    .RunJob<ParallelSlowLoserJob>())
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.parallel.waitany.loser")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            Received[typeof(RecorderJob).FullName!].GetRequired<string>("w")
                .Should().Be("winner", "WaitAny folds only the winning child's output; a cancelled loser contributes nothing");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Parallel_WaitMajority_FailedChildOutputNotFolded()
    {
        // WaitMajority folds ONLY the quorum-satisfying Completed children. Two children complete (a:1, b:2),
        // satisfying the quorum of 2; a third child emits {ghost:"leaked"} then throws (Failed). The following
        // step must see the completed children's outputs but NEVER the failed child's — a failed/loser child's
        // output must not leak forward.
        ResetReceived();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<EmitOrderIdJob>();
            b.AddJob<ParallelEmitAJob>();
            b.AddJob<ParallelEmitBJob>();
            b.AddJob<ParallelFailEmitGhostJob>();
            b.AddJob<RecorderJob>();
            b.AddBatch("fwd.parallel.majority.loser", x => x
                .RunJob<EmitOrderIdJob>()
                .ThenInParallel(g => g
                    .JoinPolicy(ParallelJoinPolicy.WaitMajority)
                    .RunJob<ParallelEmitAJob>()
                    .RunJob<ParallelEmitBJob>()
                    .RunJob<ParallelFailEmitGhostJob>())
                .ThenRunJob<RecorderJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.parallel.majority.loser")!;

            await BuildExecutor(host).RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            var recorderParams = Received[typeof(RecorderJob).FullName!];
            recorderParams.GetRequired<int>("a").Should().Be(1, "the quorum-satisfying completed children are folded");
            recorderParams.GetRequired<int>("b").Should().Be(2);
            recorderParams.Contains("ghost").Should().BeFalse("a failed child's output must never be folded forward");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    /// <summary>WaitAny winner: emits {w:"winner"} immediately.</summary>
    private sealed class ParallelFastWinnerJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("w", "winner");
            return Task.CompletedTask;
        }
    }

    /// <summary>WaitAny loser: would emit {w:"loser"} but is cancelled once the winner resolves.</summary>
    private sealed class ParallelSlowLoserJob : IJob
    {
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            context.Outputs.Set("w", "loser");
        }
    }

    /// <summary>WaitMajority loser: emits {ghost:"leaked"} then throws, so it ends Failed (capture never runs on the throw path).</summary>
    private sealed class ParallelFailEmitGhostJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            context.Outputs.Set("ghost", "leaked");
            throw new InvalidOperationException("intentional parallel-child failure");
        }
    }
}
