using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore;
using UKBatch.Storage.EntityFrameworkCore.Recovery;
using UKBatch.Storage.EntityFrameworkCore.Stores;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// <see cref="ServiceCollectionExtensions.AddUKBatchEntityFrameworkCoreStores"/> DI replacement
/// mechanics: REPLACES every InMemory store descriptor (the 6 interfaces + 2 concretes) with EF-backed
/// singletons; idempotent (×2 = one registration); the migrator (when opted) + schema-guard + reaper
/// hosted services are registered.
/// </summary>
public sealed class DiReplacementTests
{
    private static ServiceProvider BuildProvider(bool migrateOnStartup = false)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatch(_ => { });
        services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UseSqlite("DataSource=:memory:");
            o.MigrateOnStartup = migrateOnStartup;
        });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void AddEf_ReplacesAllStoreInterfaces_WithEfTypes()
    {
        using var provider = BuildProvider();

        provider.GetRequiredService<IJobStore>().Should().BeOfType<EfJobStore>();
        provider.GetRequiredService<IJobStoreInternal>().Should().BeOfType<EfJobStore>();
        provider.GetRequiredService<IJobExecutionReader>().Should().BeOfType<EfJobStore>();
        provider.GetRequiredService<IJobExecutionWriter>().Should().BeOfType<EfJobStore>();
        provider.GetRequiredService<IBatchDefinitionStore>().Should().BeOfType<EfBatchDefinitionStore>();
        provider.GetRequiredService<IApprovalGateStore>().Should().BeOfType<EfApprovalGateStore>();
    }

    [Fact]
    public void AddEf_OneSingletonBacksAllJobStoreInterfaces()
    {
        using var provider = BuildProvider();

        var byStore = provider.GetRequiredService<IJobStore>();
        var byInternal = provider.GetRequiredService<IJobStoreInternal>();
        var byReader = provider.GetRequiredService<IJobExecutionReader>();
        var byWriter = provider.GetRequiredService<IJobExecutionWriter>();

        byStore.Should().BeSameAs(byInternal).And.BeSameAs(byReader).And.BeSameAs(byWriter);
    }

    [Fact]
    public void AddEf_ConcreteInMemoryStores_AreGone()
    {
        using var provider = BuildProvider();

        provider.GetService<InMemoryJobStore>().Should().BeNull("the concrete InMemoryJobStore descriptor is removed");
        provider.GetService<InMemoryApprovalGateStore>().Should().BeNull("the concrete InMemoryApprovalGateStore descriptor is removed");
    }

    [Fact]
    public void AddEf_WatchHub_StaysTheSharedCoreSingleton()
    {
        using var provider = BuildProvider();

        // The hub is NOT removed — both stores compose the same Core singleton via IJobExecutionWatchHub.
        provider.GetRequiredService<IJobExecutionWatchHub>().Should().BeOfType<JobExecutionWatchHub>();
        var efStore = (EfJobStore)provider.GetRequiredService<IJobStore>();
        efStore.Should().NotBeNull();
    }

    [Fact]
    public void AddEf_Idempotent_SecondCallIsNoOp()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatch(_ => { });
        services.AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite("DataSource=:memory:"));
        services.AddUKBatchEntityFrameworkCoreStores(o => o.UseSqlite("DataSource=:memory:"));   // second call

        // Exactly one IDbContextFactory descriptor (the idempotency guard's sentinel).
        services.Count(d => d.ServiceType == typeof(IDbContextFactory<UKBatchDbContext>)).Should().Be(1);
        // Exactly one EfJobStore descriptor.
        services.Count(d => d.ServiceType == typeof(EfJobStore)).Should().Be(1);

        using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IJobStore>().Should().BeOfType<EfJobStore>();
    }

    // Inspect the ServiceDescriptors (NOT resolved instances) — resolving IHostedService would try to
    // activate Core's UKBatchHost, which needs IHostApplicationLifetime (only under a full Host).
    private static IServiceCollection BuildServices(bool migrateOnStartup)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatch(_ => { });
        services.AddUKBatchEntityFrameworkCoreStores(o =>
        {
            o.UseSqlite("DataSource=:memory:");
            o.MigrateOnStartup = migrateOnStartup;
        });
        return services;
    }

    [Fact]
    public void AddEf_MigrateOnStartupTrue_RegistersMigratorSchemaGuardAndReaper()
    {
        var services = BuildServices(migrateOnStartup: true);
        var hostedTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        hostedTypes.Should().Contain(typeof(EfMigrationHostedService));
        hostedTypes.Should().Contain(typeof(EfSchemaGuardHostedService));
        hostedTypes.Should().Contain(typeof(OrphanedExecutionReaper));
    }

    [Fact]
    public void AddEf_MigrateOnStartupFalse_NoMigrator_ButSchemaGuardAndReaperPresent()
    {
        var services = BuildServices(migrateOnStartup: false);
        var hostedTypes = services
            .Where(d => d.ServiceType == typeof(IHostedService))
            .Select(d => d.ImplementationType)
            .ToList();

        hostedTypes.Should().NotContain(typeof(EfMigrationHostedService), "the migrator is opt-in");
        hostedTypes.Should().Contain(typeof(EfSchemaGuardHostedService), "the schema-guard warn-log is always present");
        hostedTypes.Should().Contain(typeof(OrphanedExecutionReaper));
    }

    [Fact]
    public void AddEf_NoProviderSelected_ThrowsAtRegistration()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatch(_ => { });

        var act = () => services.AddUKBatchEntityFrameworkCoreStores(_ => { /* no UsePostgres/UseSqlite */ });
        act.Should().Throw<Microsoft.Extensions.Options.OptionsValidationException>("eager fail-fast at registration");
    }
}
