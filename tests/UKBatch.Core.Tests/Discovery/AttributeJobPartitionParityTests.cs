using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Builders;
using UKBatch.Discovery;
using UKBatch.Registry;
using Xunit;
#if NET9_0_OR_GREATER
using System.Reflection.Emit;
#endif

namespace UKBatch.Core.Tests.Discovery;

/// <summary>
/// Attribute-based discovery honours the new <c>[Job]</c> partition knobs at parity with the fluent
/// builder: <c>PartitionWorkerCount</c> seeds the worker count (falling to the runtime default when
/// unset), and <c>ItemErrorPolicy = RetryThenContinue</c> with a positive retry budget builds the
/// cached per-item retry pipeline. The same knobs on a non-partitioned job are inert.
/// </summary>
/// <remarks>
/// Each job type is emitted into an isolated, collectible <see cref="AssemblyLoadContext"/> and
/// scanned explicitly — a statically-compiled <c>[Job]</c> type would be discovered by every other
/// <c>AddUKBatch</c> across the test run. The context is unloaded and collected in <c>finally</c>.
/// </remarks>
[Collection("process-wide attribute discovery")]
public class AttributeJobPartitionParityTests
{
#if NET9_0_OR_GREATER
    /// <summary>
    /// Emits a job type carrying <c>[Job(Name, PartitionWorkerCount?, ItemErrorPolicy?)]</c>.
    /// When <paramref name="partitioned"/> the type implements <see cref="IPartitionedJob{TItem}"/> of
    /// <see cref="int"/>; otherwise a plain <see cref="IJob"/>. Method bodies are stubs — discovery
    /// inspects the type + attribute only and never runs the job. The retry budget (the nullable
    /// <c>MaxRetries</c> attribute property cannot be expressed in emitted metadata) is supplied via
    /// <c>UKBatchOptions.DefaultMaxRetries</c>, which discovery folds in identically.
    /// </summary>
    private static (Assembly Assembly, AssemblyLoadContext Context) EmitJobAssembly(
        string jobName,
        bool partitioned,
        int? partitionWorkerCount,
        ItemErrorPolicy? itemErrorPolicy)
    {
        var an = new AssemblyName("UKBatch.Tests.Emitted.Parity." + Guid.NewGuid().ToString("N"));
        var ab = new PersistedAssemblyBuilder(an, typeof(object).Assembly);
        var module = ab.DefineDynamicModule("main");
        var tb = module.DefineType("EmittedParityJob", TypeAttributes.Public | TypeAttributes.Class);

        if (partitioned)
        {
            var partType = typeof(IPartitionedJob<>).MakeGenericType(typeof(int));
            tb.AddInterfaceImplementation(partType);

            // IAsyncEnumerable<int> SourceAsync(JobContext, CancellationToken) => null; (never invoked)
            var source = tb.DefineMethod(
                "SourceAsync",
                MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(IAsyncEnumerable<int>),
                new[] { typeof(JobContext), typeof(CancellationToken) });
            var sil = source.GetILGenerator();
            sil.Emit(OpCodes.Ldnull);
            sil.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(source, partType.GetMethod("SourceAsync")!);

            // Task ProcessAsync(int, JobContext, CancellationToken) => Task.CompletedTask;
            var process = tb.DefineMethod(
                "ProcessAsync",
                MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(Task),
                new[] { typeof(int), typeof(JobContext), typeof(CancellationToken) });
            var pil = process.GetILGenerator();
            pil.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!);
            pil.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(process, partType.GetMethod("ProcessAsync")!);
        }
        else
        {
            tb.AddInterfaceImplementation(typeof(IJob));
            var execute = tb.DefineMethod(
                nameof(IJob.ExecuteAsync),
                MethodAttributes.Public | MethodAttributes.Virtual,
                typeof(Task),
                new[] { typeof(JobContext), typeof(CancellationToken) });
            var il = execute.GetILGenerator();
            il.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!);
            il.Emit(OpCodes.Ret);
            tb.DefineMethodOverride(execute, typeof(IJob).GetMethod(nameof(IJob.ExecuteAsync))!);
        }

        var attrCtor = typeof(JobAttribute).GetConstructor(Type.EmptyTypes)!;
        var props = new List<PropertyInfo> { typeof(JobAttribute).GetProperty(nameof(JobAttribute.Name))! };
        var values = new List<object> { jobName };
        if (partitionWorkerCount is { } pwc)
        {
            props.Add(typeof(JobAttribute).GetProperty(nameof(JobAttribute.PartitionWorkerCount))!);
            values.Add(pwc);
        }
        if (itemErrorPolicy is { } iep)
        {
            props.Add(typeof(JobAttribute).GetProperty(nameof(JobAttribute.ItemErrorPolicy))!);
            values.Add(iep);
        }
        tb.SetCustomAttribute(new CustomAttributeBuilder(attrCtor, Array.Empty<object>(), props.ToArray(), values.ToArray()));
        tb.CreateType();

        using var ms = new MemoryStream();
        ab.Save(ms);
        var alc = new AssemblyLoadContext("ParityProbe", isCollectible: true);
        var loaded = alc.LoadFromStream(new MemoryStream(ms.ToArray()));
        return (loaded, alc);
    }

    [Fact]
    public void Partitioned_AttributeWorkerCount_SeedsDefinition()
    {
        DrainEmittedAssembly(RunWorkerCountScenario("parity.workers.explicit", attributeWorkerCount: 4, expected: 4));
    }

    [Fact]
    public void Partitioned_UnsetWorkerCount_FallsBackToRuntimeDefault()
    {
        // PartitionWorkerCount unset (0) → discovery uses options.DefaultPartitionWorkerCount.
        DrainEmittedAssembly(RunWorkerCountScenario("parity.workers.default", attributeWorkerCount: null, expected: 7, defaultWorkerCount: 7));
    }

    [Fact]
    public void Partitioned_RetryThenContinue_WithRetryBudget_BuildsItemRetryPipeline()
    {
        DrainEmittedAssembly(RunRetryPipelineScenario(
            "parity.retry.withbudget",
            policy: ItemErrorPolicy.RetryThenContinue,
            maxRetries: 2,
            expectPolicy: ItemErrorPolicy.RetryThenContinue,
            expectPipeline: true));
    }

    [Fact]
    public void Partitioned_RetryThenContinue_WithZeroBudget_NoItemRetryPipeline()
    {
        // RetryThenContinue but MaxRetries=0 → no pipeline (mirrors the fluent builder).
        DrainEmittedAssembly(RunRetryPipelineScenario(
            "parity.retry.nobudget",
            policy: ItemErrorPolicy.RetryThenContinue,
            maxRetries: 0,
            expectPolicy: ItemErrorPolicy.RetryThenContinue,
            expectPipeline: false));
    }

    [Fact]
    public void Partitioned_FailFastPolicy_NoItemRetryPipeline()
    {
        DrainEmittedAssembly(RunRetryPipelineScenario(
            "parity.failfast",
            policy: ItemErrorPolicy.FailFast,
            maxRetries: 3,
            expectPolicy: ItemErrorPolicy.FailFast,
            expectPipeline: false));
    }

    [Fact]
    public void NonPartitioned_WithPartitionAttributes_IgnoresThem_NoError()
    {
        DrainEmittedAssembly(RunNonPartitionedScenario("parity.nonpartitioned"));
    }

    [Fact]
    public void FluentWithParallelism_OverridesAttributeWorkerCount()
    {
        DrainEmittedAssembly(RunFluentOverrideScenario(
            "parity.fluent.workers",
            attributeWorkerCount: 4,
            attributePolicy: ItemErrorPolicy.FailFast,
            fluentWorkerCount: 9,
            fluentPolicy: null,
            expectedWorkerCount: 9,
            expectedPolicy: ItemErrorPolicy.FailFast));
    }

    [Fact]
    public void FluentWithItemErrorPolicy_OverridesAttributePolicy()
    {
        DrainEmittedAssembly(RunFluentOverrideScenario(
            "parity.fluent.policy",
            attributeWorkerCount: 4,
            attributePolicy: ItemErrorPolicy.FailFast,
            fluentWorkerCount: null,
            fluentPolicy: ItemErrorPolicy.ContinueOnError,
            expectedWorkerCount: 4,
            expectedPolicy: ItemErrorPolicy.ContinueOnError));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunWorkerCountScenario(string name, int? attributeWorkerCount, int expected, int defaultWorkerCount = 1)
    {
        var (emitted, alc) = EmitJobAssembly(name, partitioned: true, attributeWorkerCount, itemErrorPolicy: null);
        try
        {
            var registry = new JobDefinitionRegistry();
            var options = new UKBatchOptions
            {
                DefaultPartitionWorkerCount = defaultWorkerCount,
                AdditionalAssembliesToScan = new[] { emitted },
            };

            AttributeJobDiscovery.DiscoverAndRegister(new ServiceCollection(), registry, options);

            var def = registry.TryGet(name);
            def.Should().NotBeNull();
            def!.IsPartitioned.Should().BeTrue();
            def.PartitionWorkerCount.Should().Be(expected);
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunRetryPipelineScenario(
        string name, ItemErrorPolicy policy, int maxRetries, ItemErrorPolicy expectPolicy, bool expectPipeline)
    {
        var (emitted, alc) = EmitJobAssembly(name, partitioned: true, partitionWorkerCount: 2, policy);
        try
        {
            var registry = new JobDefinitionRegistry();
            // Drive the retry budget through the runtime default (discovery folds attr.MaxRetries
            // ?? options.DefaultMaxRetries identically; the nullable attribute property cannot be emitted).
            var options = new UKBatchOptions { DefaultMaxRetries = maxRetries, AdditionalAssembliesToScan = new[] { emitted } };

            AttributeJobDiscovery.DiscoverAndRegister(new ServiceCollection(), registry, options);

            var def = registry.TryGet(name);
            def.Should().NotBeNull();
            def!.ItemErrorPolicy.Should().Be(expectPolicy);
            var pipeline = registry.TryGetItemRetryPipeline(name);
            if (expectPipeline)
            {
                pipeline.Should().NotBeNull("RetryThenContinue with a positive retry budget builds the cached item-retry pipeline");
            }
            else
            {
                pipeline.Should().BeNull("no pipeline is built for FailFast/ContinueOnError or a zero retry budget");
            }
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunNonPartitionedScenario(string name)
    {
        // A plain IJob carrying the partition knobs: discovery must register it with PartitionWorkerCount 0
        // and no pipeline, and must not throw — even with a non-zero default retry budget present.
        var (emitted, alc) = EmitJobAssembly(name, partitioned: false, partitionWorkerCount: 4, itemErrorPolicy: ItemErrorPolicy.RetryThenContinue);
        try
        {
            var registry = new JobDefinitionRegistry();
            var options = new UKBatchOptions { DefaultMaxRetries = 3, AdditionalAssembliesToScan = new[] { emitted } };

            Action act = () => AttributeJobDiscovery.DiscoverAndRegister(new ServiceCollection(), registry, options);
            act.Should().NotThrow();

            var def = registry.TryGet(name);
            def.Should().NotBeNull();
            def!.IsPartitioned.Should().BeFalse();
            def.PartitionWorkerCount.Should().Be(0, "the partition attributes are inert on a non-partitioned job");
            registry.TryGetItemRetryPipeline(name).Should().BeNull();
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunFluentOverrideScenario(
        string name,
        int attributeWorkerCount,
        ItemErrorPolicy attributePolicy,
        int? fluentWorkerCount,
        ItemErrorPolicy? fluentPolicy,
        int expectedWorkerCount,
        ItemErrorPolicy expectedPolicy)
    {
        // The type is emitted into a collectible context and registered through the JobBuilder
        // DIRECTLY (no process-wide discovery scan), so the [Job] seed is read in the builder ctor
        // and the fluent calls below must override it. Construction-time isolation means this type
        // never contaminates sibling discoveries.
        var (emitted, alc) = EmitJobAssembly(name, partitioned: true, attributeWorkerCount, attributePolicy);
        try
        {
            var jobType = emitted.GetTypes().Single(t => !t.IsAbstract && typeof(IPartitionedJobMarker).IsAssignableFrom(t));
            var registry = new JobDefinitionRegistry();
            var builder = new JobBuilder(new ServiceCollection(), jobType, isPartitioned: true, registry);

            if (fluentWorkerCount is { } fwc)
            {
                builder.WithParallelism(fwc);
            }
            if (fluentPolicy is { } fp)
            {
                builder.WithItemErrorPolicy(fp);
            }
            builder.Apply(new UKBatchOptions());

            var def = registry.TryGet(name);
            def.Should().NotBeNull();
            def!.PartitionWorkerCount.Should().Be(expectedWorkerCount);
            def.ItemErrorPolicy.Should().Be(expectedPolicy);
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static void DrainEmittedAssembly(WeakReference alcRef)
    {
        for (var i = 0; i < 10 && alcRef.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        alcRef.IsAlive.Should().BeFalse(
            "the emitted probe assembly must be fully collected before another test scans the loaded-assembly list");
    }
#endif
}
