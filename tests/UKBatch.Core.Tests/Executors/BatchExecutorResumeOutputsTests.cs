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
/// Pins the <c>resumeOutputs</c> parameter on <see cref="BatchExecutor.RunAsync"/>: a resume seeds the
/// forwarding accumulator with earlier steps' outputs so that steps dispatched after the resume index still
/// observe them. Drives the executor directly (same harness as <see cref="BatchExecutorResumeTests"/>),
/// starting at a step index with a pre-populated accumulator.
/// </summary>
public class BatchExecutorResumeOutputsTests
{
    /// <summary>Captures the parameters each step received, keyed by job name.</summary>
    private sealed class RecordingJob : IJob
    {
        public static readonly ConcurrentDictionary<string, JobParameters> Captured = new(StringComparer.Ordinal);
        public static void Reset() => Captured.Clear();
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured[context.JobName] = context.Parameters;
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

    [Fact]
    public async Task RunAsync_ResumeOutputsSeedAccumulator_StepAfterResumeSeesThem()
    {
        // A 2-step batch resumed at index 1 (step 0 already done before the crash) with a seeded accumulator
        // {orderId: 7}. Step 1 must see orderId, exactly as if step 0 had just produced it.
        RecordingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("fwd.resume.seed", x => x
                .RunJob<RecordingJob>()
                .ThenRunJob<RecordingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.resume.seed")!;
            var resumeOutputs = new Dictionary<string, object?> { ["orderId"] = 7 };

            await BuildExecutor(host).RunAsync(
                def, Guid.NewGuid().ToString("N"), JobParameters.Empty, "tester", CancellationToken.None,
                startStepIndex: 1, resumeOutputs: resumeOutputs);

            // Only step 1 ran (step 0 was skipped), and it saw the seeded output.
            RecordingJob.Captured.Should().ContainSingle("only the not-yet-completed step is dispatched on resume");
            RecordingJob.Captured.Values.Single().GetRequired<int>("orderId")
                .Should().Be(7, "the seeded resume outputs are forwarded into the resumed step's parameters");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunAsync_ResumeOutputsNull_NoSeedInjected()
    {
        // The trigger-path default: resumeOutputs null seeds an empty accumulator, so a step sees only the
        // batch-initial parameters (no phantom forwarded keys).
        RecordingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddBatch("fwd.resume.noseed", x => x.RunJob<RecordingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.resume.noseed")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["region"] = "EU" });

            await BuildExecutor(host).RunAsync(def, Guid.NewGuid().ToString("N"), initial, "tester", CancellationToken.None);

            var captured = RecordingJob.Captured.Values.Single();
            captured.GetRequired<string>("region").Should().Be("EU");
            captured.Values.Should().HaveCount(1, "no forwarded keys are injected when nothing was seeded");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
