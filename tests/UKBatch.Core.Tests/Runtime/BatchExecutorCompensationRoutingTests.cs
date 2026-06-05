using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// A step that fails with ANY exception (not only a step-terminated signal) — e.g. an unregistered
/// job name surfacing from dispatch — must follow the same <see cref="BatchFailurePolicy"/> routing,
/// including compensation. Batch cancellation is ordered ahead of the failure arms, so a cancelled
/// batch NEVER runs <c>OnFailureSteps</c>.
/// </summary>
public class BatchExecutorCompensationRoutingTests
{
    /// <summary>Signals (once) when its compensation step runs.</summary>
    public sealed class CompensationProbeJob : IJob
    {
        public static TaskCompletionSource Ran { get; private set; } = NewSignal();
        public static void ResetSignal() => Ran = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>Signals (once) when the follow-on step runs.</summary>
    public sealed class NextStepProbeJob : IJob
    {
        public static TaskCompletionSource Ran { get; private set; } = NewSignal();
        public static void ResetSignal() => Ran = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>A registered no-op so a parallel group can have a valid second child.</summary>
    public sealed class NoopJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>Signals start, then blocks on its execution token (for the cancellation test).</summary>
    public sealed class BlockingStepJob : IJob
    {
        public static TaskCompletionSource Started { get; private set; } = NewSignal();
        public static void ResetSignal() => Started = NewSignal();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            return Task.Delay(Timeout.Infinite, cancellationToken);
        }
    }

    private const string UnregisteredJobName = "definitely.not.registered.job";

    private static IBatchCompletionEvents ResolveSignal(IServiceProvider sp)
    {
        var coreAssembly = typeof(IJobRunner).Assembly;
        var signalType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignal")
            ?? throw new InvalidOperationException("BatchCompletionSignal type not found in UKBatch.Core.");
        return (IBatchCompletionEvents)sp.GetRequiredService(signalType);
    }

    private static async Task WaitForBatchCompletionAsync(IServiceProvider sp, string batchId, TimeSpan timeout)
    {
        var signal = ResolveSignal(sp);
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var payload in signal.CompletedBatchRunIds.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                if (payload.BatchRunId == batchId)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // watchdog — caller asserts on observed effects
        }
    }

    [Fact]
    public async Task SequentialStep_UnregisteredJob_CompensatePolicy_RunsOnFailureSteps()
    {
        CompensationProbeJob.ResetSignal();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<CompensationProbeJob>();
            b.AddBatch("compensate.sequential", x => x
                .RunJob(UnregisteredJobName)
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<CompensationProbeJob>()));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("compensate.sequential")!;

            await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            await CompensationProbeJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            CompensationProbeJob.Ran.Task.IsCompletedSuccessfully.Should().BeTrue(
                "an unregistered job step under Compensate must still route through OnFailureSteps.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task ParallelChild_UnregisteredJob_CompensatePolicy_RunsOnFailureSteps()
    {
        CompensationProbeJob.ResetSignal();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<CompensationProbeJob>();
            b.AddJob<NoopJob>();
            b.AddBatch("compensate.parallel", x => x
                .ThenInParallel(g => g
                    .RunJob(UnregisteredJobName)
                    .RunJob<NoopJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<CompensationProbeJob>()));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("compensate.parallel")!;

            await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            await CompensationProbeJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            CompensationProbeJob.Ran.Task.IsCompletedSuccessfully.Should().BeTrue(
                "an unregistered parallel child under Compensate must still route through OnFailureSteps.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task SequentialStep_UnregisteredJob_ContinueOnFailure_ProceedsToNextStep()
    {
        NextStepProbeJob.ResetSignal();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<NextStepProbeJob>();
            b.AddBatch("continue.sequential", x => x
                .RunJob(UnregisteredJobName)
                .ThenRunJob<NextStepProbeJob>()
                .FailurePolicy(BatchFailurePolicy.ContinueOnFailure));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("continue.sequential")!;

            await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            await NextStepProbeJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            NextStepProbeJob.Ran.Task.IsCompletedSuccessfully.Should().BeTrue(
                "ContinueOnFailure must proceed to the next step after an unregistered-job failure.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task SequentialStep_UnregisteredJob_StopOnFailure_DoesNotRunCompensation()
    {
        CompensationProbeJob.ResetSignal();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<CompensationProbeJob>();
            b.AddBatch("stop.sequential", x => x
                .RunJob(UnregisteredJobName)
                .FailurePolicy(BatchFailurePolicy.StopOnFailure)
                .OnFailure(f => f.RunJob<CompensationProbeJob>()));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("stop.sequential")!;

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            // Wait for the batch to finish, then confirm compensation never ran.
            await WaitForBatchCompletionAsync(host.Services, batchId, TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            // Small bounded settle so a (wrongly) scheduled compensation would have a chance to run.
            var ranWithinSettle = await Task.WhenAny(
                CompensationProbeJob.Ran.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            (ranWithinSettle == CompensationProbeJob.Ran.Task).Should().BeFalse(
                "StopOnFailure must NOT run OnFailureSteps.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task BatchCancellation_BeforeStepCompletes_DoesNotRunCompensation()
    {
        BlockingStepJob.ResetSignal();
        CompensationProbeJob.ResetSignal();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<BlockingStepJob>();
            b.AddJob<CompensationProbeJob>();
            b.AddBatch("cancel.sequential", x => x
                .RunJob<BlockingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<CompensationProbeJob>()));
        }).ConfigureAwait(false);
        var stopped = false;
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("cancel.sequential")!;

            await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            // Ensure the step is mid-flight, then cancel via host shutdown (the batch CT is the host's
            // ApplicationStopping token — it is host-decoupled from the trigger CT by design).
            await BlockingStepJob.Started.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            await host.StopAsync(TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            stopped = true;

            // A cancelled batch is not a failed batch — compensation must NOT run.
            var ranWithinSettle = await Task.WhenAny(
                CompensationProbeJob.Ran.Task, Task.Delay(TimeSpan.FromSeconds(1))).ConfigureAwait(false);
            (ranWithinSettle == CompensationProbeJob.Ran.Task).Should().BeFalse(
                "batch cancellation must propagate without running OnFailureSteps.");
        }
        finally
        {
            if (!stopped)
            {
                await TestHostBuilder.StopGracefullyAsync(host, TimeSpan.FromSeconds(15)).ConfigureAwait(false);
            }
        }
    }
}
