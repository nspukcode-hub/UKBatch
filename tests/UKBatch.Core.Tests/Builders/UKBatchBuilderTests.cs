using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Builders;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Registry;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Builders;

/// <summary>
/// Builder + DI registration smoke tests. Verifies:
/// - the default storage / transport registration
/// - per-job registration via AddJob
/// cron expressions validated at Complete-time against final options (post-audit)
/// </summary>
public class UKBatchBuilderTests
{
    [Fact]
    public void AddUKBatch_RegistersDefaultInMemoryServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(_ => { });

        using var sp = services.BuildServiceProvider();
        sp.GetService<IJobStore>().Should().NotBeNull();
        sp.GetService<IJobExecutionReader>().Should().NotBeNull();
        sp.GetService<IJobExecutionWriter>().Should().NotBeNull();
        sp.GetService<IBatchDefinitionStore>().Should().NotBeNull();
        sp.GetService<ITransport>().Should().NotBeNull();
        sp.GetService<IApprovalGateService>().Should().NotBeNull();
        sp.GetService<IJobRunner>().Should().NotBeNull();
        sp.GetService<IValidateOptions<UKBatchOptions>>().Should().NotBeNull();
    }

    [Fact]
    public void AddJob_RegistersJobInRegistry()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b => b.AddJob<SucceedingJob>().Named("my.job"));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<JobDefinitionRegistry>();
        registry.TryGet("my.job").Should().NotBeNull();
        registry.TryGetImplementationType("my.job").Should().Be<SucceedingJob>();
    }

    [Fact]
    public void AddJob_DuplicateName_ThrowsOnRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        Action act = () => services.AddUKBatch(b =>
        {
            b.AddJob<SucceedingJob>().Named("dup");
            b.AddJob<FailingJob>().Named("dup");
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*already registered*");
    }

    [Fact]
    public void AddJob_OrderIndependentConfigureWithSchedule_DefersValidation()
    {
        // WithSchedule defers cron-format validation to Complete-time so
        // a Configure(...) call AFTER AddJob still takes effect.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        // Use a 5-field expression that's INVALID under IncludeSeconds (default) but VALID under Standard.
        // Order: AddJob first, then Configure to switch CronFormat to Standard.
        Action act = () => services.AddUKBatch(b =>
        {
            b.AddJob<SucceedingJob>().Named("ordered.cron").WithSchedule("0 0 * * *"); // 5-field
            b.Configure(o => o.CronFormat = Cronos.CronFormat.Standard);
        });
        act.Should().NotThrow();
    }

    [Fact]
    public void AddJob_InvalidCronAgainstFinalFormat_ThrowsAtCompleteTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        Action act = () => services.AddUKBatch(b =>
        {
            b.AddJob<SucceedingJob>().Named("bad.cron").WithSchedule("totally not a cron");
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Invalid cron expression*");
    }

    [Fact]
    public void AddBatch_InvalidConfiguration_ThrowsAtCompleteTime()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        // Empty batch — no steps — should fail validation at Complete time.
        Action act = () => services.AddUKBatch(b =>
        {
            b.AddBatch("empty.batch", _ => { });
        });
        act.Should().Throw<InvalidOperationException>().WithMessage("*Batch 'empty.batch'*");
    }

    [Fact]
    public void Configure_AppliesOptionsToServiceProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b =>
        {
            b.Configure(o =>
            {
                o.MaxDegreeOfParallelism = 6;
                o.DefaultMaxRetries = 7;
            });
        });

        using var sp = services.BuildServiceProvider();
        var opts = sp.GetRequiredService<IOptions<UKBatchOptions>>();
        opts.Value.MaxDegreeOfParallelism.Should().Be(6);
        opts.Value.DefaultMaxRetries.Should().Be(7);
    }

    // ===== — IBatchDefinitionLookup DI integration tests =====

    [Fact]
    public void AddBatch_DuplicateName_ThrowsDuringComplete()
    {
        // Test #14 — second AddBatch("foo",...) registration triggers name-collision throw
        // inside BatchDefinitionRegistry.Register at Complete time.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        Action act = () => services.AddUKBatch(b =>
        {
            b.AddBatch("foo", x => x.RunJob<SucceedingJob>());
            b.AddBatch("foo", x => x.RunJob<SucceedingJob>());
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*foo*already registered*");
    }

    [Theory]
    [InlineData("   ")]
    [InlineData("\t")]
    [InlineData("\n")]
    public void AddBatch_NameWhitespace_ThrowsImmediately(string whitespaceName)
    {
        // Test #15 — whitespace-only name rejected synchronously at AddBatch call site
        // (promoted from ThrowIfNullOrEmpty to ThrowIfNullOrWhiteSpace).
        var services = new ServiceCollection();
        var builder = new UKBatchBuilder_AccessForTest(services);

        Action act = () => builder.AddBatch(whitespaceName, _ => { });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void AddBatch_LookupResolves_AfterAddUKBatch()
    {
        // Test #16 — IBatchDefinitionLookup resolves the registered batch by name; the returned
        // BatchDefinition.Id must match what JobRunner.TriggerBatchAsync would accept.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b => b.AddBatch("nightly", x => x.RunJob<SucceedingJob>()));

        using var sp = services.BuildServiceProvider();
        var lookup = sp.GetRequiredService<IBatchDefinitionLookup>();
        var def = lookup.TryGetByName("nightly");
        def.Should().NotBeNull();
        def!.Name.Should().Be("nightly");
        def.Id.Should().NotBeNullOrWhiteSpace();
        // Lookup-by-id symmetry — same instance, same id.
        lookup.TryGetById(def.Id)!.Name.Should().Be("nightly");
    }

    [Fact]
    public void AddBatch_DI_LookupAndRegistryAreSameSingleton()
    {
        // Test #16b — factory-registration of IBatchDefinitionLookup must resolve to the
        // SAME singleton instance as the concrete BatchDefinitionRegistry, otherwise the
        // interface would expose an empty registry while runtime mutates a different one.
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b => b.AddBatch("foo", x => x.RunJob<SucceedingJob>()));

        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<BatchDefinitionRegistry>();
        var lookup = sp.GetRequiredService<IBatchDefinitionLookup>();

        Assert.Same(registry, lookup);
    }

    [Fact]
    public void Register_AllRegisteredDefinitions_HaveSourceCode()
    {
        // Every definition reachable via IBatchDefinitionLookup must have BatchSource.Code
        // (the lookup is intentionally code-only; store-defined batches live in
        // IBatchDefinitionStore).
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostApplicationLifetime>(new TestHostLifetime());

        services.AddUKBatch(b =>
        {
            b.AddBatch("a", x => x.RunJob<SucceedingJob>());
            b.AddBatch("b", x => x.RunJob<SucceedingJob>());
            b.AddBatch("c", x => x.RunJob<SucceedingJob>());
        });

        using var sp = services.BuildServiceProvider();
        var lookup = sp.GetRequiredService<IBatchDefinitionLookup>();
        var all = lookup.All();
        all.Should().HaveCount(3);
        Assert.All(all, def => Assert.Equal(BatchSource.Code, def.Source));
    }

    // Helper subclass to access the internal UKBatchBuilder constructor for test #15. The
    // production AddUKBatch path would also work, but test #15 specifically asserts the
    // throw is SYNCHRONOUS — i.e. not deferred to Complete — so we want the throw to
    // happen on the AddBatch line itself, not via the AddUKBatch wrapper.
    private sealed class UKBatchBuilder_AccessForTest
    {
        private readonly UKBatchBuilder _inner;
        public UKBatchBuilder_AccessForTest(IServiceCollection services)
        {
            // Use the public AddUKBatch entry to construct the builder, then capture the
            // builder reference from inside the configure lambda. This is the only public
            // path to a UKBatchBuilder instance.
            UKBatchBuilder? captured = null;
            try
            {
                services.AddUKBatch(b =>
                {
                    captured = b;
                    // Throw immediately to short-circuit Complete (we only need the builder).
                    throw new TestBuilderCapturedException();
                });
            }
            catch (TestBuilderCapturedException) { /* expected */ }
            _inner = captured ?? throw new InvalidOperationException("Could not capture UKBatchBuilder.");
        }

        public UKBatchBuilder AddBatch(string name, Action<BatchBuilder> configure)
            => _inner.AddBatch(name, configure);

        private sealed class TestBuilderCapturedException : Exception { }
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
