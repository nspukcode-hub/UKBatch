using System.Reflection;
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
public class AttributeCronValidationTests
{
    // Six-field: valid under IncludeSeconds (default), invalid under Standard (five-field grammar).
    private const string SixFieldDailySchedule = "0 0 0 * * *";

#if NET9_0_OR_GREATER
    private static (Assembly Assembly, AssemblyLoadContext Context) EmitScheduledJobAssembly()
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
        tb.SetCustomAttribute(new CustomAttributeBuilder(
            attrCtor,
            Array.Empty<object>(),
            new[] { nameProp, scheduleProp },
            new object[] { "discovery.cron.probe", SixFieldDailySchedule }));
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
        }
        finally
        {
            alc.Unload();
            // Evict the emitted assembly before the next (sequential) test scans loaded assemblies.
            for (var i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }

    [Fact]
    public void DiscoverAndRegister_AttributeCronValidUnderFormat_Registers()
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
        }
        finally
        {
            alc.Unload();
            for (var i = 0; i < 5; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }
    }
#endif
}
