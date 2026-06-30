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
/// The zero-regression anchor for step-output forwarding. When a batch forwards no output, the merge of a
/// step's parameters must be the IDENTITY of the batch-initial parameters — the same reference, no copy, no
/// allocation. And a whole batch where no job touches <see cref="JobContext.Outputs"/> must dispatch the
/// byte-identical parameter set it did before forwarding existed. These pin the "a job that writes nothing
/// changes no behavior" promise that the feature rests on.
/// </summary>
public class MergeParametersFastPathTests
{
    [Fact]
    public void Merge_BothExtraSourcesNull_ReturnsSameInitialReference()
    {
        var initial = new JobParameters(new Dictionary<string, object?> { ["x"] = 1 });

        var merged = ParallelGroupRunner.MergeParameters(initial, accumulatedOutputs: null, stepParameters: null);

        merged.Should().BeSameAs(initial, "with nothing to merge, the initial parameters are returned unchanged (no copy)");
    }

    [Fact]
    public void Merge_BothExtraSourcesEmpty_ReturnsSameInitialReference()
    {
        var initial = new JobParameters(new Dictionary<string, object?> { ["x"] = 1 });
        var emptyOutputs = new Dictionary<string, object?>();
        var emptyStep = new Dictionary<string, object?>();

        var merged = ParallelGroupRunner.MergeParameters(initial, emptyOutputs, emptyStep);

        merged.Should().BeSameAs(initial, "empty extra sources take the same fast path as null");
    }

    [Fact]
    public void Merge_OnlyAccumulatedOutputs_OverlaysInitial()
    {
        var initial = new JobParameters(new Dictionary<string, object?> { ["x"] = 1 });
        var outputs = new Dictionary<string, object?> { ["y"] = 2 };

        var merged = ParallelGroupRunner.MergeParameters(initial, outputs, stepParameters: null);

        merged.Should().NotBeSameAs(initial, "a non-empty source forces a merged copy");
        merged.GetRequired<int>("x").Should().Be(1);
        merged.GetRequired<int>("y").Should().Be(2);
    }

    [Fact]
    public void Merge_Precedence_StepBeatsOutputsBeatsInitial()
    {
        var initial = new JobParameters(new Dictionary<string, object?> { ["v"] = "init", ["i"] = 1 });
        var outputs = new Dictionary<string, object?> { ["v"] = "fwd", ["o"] = 2 };
        var step = new Dictionary<string, object?> { ["v"] = "static", ["s"] = 3 };

        var merged = ParallelGroupRunner.MergeParameters(initial, outputs, step);

        merged.GetRequired<string>("v").Should().Be("static", "step-static beats forwarded beats initial");
        merged.GetRequired<int>("i").Should().Be(1, "initial-only keys survive");
        merged.GetRequired<int>("o").Should().Be(2, "forwarded-only keys survive");
        merged.GetRequired<int>("s").Should().Be(3, "step-only keys survive");
    }

    /// <summary>Captures the exact parameter dictionary each step received, keyed by job name.</summary>
    private sealed class CapturingJob : IJob
    {
        public static readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, object?>> Captured = new(StringComparer.Ordinal);
        public static void Reset() => Captured.Clear();
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured[context.JobName] = context.Parameters.Values;
            return Task.CompletedTask;
        }
    }

    private sealed class SecondCapturingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            CapturingJob.Captured[context.JobName] = context.Parameters.Values;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task NoJobTouchesOutputs_DispatchedParametersAreByteIdenticalToInitial()
    {
        // A batch whose jobs never write Outputs must dispatch each step the batch-initial parameters
        // verbatim — no forwarded keys injected, no values altered. The capturing jobs never call
        // Outputs.Set, so the accumulator stays empty and the merge fast path applies on every step.
        CapturingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<CapturingJob>();
            b.AddJob<SecondCapturingJob>();
            b.AddBatch("fwd.nooutput.identity", x => x
                .RunJob<CapturingJob>()
                .ThenRunJob<SecondCapturingJob>());
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("fwd.nooutput.identity")!;
            var initial = new JobParameters(new Dictionary<string, object?> { ["region"] = "EU", ["tier"] = 3 });

            var executor = new BatchExecutor(
                host.Services.GetRequiredService<IJobRunnerInternal>(),
                host.Services.GetRequiredService<IApprovalGateCoordinator>(),
                host.Services.GetRequiredService<IJobExecutionAwaiter>(),
                host.Services.GetRequiredService<ITransport>(),
                thisServiceName: null,
                host.Services.GetRequiredService<TimeProvider>(),
                host.Services.GetRequiredService<ILogger<BatchExecutor>>());

            await executor.RunAsync(def, Guid.NewGuid().ToString("N"), initial, "tester", CancellationToken.None);

            var expected = new Dictionary<string, object?> { ["region"] = "EU", ["tier"] = 3 };
            CapturingJob.Captured[typeof(CapturingJob).FullName!].Should().Equal(expected,
                "the first step receives the batch-initial parameters unchanged");
            CapturingJob.Captured[typeof(SecondCapturingJob).FullName!].Should().Equal(expected,
                "a later step with no forwarded output still receives exactly the batch-initial parameters");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
