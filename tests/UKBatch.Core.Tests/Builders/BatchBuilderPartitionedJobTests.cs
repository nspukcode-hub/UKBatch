using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Builders;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Builders;

/// <summary>
/// Typed partitioned-job batch steps. <see cref="IPartitionedJob{TItem}"/> implementations cannot be
/// added through the <see cref="IJob"/>-constrained <c>RunJob&lt;TJob&gt;()</c>; the
/// <c>RunPartitionedJob&lt;TJob&gt;()</c> family closes that gap. Each typed method must produce a step
/// IDENTICAL (same step type + same job name) to the string overload called with the job's type name,
/// and that name must equal the default name <c>AddPartitionedJob&lt;TJob, TItem&gt;()</c> assigns —
/// otherwise the batch could not resolve the registered job at run time.
/// </summary>
public class BatchBuilderPartitionedJobTests
{
    private static readonly string PartitionedJobName =
        typeof(CountingPartitionedJob).FullName ?? typeof(CountingPartitionedJob).Name;

    private static BatchDefinition Build(Action<BatchBuilder> configure)
    {
        var builder = new BatchBuilder(new UKBatchOptions());
        configure(builder);
        return builder.Build("id-1", "batch-1", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void RunPartitionedJob_ProducesJobStep_WithTypeNameMatchingStringOverload()
    {
        var typed = Build(b => b.RunPartitionedJob<CountingPartitionedJob>());
        var stringly = Build(b => b.RunJob(PartitionedJobName));

        var typedStep = typed.Steps.Single();
        var stringStep = stringly.Steps.Single();

        typedStep.StepType.Should().Be(BatchStepType.Job);
        typedStep.Job.Should().NotBeNull();
        typedStep.Job!.JobName.Should().Be(PartitionedJobName);

        // Same step shape as the string overload (StepId is a fresh id by design, so it is excluded).
        typedStep.StepType.Should().Be(stringStep.StepType);
        typedStep.Job!.JobName.Should().Be(stringStep.Job!.JobName);
        typedStep.Job!.TargetService.Should().Be(stringStep.Job!.TargetService);
    }

    [Fact]
    public void ThenRunPartitionedJob_IsAliasForRunPartitionedJob()
    {
        var run = Build(b => b.RunPartitionedJob<CountingPartitionedJob>());
        var then = Build(b => b.ThenRunPartitionedJob<CountingPartitionedJob>());

        then.Steps.Single().StepType.Should().Be(run.Steps.Single().StepType);
        then.Steps.Single().Job!.JobName.Should().Be(run.Steps.Single().Job!.JobName);
        then.Steps.Single().Job!.JobName.Should().Be(PartitionedJobName);
    }

    [Fact]
    public void RunPartitionedJob_AppliesConfigureCallback()
    {
        var typed = Build(b => b.RunPartitionedJob<CountingPartitionedJob>(s => s.WithMaxRetries(4)));
        var stringly = Build(b => b.RunJob(PartitionedJobName, s => s.WithMaxRetries(4)));

        typed.Steps.Single().Job!.MaxRetries.Should().Be(4);
        typed.Steps.Single().Job!.MaxRetries.Should().Be(stringly.Steps.Single().Job!.MaxRetries);
    }

    [Fact]
    public void ParallelGroup_RunPartitionedJob_ProducesChildStep_WithTypeName()
    {
        var typed = Build(b => b.ThenInParallel(g =>
        {
            g.RunPartitionedJob<CountingPartitionedJob>();
            g.RunJob("OtherJob");
        }));
        var stringly = Build(b => b.ThenInParallel(g =>
        {
            g.RunJob(PartitionedJobName);
            g.RunJob("OtherJob");
        }));

        var typedGroup = typed.Steps.Single();
        typedGroup.StepType.Should().Be(BatchStepType.ParallelGroup);
        typedGroup.ParallelGroup.Should().NotBeNull();

        var typedChild = typedGroup.ParallelGroup!.Steps[0];
        var stringChild = stringly.Steps.Single().ParallelGroup!.Steps[0];

        typedChild.StepType.Should().Be(BatchStepType.Job);
        typedChild.Job!.JobName.Should().Be(PartitionedJobName);
        typedChild.Job!.JobName.Should().Be(stringChild.Job!.JobName);
    }

    [Fact]
    public void OnFailure_RunPartitionedJob_ProducesCompensationStep_WithTypeName()
    {
        var typed = Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.RunPartitionedJob<CountingPartitionedJob>());
        });
        var stringly = Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.RunJob(PartitionedJobName));
        });

        var typedStep = typed.OnFailureSteps.Single();
        var stringStep = stringly.OnFailureSteps.Single();

        typedStep.StepType.Should().Be(BatchStepType.Job);
        typedStep.Job!.JobName.Should().Be(PartitionedJobName);
        typedStep.Job!.JobName.Should().Be(stringStep.Job!.JobName);
    }

    [Fact]
    public void OnFailure_ThenRunPartitionedJob_IsAliasForRunPartitionedJob()
    {
        var run = Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.RunPartitionedJob<CountingPartitionedJob>());
        });
        var then = Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.ThenRunPartitionedJob<CountingPartitionedJob>());
        });

        then.OnFailureSteps.Single().Job!.JobName.Should().Be(run.OnFailureSteps.Single().Job!.JobName);
        then.OnFailureSteps.Single().Job!.JobName.Should().Be(PartitionedJobName);
    }

    [Fact]
    public void TypedStepName_MatchesAddPartitionedJobDefaultRegistrationName()
    {
        // The default name AddPartitionedJob assigns must equal the name the typed batch step resolves,
        // otherwise the batch would reference a job that was never registered under that name.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b => b.AddPartitionedJob<CountingPartitionedJob, int>().WithParallelism(2));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<JobDefinitionRegistry>();

        registry.TryGet(PartitionedJobName).Should().NotBeNull(
            "the typed batch step resolves the job by this name");
        registry.TryGetImplementationType(PartitionedJobName).Should().Be<CountingPartitionedJob>();
    }

    [Fact]
    public async Task EndToEnd_RunPartitionedJob_BatchReachesCompleted()
    {
        CountingPartitionedJob.Reset();
        CountingPartitionedJob.Total = 6;

        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddPartitionedJob<CountingPartitionedJob, int>().WithParallelism(3).WithMaxRetries(0);
            b.AddBatch("partitioned.pipeline", x => x.RunPartitionedJob<CountingPartitionedJob>());
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var signal = ResolveBatchCompletion(host.Services);

            var def = lookup.TryGetByName("partitioned.pipeline")
                ?? throw new InvalidOperationException("batch definition not found.");

            var batchRunId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            BatchCompletionSignalPayload? observed = null;
            await foreach (var payload in signal.CompletedBatchRunIds.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                if (payload.BatchRunId == batchRunId)
                {
                    observed = payload;
                    break;
                }
            }

            observed.Should().NotBeNull("the partitioned-job batch must complete within 30s.");
            CountingPartitionedJob.Processed.Should().Be(6);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    private static IBatchCompletionEvents ResolveBatchCompletion(IServiceProvider sp)
    {
        var coreAssembly = typeof(IJobRunner).Assembly;
        var signalType = coreAssembly.GetType("UKBatch.Runtime.BatchCompletionSignal")
            ?? throw new InvalidOperationException("BatchCompletionSignal type not found in UKBatch.Core.");
        return (IBatchCompletionEvents)sp.GetRequiredService(signalType);
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted { get; } = default;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped { get; } = default;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
