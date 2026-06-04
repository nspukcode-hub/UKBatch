using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Endpoints;
using UKBatch.Transport.Http.Receiver;
using UKBatch.Transport.Http.Resilience;

namespace UKBatch.Transport.Http;

/// <summary>Entry point for <see cref="UKBatch.Transport.Http"/> DI registration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the HTTP transport adapter and replaces any prior <see cref="ITransport"/>
    /// registration. Binds <see cref="HttpTransportOptions"/> from
    /// <c>UKBatch:Transport:Http</c>. Optional <paramref name="configure"/> overlays the bound
    /// values programmatically.
    /// </summary>
    /// <remarks>
    /// <para><b>Idempotent:</b> calling twice is a no-op on the second invocation
    /// — detected via the singleton <see cref="HttpTransport"/> descriptor presence.</para>
    /// <para><b>Orphan removal:</b> <c>AddUKBatch</c>'s <c>UseInProcessTransport</c> default
    /// registers BOTH <see cref="InProcessTransport"/> singleton AND the
    /// <see cref="ITransport"/> factory. After we replace the factory, the concrete
    /// <see cref="InProcessTransport"/> singleton lingers as an unreachable zombie. We remove it now.
    /// </para>
    /// <para><b>Receiver-side:</b> this method registers ONLY the DI. To accept inbound requests,
    /// also call <see cref="EndpointRouteBuilderExtensions.MapUKBatchHttpTransport"/> on your
    /// <c>WebApplication</c>.</para>
    /// </remarks>
    public static IServiceCollection AddUKBatchHttpTransport(
        this IServiceCollection services,
        Action<HttpTransportOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard.
        if (services.Any(d => d.ServiceType == typeof(HttpTransport)))
        {
            return services;
        }

        // Options binding — appsettings section + optional programmatic overlay.
        var optionsBuilder = services.AddOptions<HttpTransportOptions>()
            .BindConfiguration("UKBatch:Transport:Http");
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.TryAddSingleton<IValidateOptions<HttpTransportOptions>, HttpTransportOptionsValidator>();

        // HMAC primitives.
        services.TryAddSingleton<HmacSignatureService>();
        services.TryAddSingleton<NonceDedupeCache>(sp =>
            new NonceDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.NonceCacheCapacity));
        services.TryAddSingleton<MessageIdDedupeCache>(sp =>
            new MessageIdDedupeCache(sp.GetRequiredService<IOptions<HttpTransportOptions>>().Value.MessageIdCacheCapacity));
        services.TryAddSingleton<HmacAuthorizationFilter>();

        // Receiver pump — singleton bridge between publish/poll endpoints and in-process consumers.
        services.TryAddSingleton<HttpTransportReceiver>();

        // Named-client + Polly resilience pipeline.
        PollyResilienceHandlerSetup.RegisterNamedClients(services);

        // Concrete HttpTransport singleton — registered via explicit factory because the ctor is
        // `internal` (direct construction unsupported). DI activator cannot find
        // an internal ctor under `BuildServiceProvider(validateScopes:true)`.
        services.TryAddSingleton<HttpTransport>(sp => new HttpTransport(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<IOptions<HttpTransportOptions>>(),
            sp.GetRequiredService<HttpTransportReceiver>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HttpTransport>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<IServiceDiscovery>()));

        // Remove orphan InProcessTransport singleton BEFORE replacing the ITransport factory.
        var orphan = services.FirstOrDefault(d => d.ServiceType == typeof(InProcessTransport));
        if (orphan is not null)
        {
            services.Remove(orphan);
        }

        // A3: last-registered-wins. Replace any prior ITransport factory.
        services.Replace(ServiceDescriptor.Singleton<ITransport>(
            sp => sp.GetRequiredService<HttpTransport>()));

        return services;
    }

    /// <summary>
    /// Overload taking an <see cref="IConfigurationSection"/> for explicit-section binding (rarely
    /// needed since the parameterless overload already binds <c>UKBatch:Transport:Http</c>).
    /// </summary>
    public static IServiceCollection AddUKBatchHttpTransport(
        this IServiceCollection services,
        IConfigurationSection configurationSection)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configurationSection);

        // Caller-supplied section takes precedence over the convention path.
        services.Configure<HttpTransportOptions>(configurationSection);
        return services.AddUKBatchHttpTransport(configure: null);
    }
}
