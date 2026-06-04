using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Dedupe;
using UKBatch.Transport.RabbitMQ.Receiver;
using UKBatch.Transport.RabbitMQ.Rpc;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.DI;

/// <summary>
/// <c>AddUKBatchRabbitMqTransport</c> DI wiring. Idempotency guard, orphan
/// <see cref="InProcessTransport"/> removal, last-registered-wins <see cref="ITransport"/> replace,
/// hosted-service + collaborator registration, options binding. Docker-free (no broker contact).
/// </summary>
public sealed class ServiceCollectionExtensionsTests
{
    private static IServiceCollection BuildBaseServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:RabbitMQ:HostName"] = "localhost",
            })
            .Build());
        return services;
    }

    // ===== Idempotency guard =====

    [Fact]
    public void AddUKBatchRabbitMqTransport_CalledTwice_RegistersTransportOnce()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();
        var afterFirst = services.Count(d => d.ServiceType == typeof(RabbitMqTransport));
        services.AddUKBatchRabbitMqTransport();
        var afterSecond = services.Count(d => d.ServiceType == typeof(RabbitMqTransport));

        afterFirst.Should().Be(1);
        afterSecond.Should().Be(1, "idempotency guard prevents double registration");
    }

    [Fact]
    public void AddUKBatchRabbitMqTransport_CalledTwice_RegistersHostedServiceOnce()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();
        services.AddUKBatchRabbitMqTransport();

        // The pump is registered as an IHostedService implementation; the guard must keep it singular.
        services.Count(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RabbitMqConsumerPump)).Should().Be(1);
    }

    [Fact]
    public void AddUKBatchRabbitMqTransport_NullServices_Throws()
    {
        IServiceCollection services = null!;
        var act = () => services.AddUKBatchRabbitMqTransport();
        act.Should().Throw<ArgumentNullException>();
    }

    // ===== Orphan InProcessTransport removal =====

    [Fact]
    public void AddUKBatchRabbitMqTransport_RemovesOrphanInProcessTransportDescriptor()
    {
        var services = BuildBaseServices();
        services.AddSingleton<InProcessTransport>();
        services.Any(d => d.ServiceType == typeof(InProcessTransport)).Should().BeTrue();

        services.AddUKBatchRabbitMqTransport();

        services.Any(d => d.ServiceType == typeof(InProcessTransport)).Should().BeFalse(
 "the orphan InProcessTransport singleton descriptor is removed when RabbitMQ supersedes it");
    }

    [Fact]
    public void AddUKBatchRabbitMqTransport_NoOrphan_DoesNotThrow()
    {
        var services = BuildBaseServices();
        var act = () => services.AddUKBatchRabbitMqTransport();
        act.Should().NotThrow();
    }

    // ===== Last-registered-wins ITransport replacement =====

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_AfterInProcessRegistration_ReplacesITransport()
    {
        var services = BuildBaseServices();
        services.AddSingleton<InProcessTransport>();
        services.AddSingleton<ITransport>(sp => sp.GetRequiredService<InProcessTransport>());

        services.AddUKBatchRabbitMqTransport();

        await using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<ITransport>().Should().BeOfType<RabbitMqTransport>();
    }

    [Fact]
    public void AddUKBatchRabbitMqTransport_OnlyOneITransportDescriptor_AfterReplace()
    {
        var services = BuildBaseServices();
        services.AddSingleton<InProcessTransport>();
        services.AddSingleton<ITransport>(sp => sp.GetRequiredService<InProcessTransport>());

        services.AddUKBatchRabbitMqTransport();

        services.Count(d => d.ServiceType == typeof(ITransport)).Should().Be(1,
            "Replace swaps the single ITransport descriptor in place");
    }

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_NoPriorTransport_RegistersRabbitMqAsITransport()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();

        await using var sp = services.BuildServiceProvider();
        var transport = sp.GetRequiredService<ITransport>();
        transport.Should().BeOfType<RabbitMqTransport>();
        transport.Name.Should().Be("RabbitMQ");
    }

    // ===== Collaborator registration =====

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_RegistersAllCollaborators()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();
        await using var sp = services.BuildServiceProvider();

        sp.GetService<RabbitMqConnectionManager>().Should().NotBeNull();
        sp.GetService<RabbitMqReplyRouter>().Should().NotBeNull();
        sp.GetService<MessageIdDedupeCache>().Should().NotBeNull();
        sp.GetService<RabbitMqTransport>().Should().NotBeNull();
    }

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_RegistersOptionsValidator()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();
        await using var sp = services.BuildServiceProvider();

        sp.GetServices<IValidateOptions<RabbitMqTransportOptions>>()
            .Should().ContainSingle(v => v is RabbitMqTransportOptionsValidator);
    }

    [Fact]
    public void AddUKBatchRabbitMqTransport_RegistersHostedService()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();

        services.Should().Contain(d => d.ServiceType == typeof(IHostedService)
            && d.ImplementationType == typeof(RabbitMqConsumerPump));
    }

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_CollaboratorsAreSingletons()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport();
        await using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<RabbitMqConnectionManager>()
            .Should().BeSameAs(sp.GetRequiredService<RabbitMqConnectionManager>());
        sp.GetRequiredService<MessageIdDedupeCache>()
            .Should().BeSameAs(sp.GetRequiredService<MessageIdDedupeCache>());
    }

    // ===== Options binding =====

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_ConfigureOverlay_AppliesValues()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport(o =>
        {
            o.ExchangeName = "custom.exchange";
            o.PrefetchCount = 32;
        });
        await using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value;
        options.ExchangeName.Should().Be("custom.exchange");
        options.PrefetchCount.Should().Be((ushort)32);
    }

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_BindsConfigurationSection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:RabbitMQ:ExchangeName"] = "bound.exchange",
                ["UKBatch:Transport:RabbitMQ:MaxRedeliveryCount"] = "9",
            })
            .Build());

        services.AddUKBatchRabbitMqTransport();
        await using var sp = services.BuildServiceProvider();

        var options = sp.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value;
        options.ExchangeName.Should().Be("bound.exchange");
        options.MaxRedeliveryCount.Should().Be(9);
    }

    [Fact]
    public async Task AddUKBatchRabbitMqTransport_MessageIdCacheCapacity_FlowsToCache()
    {
        var services = BuildBaseServices();
        services.AddUKBatchRabbitMqTransport(o => o.MessageIdCacheCapacity = 128);
        await using var sp = services.BuildServiceProvider();

        // The cache factory reads MessageIdCacheCapacity off options; default 4096 would differ.
        var cache = sp.GetRequiredService<MessageIdDedupeCache>();
        cache.Should().NotBeNull();
        // Indirectly assert capacity by filling beyond it and observing eviction.
        for (var i = 0; i < 200; i++)
        {
            cache.TryAdd($"k{i}");
        }
        cache.Count.Should().BeLessThanOrEqualTo(128, "the configured capacity must cap the LRU");
    }
}
