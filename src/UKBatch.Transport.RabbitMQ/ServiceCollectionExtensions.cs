using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Dedupe;
using UKBatch.Transport.RabbitMQ.Receiver;
using UKBatch.Transport.RabbitMQ.Rpc;

namespace UKBatch.Transport.RabbitMQ;

/// <summary>Entry point for <see cref="UKBatch.Transport.RabbitMQ"/> DI registration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the RabbitMQ transport adapter and replaces any prior <see cref="ITransport"/>
    /// registration. Binds <see cref="RabbitMqTransportOptions"/> from
    /// <c>UKBatch:Transport:RabbitMQ</c>; optional <paramref name="configure"/> overlays the bound
    /// values programmatically. Registers the consumer pump as an <c>IHostedService</c> so the worker
    /// starts consuming its durable service queue at host start.
    /// </summary>
    /// <remarks>
    /// <para><b>Idempotent:</b> calling twice is a no-op on the second invocation
    /// — detected via the singleton <see cref="RabbitMqTransport"/> descriptor presence. The orphan
    /// removal + <c>ITransport</c> replace run AFTER the guard, so a second call neither double-removes
    /// nor re-replaces.</para>
    /// <para><b>Orphan removal:</b> <c>AddUKBatch</c>'s in-process default registers BOTH the
    /// <see cref="InProcessTransport"/> singleton AND the <see cref="ITransport"/> factory. After we
    /// replace the factory the concrete <see cref="InProcessTransport"/> lingers as an unreachable
    /// zombie — we remove it (mirrors the HTTP transport).</para>
    /// <para><b>Prerequisite:</b> call AFTER <c>AddUKBatch</c> — the consumer pump depends on
    /// <c>IServiceScopeFactory</c>, <c>IJobRunner</c>, <c>IJobExecutionAwaiter</c> and
    /// <c>IJobDefinitionLookup</c> registered by the core.</para>
    /// </remarks>
    public static IServiceCollection AddUKBatchRabbitMqTransport(
        this IServiceCollection services,
        Action<RabbitMqTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard. All mutation below is gated behind this.
        if (services.Any(d => d.ServiceType == typeof(RabbitMqTransport)))
        {
            return services;
        }

        // Options binding — appsettings section + optional programmatic overlay.
        var optionsBuilder = services.AddOptions<RabbitMqTransportOptions>()
            .BindConfiguration("UKBatch:Transport:RabbitMQ");
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton<IValidateOptions<RabbitMqTransportOptions>, RabbitMqTransportOptionsValidator>();

        // Connection owner + RPC reply router + receiver-side dedupe.
        services.TryAddSingleton<RabbitMqConnectionManager>();
        services.TryAddSingleton<RabbitMqReplyRouter>();
        services.TryAddSingleton<MessageIdDedupeCache>(sp =>
            new MessageIdDedupeCache(
                sp.GetRequiredService<IOptions<RabbitMqTransportOptions>>().Value.MessageIdCacheCapacity));

        // Concrete transport singleton (public ctor — direct activation is fine).
        services.TryAddSingleton<RabbitMqTransport>();

        // Worker-side consumer pump: StartAsync connects + declares + consumes the service queue.
        services.AddHostedService<RabbitMqConsumerPump>();

        // Remove orphan InProcessTransport singleton BEFORE replacing the ITransport factory.
        var orphan = services.FirstOrDefault(d => d.ServiceType == typeof(InProcessTransport));
        if (orphan is not null)
        {
            services.Remove(orphan);
        }

        // Last-registered-wins. Replace any prior ITransport factory.
        services.Replace(ServiceDescriptor.Singleton<ITransport>(
            sp => sp.GetRequiredService<RabbitMqTransport>()));

        return services;
    }

    /// <summary>
    /// Overload taking an <see cref="IConfigurationSection"/> for explicit-section binding (rarely
    /// needed since the parameterless overload already binds <c>UKBatch:Transport:RabbitMQ</c>).
    /// </summary>
    public static IServiceCollection AddUKBatchRabbitMqTransport(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationSection);

        services.Configure<RabbitMqTransportOptions>(configurationSection);
        return services.AddUKBatchRabbitMqTransport(configure: null);
    }
}
