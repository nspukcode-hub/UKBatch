using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// (Test) — verifies that <c>JobRunner.TriggerBatchAsync</c>
/// signals batch completion with the full <see cref="BatchCompletionSignalPayload"/>
/// (BatchRunId + BatchDefinitionId + BatchName) AFTER the executor finishes, in every outcome.
/// </summary>
public class JobRunnerBatchCompletionSignalTests
{
    /// <summary>Resolve the internal <c>BatchCompletionSignal</c> via reflection.</summary>
    private static IBatchCompletionEvents ResolveSignal(IServiceProvider sp)
    {
        var coreAssembly = typeof(IJobRunner).Assembly;
        var signalType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignal")
            ?? throw new InvalidOperationException("BatchCompletionSignal type not found in UKBatch.Core.");
        return (IBatchCompletionEvents)sp.GetRequiredService(signalType);
    }

    [Theory]
    [InlineData(BatchScenario.Success)]
    [InlineData(BatchScenario.Failure)]
    public async Task TriggerBatchAsync_SignalsAfterRunAsyncReturns_ForOutcome(BatchScenario scenario)
    {
        // Test #5b parameterized: success / failure (StopOnFailure) — host-shutdown variant is
        // implicit in TriggerBatchAsync's hostStopping wiring (covered by ConcurrentTriggersTests).
        SucceedingJob.Reset();
        FailingJob.Reset();

        var host = await TestHostBuilder.StartAsync(b =>
        {
            // Use default (Type.FullName) names so the batch builder can resolve them.
            b.AddJob<SucceedingJob>();
            b.AddJob<FailingJob>().WithMaxRetries(0);
            b.AddBatch("batch.signal.success.pipeline", x => x
                .RunJob<SucceedingJob>());
            b.AddBatch("batch.signal.failure.pipeline", x => x
                .RunJob<FailingJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<UKBatch.Abstractions.Batches.IBatchDefinitionLookup>();
            var signalEvents = ResolveSignal(host.Services);

            string defName = scenario == BatchScenario.Success
                ? "batch.signal.success.pipeline"
                : "batch.signal.failure.pipeline";
            var def = lookup.TryGetByName(defName)
                ?? throw new InvalidOperationException($"definition not found: {defName}");

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            // Wait for the signal payload (up to 5s).
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            BatchCompletionSignalPayload? observed = null;
            await foreach (var payload in signalEvents.CompletedBatchRunIds.ReadAllAsync(cts.Token))
            {
                if (payload.BatchRunId == batchId)
                {
                    observed = payload;
                    break;
                }
            }

            observed.Should().NotBeNull("the runtime must signal completion within 5s.");
            observed!.BatchRunId.Should().Be(batchId);
            observed.BatchDefinitionId.Should().Be(def.Id);
            observed.BatchName.Should().Be(defName);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    public enum BatchScenario { Success, Failure }
}
