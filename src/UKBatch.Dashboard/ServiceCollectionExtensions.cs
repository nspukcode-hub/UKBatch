using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.State;

namespace UKBatch.Dashboard;

/// <summary>Entry point for <c>UKBatch.Dashboard</c> DI registration.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Blazor Server dashboard infrastructure: options binding + validation, the
    /// REST + SignalR client factory, the hosted-service conductor (eager connect on startup),
    /// <see cref="IHttpClientFactory"/>, and Razor Components (scoped services + render mode).
    /// </summary>
    /// <remarks>
    /// <para><b>Idempotency guard:</b> if
    /// <see cref="AddUKBatchDashboard"/> has already registered (detected via
    /// <c>UKBatchServiceConductor</c> singleton presence), the second call is a NO-OP. Without
    /// the guard, two <c>IHostedService → UKBatchServiceConductor</c> registrations would fire
    /// <c>StartAsync</c> twice and leak hub connections.</para>
    /// <para><b>Auth-agnostic:</b> no <c>AddAuthentication</c> / <c>AddAuthorization</c> call.
    /// Caller mounts the route group with <c>.RequireAuthorization()</c> for production.</para>
    /// <para><b>Order of registration:</b> may be called BEFORE or AFTER <c>AddUKBatchApi</c> /
    /// <c>AddUKBatchAspNetCore</c> in embedded deployments — the dashboard NEVER calls
    /// the in-process runtime directly; ALL data flows through HTTP/SignalR. (Embedded mode is
    /// architecturally identical to the server + workers deployment; only config differs.)</para>
    /// </remarks>
    public static IServiceCollection AddUKBatchDashboard(
        this IServiceCollection services,
        Action<DashboardOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard — detect prior registration via UKBatchServiceConductor singleton.
        if (services.Any(d => d.ServiceType == typeof(UKBatchServiceConductor)))
        {
            return services;
        }

        // Options binding + validation.
        services.AddOptions<DashboardOptions>()
            .BindConfiguration("UKBatch:Dashboard");
        if (configure is not null)
        {
            services.PostConfigure(configure);
        }
        services.AddSingleton<IValidateOptions<DashboardOptions>, DashboardOptionsValidator>();

        // HttpClientFactory — default named client + per-service descriptor configurator.
        // v0.1 ships WITHOUT REST client-side retry. SignalR.WithAutomaticReconnect
        // handles hub-side resilience; for REST, latency-tolerance posture is "surface ErrorBanner,
        // operator retries". The server already retries on Polly;
        // double-retrying client side risks N×M amplification + masks server outages. v0.2 backlog:
        // re-introduce via `Microsoft.Extensions.Http.Resilience` (replaces Polly v8) when telemetry
        // justifies it.
        services.AddHttpClient("UKBatch.Dashboard.Default");

        // Per-service named client configurator (runs at first CreateClient call per name).
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureOptions<HttpClientFactoryOptions>, HttpClientFactoryNamedConfigurator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IConfigureNamedOptions<HttpClientFactoryOptions>, HttpClientFactoryNamedConfigurator>());

        // Registry + factory + conductor.
        services.TryAddSingleton<IUKBatchServiceRegistry, StaticServiceRegistry>();
        services.TryAddSingleton<IUKBatchClientFactory, UKBatchClientFactory>();
        services.TryAddSingleton<UKBatchServiceConductor>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UKBatchServiceConductor>());

        // Razor Components + Interactive Server.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // Per-circuit scoped state + notifications.
        services.TryAddScoped<IDashboardState, DashboardState>();
        services.TryAddScoped<INotificationService, NotificationService>();

        return services;
    }
}
