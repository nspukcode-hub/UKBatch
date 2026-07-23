using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using UKBatch.AspNetCore;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Security;
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
    /// <para><b>Auth-agnostic:</b> no <c>AddAuthentication</c> call. Caller mounts the route group with
    /// <c>.RequireAuthorization()</c> for production. The UI authorization stack (cascading auth state +
    /// role policies) is always registered so the authorization views render; with no authentication
    /// integration present it reports an authenticated all-roles principal, keeping every control visible
    /// (the open default).</para>
    /// <para><b>Per-user authentication:</b> when an authentication integration has registered an
    /// <c>IUKBatchUserTokenAccessor</c> BEFORE this call, the dashboard binds a REST + hub client per
    /// circuit that forwards the signed-in user's token, and does not register the shared-identity
    /// startup conductor. Register the integration first (the server + workers host orders it so).</para>
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

        // Idempotency guard — a dedicated marker so re-registration is detected in both the
        // shared-identity path (which registers the conductor) and the per-user path (which does not).
        if (services.Any(d => d.ServiceType == typeof(DashboardRegistrationMarker)))
        {
            return services;
        }
        services.AddSingleton<DashboardRegistrationMarker>();

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

        services.TryAddSingleton<IUKBatchServiceRegistry, StaticServiceRegistry>();

        // Client wiring branches on whether an authentication integration is present. Its token accessor
        // is the "per-user auth" signal; it must be registered BEFORE this call (the server + workers
        // host orders AddUKBatchOpenIdConnect first). The PerUserAuthentication option is applied via
        // PostConfigure, so it is not yet readable here — presence of the accessor is.
        var perUserAuth = services.Any(d => d.ServiceType == typeof(IUKBatchUserTokenAccessor));

        if (perUserAuth)
        {
            // One REST + hub client per circuit so each socket and forwarded bearer carry the signed-in
            // user's identity. No conductor / hosted service: at boot no user is present, so an eager
            // connect to a gated hub would loop on 401.
            services.AddScoped<IUKBatchClientFactory, PerCircuitUKBatchClientProvider>();
        }
        else
        {
            // Shared machine-identity clients connected once at startup by the conductor.
            services.TryAddSingleton<IUKBatchClientFactory, UKBatchClientFactory>();
            services.TryAddSingleton<UKBatchServiceConductor>();
            services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<UKBatchServiceConductor>());
        }

        // Razor Components + Interactive Server.
        services.AddRazorComponents()
            .AddInteractiveServerComponents();

        // UI authorization stack — always registered so the authorization views render in both modes.
        services.AddCascadingAuthenticationState();
        services.AddAuthorizationCore();
        if (!perUserAuth)
        {
            // Auth-off: report an authenticated principal and grant the role policies unconditionally so
            // every control renders, preserving the open default. An authentication integration provides
            // the real principal and policies instead (and is detected above).
            services.AddScoped<AuthenticationStateProvider, PermitAllAuthenticationStateProvider>();
            services.Configure<AuthorizationOptions>(options =>
            {
                options.AddPolicy("UKBatch:Viewer", policy => policy.RequireAssertion(_ => true));
                options.AddPolicy("UKBatch:Operator", policy => policy.RequireAssertion(_ => true));
            });
        }

        // Per-circuit scoped state + notifications.
        services.TryAddScoped<IDashboardState, DashboardState>();
        services.TryAddScoped<INotificationService, NotificationService>();

        return services;
    }

    /// <summary>Marker singleton used to make <see cref="AddUKBatchDashboard"/> idempotent.</summary>
    private sealed class DashboardRegistrationMarker;
}
