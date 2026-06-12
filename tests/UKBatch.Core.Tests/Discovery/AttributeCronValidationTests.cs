using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Discovery;
using UKBatch.Registry;
using Xunit;
#if NET9_0_OR_GREATER
using System.Reflection.Emit;
#endif

namespace UKBatch.Core.Tests.Discovery;

/// <summary>
/// Attribute-based discovery fails fast on an invalid cron in <c>[Job(Schedule = ...)]</c>, surfacing
/// the same actionable <see cref="InvalidOperationException"/> as the fluent <c>JobBuilder</c> path
/// (rather than letting a bad expression reach — and crash — host startup).
/// </summary>
/// <remarks>
/// <para>The job type is emitted into an isolated, collectible <see cref="AssemblyLoadContext"/> and
/// scanned explicitly. A statically-compiled <c>[Job(Schedule="...")]</c> type cannot be used here:
/// discovery scans every loaded assembly, so a compiled job type would also be registered by unrelated
/// hosts across the test run.</para>
/// <para>The emitted schedule is a six-field expression that is VALID under the default
/// <see cref="Cronos.CronFormat.IncludeSeconds"/> (a daily-midnight cron that never fires within a test
/// run and stays inside the scheduler's timer bound) but INVALID under <see cref="Cronos.CronFormat.Standard"/>,
/// which this test passes explicitly to drive the failure — so even while the assembly is briefly loaded
/// it cannot break sibling discoveries that use the default format. The context is unloaded and collected
/// in <c>finally</c>.</para>
/// </remarks>
// Attribute discovery scans EVERY assembly loaded in the process, so the probe assembly emitted
// here is visible to any concurrently-running AddUKBatch call. A sibling that switches the cron
// format away from the default would then validate the probe's six-field schedule against the
// wrong grammar and fail. Sharing one collection with those siblings serializes the overlap.
[Collection("process-wide attribute discovery")]
public class AttributeCronValidationTests
{
    // Six-field: valid under IncludeSeconds (default), invalid under Standard (five-field grammar).
    private const string SixFieldDailySchedule = "0 0 0 * * *";

#if NET9_0_OR_GREATER
    private static (Assembly Assembly, AssemblyLoadContext Context) EmitScheduledJobAssembly(string? jobName = "discovery.cron.probe")
    {
        var an = new AssemblyName("UKBatch.Tests.Emitted.Cron." + Guid.NewGuid().ToString("N"));
        var ab = new PersistedAssemblyBuilder(an, typeof(object).Assembly);
        var module = ab.DefineDynamicModule("main");
        var tb = module.DefineType("EmittedScheduledJob", TypeAttributes.Public | TypeAttributes.Class);
        tb.AddInterfaceImplementation(typeof(IJob));

        var executeAsync = tb.DefineMethod(
            nameof(IJob.ExecuteAsync),
            MethodAttributes.Public | MethodAttributes.Virtual,
            typeof(Task),
            new[] { typeof(JobContext), typeof(CancellationToken) });
        var il = executeAsync.GetILGenerator();
        il.Emit(OpCodes.Call, typeof(Task).GetProperty(nameof(Task.CompletedTask))!.GetGetMethod()!);
        il.Emit(OpCodes.Ret);
        tb.DefineMethodOverride(executeAsync, typeof(IJob).GetMethod(nameof(IJob.ExecuteAsync))!);

        var attrCtor = typeof(JobAttribute).GetConstructor(Type.EmptyTypes)!;
        var nameProp = typeof(JobAttribute).GetProperty(nameof(JobAttribute.Name))!;
        var scheduleProp = typeof(JobAttribute).GetProperty(nameof(JobAttribute.Schedule))!;
        // jobName == null emits [Job] WITHOUT a Name so discovery derives the type's full name.
        var props = jobName is null ? new[] { scheduleProp } : new[] { nameProp, scheduleProp };
        var values = jobName is null ? new object[] { SixFieldDailySchedule } : new object[] { jobName, SixFieldDailySchedule };
        tb.SetCustomAttribute(new CustomAttributeBuilder(attrCtor, Array.Empty<object>(), props, values));
        tb.CreateType();

        using var ms = new MemoryStream();
        ab.Save(ms);
        var bytes = ms.ToArray();

        var alc = new AssemblyLoadContext("CronProbe", isCollectible: true);
        var loaded = alc.LoadFromStream(new MemoryStream(bytes));
        return (loaded, alc);
    }

    [Fact]
    public void DiscoverAndRegister_AttributeCronInvalidUnderFormat_ThrowsInvalidOperation_LikeFluentPath()
    {
        var alcRef = RunInvalidFormatScenario();
        DrainEmittedAssembly(alcRef);
    }

    [Fact]
    public void DiscoverAndRegister_AttributeCronValidUnderFormat_Registers()
    {
        var alcRef = RunValidFormatScenario();
        DrainEmittedAssembly(alcRef);
    }

    [Fact]
    public void DiscoverAndRegister_TypeAlreadyRegisteredByBuilder_KeepsOnlyExplicitRegistration()
    {
        var alcRef = RunExplicitRegistrationWinsScenario();
        DrainEmittedAssembly(alcRef);
    }

    // The scenario bodies live in NoInlining helpers and the collect-pump runs in the CALLER:
    // in a Debug build the JIT keeps a method's locals alive until the method returns, so the
    // registry (which roots the probe's Type, which roots the load context) pins the emitted
    // assembly through any GC pump placed inside the same frame — it then stays visible to
    // AppDomain.GetAssemblies() and leaks into whichever test scans loaded assemblies next.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunInvalidFormatScenario()
    {
        var (emitted, alc) = EmitScheduledJobAssembly();
        try
        {
            var services = new ServiceCollection();
            var registry = new JobDefinitionRegistry();
            // Standard format makes the six-field schedule a parse failure → discovery must fail fast.
            var options = new UKBatchOptions
            {
                CronFormat = Cronos.CronFormat.Standard,
                AdditionalAssembliesToScan = new[] { emitted },
            };

            Action act = () => AttributeJobDiscovery.DiscoverAndRegister(services, registry, options);

            act.Should().Throw<InvalidOperationException>()
                .WithMessage("*Invalid cron expression*" + SixFieldDailySchedule + "*");
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunValidFormatScenario()
    {
        var (emitted, alc) = EmitScheduledJobAssembly();
        try
        {
            var services = new ServiceCollection();
            var registry = new JobDefinitionRegistry();
            // IncludeSeconds (default) makes the six-field schedule valid → discovery succeeds and registers.
            var options = new UKBatchOptions
            {
                CronFormat = Cronos.CronFormat.IncludeSeconds,
                AdditionalAssembliesToScan = new[] { emitted },
            };

            Action act = () => AttributeJobDiscovery.DiscoverAndRegister(services, registry, options);

            act.Should().NotThrow();
            registry.TryGet("discovery.cron.probe").Should().NotBeNull();
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static Abstractions.Models.JobDefinition Def(string name, string schedule) => new()
    {
        Name = name,
        ImplementationTypeName = typeof(object).AssemblyQualifiedName,
        IsPartitioned = false,
        Schedule = schedule,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        PartitionWorkerCount = 0,
        ItemErrorPolicy = ItemErrorPolicy.FailFast,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
        SourceService = null,
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference RunExplicitRegistrationWinsScenario()
    {
        // No attribute Name: discovery would derive the emitted type's FULL name — a different
        // name than the explicit registration below, so only the implementation-type identity can
        // prevent the duplicate. Without that check the type would be registered a second time
        // with its attribute schedule, and the scheduler would fire it twice per occurrence.
        var (emitted, alc) = EmitScheduledJobAssembly(jobName: null);
        try
        {
            var services = new ServiceCollection();
            var registry = new JobDefinitionRegistry();
            var jobType = emitted.GetTypes().Single(t => !t.IsAbstract && typeof(IJob).IsAssignableFrom(t));
            registry.Register(Def("explicit.heartbeat", SixFieldDailySchedule), jobType, null);
            var options = new UKBatchOptions { AdditionalAssembliesToScan = new[] { emitted } };

            AttributeJobDiscovery.DiscoverAndRegister(services, registry, options);

            registry.TryGet("explicit.heartbeat").Should().NotBeNull();
            registry.TryGet(jobType.FullName!).Should().BeNull(
                "a type registered explicitly through the builder must not be re-registered by discovery under its attribute-derived name");
            return new WeakReference(alc);
        }
        finally
        {
            alc.Unload();
        }
    }

    private static void DrainEmittedAssembly(WeakReference alcRef)
    {
        // Pump until the collectible context is actually collected (its assemblies then leave
        // AppDomain.GetAssemblies()). Bounded so a regression that re-roots the context fails
        // the assertion loudly instead of looping forever.
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
