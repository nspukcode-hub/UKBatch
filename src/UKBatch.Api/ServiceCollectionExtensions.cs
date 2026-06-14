using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using UKBatch.Api.Hub;
#if NET10_0_OR_GREATER
using UKBatch.Api.OpenApi;
#endif
using UKBatch.Api.Workers;
using UKBatch.AspNetCore.Triggering;

namespace UKBatch.Api;

/// <summary>Entry point for UKBatch.Api DI registration. CALL AFTER <c>AddUKBatchAspNetCore</c>.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers REST endpoint handlers, the SignalR hub + fan-out IHostedService, OpenAPI defaults,
    /// and the JSON enum converter. Does NOT call <c>AddUKBatch</c> — the caller MUST have already
    /// registered the runtime (via <c>AddUKBatch</c> or <c>AddUKBatchAspNetCore</c>).
    /// </summary>
    /// <remarks>
    /// <para>The package is auth-agnostic — no <c>AddAuthorizationBuilder</c> call. Apply
    /// <c>RequireAuthorization</c> on the route group at <c>MapUKBatchApi</c> time to enforce auth.</para>
    /// <para>Throws <see cref="InvalidOperationException"/> if <c>AddUKBatchAspNetCore</c> has not been
    /// called (detected via the absence of <c>IJobTriggerContext</c> registration).</para>
    /// <para><b>Idempotent:</b> calling twice is a no-op on the second
    /// invocation — mirrors the <c>AddUKBatchAspNetCore</c> double-registration guard. Without
    /// this guard, the second call would register a second <c>IHostedService</c> factory pointing
    /// at the same singleton, causing <c>StartAsync</c> to fire twice and leak three pump tasks.</para>
    /// </remarks>
    public static IServiceCollection AddUKBatchApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Idempotency guard — detect prior registration via the
        // singleton JobStatusHubFanout descriptor (unique to this method).
        if (services.Any(d => d.ServiceType == typeof(JobStatusHubFanout)))
        {
            return services;
        }

        // Fail-fast: REST endpoints depend on the AddUKBatchAspNetCore services.
        var hasAspNetCore = services.Any(d => d.ServiceType == typeof(IJobTriggerContext));
        if (!hasAspNetCore)
        {
            throw new InvalidOperationException(
                "AddUKBatchApi requires AddUKBatchAspNetCore (or AddUKBatch + AddUKBatchAspNetCore) to be registered first.");
        }

        // RFC 7807 ProblemDetails for failed responses (binding/validation 400s, unhandled 500s).
        // Idempotent: AddProblemDetails registers IProblemDetailsService via TryAdd, so a host that
        // also calls it is unaffected.
        services.AddProblemDetails();

        services.AddSignalR();

        // JSON enum-as-string for REST + hub serialization (OpenAPI consistency).
        services.ConfigureHttpJsonOptions(opts =>
        {
            opts.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

#if NET10_0_OR_GREATER
        // OpenAPI registration — ships defaults. No RequireAuthorizationOperationTransformer stub:
        // the README documents the recipe for users to add Bearer / API-key security schemes.
        // The built-in OpenAPI document generator requires net9+, so on net8.0 the package ships
        // REST + SignalR without document generation (enum-as-string serialization is unaffected).
        services.AddOpenApi(opts =>
        {
            opts.AddDocumentTransformer<ServersTransformer>();
            opts.AddOperationTransformer<ProblemDetailsResponseTransformer>();
            opts.AddSchemaTransformer<EnumStringTransformer>();
        });
#endif

        // Hub + fan-out lifetime.
        services.AddSingleton<JobStatusHubFanout>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(sp => sp.GetRequiredService<JobStatusHubFanout>());

        // Live, in-memory worker registry feeding /api/workers/* (observability only —
        // NEVER consulted for dispatch). Registered AFTER the idempotency early-return above so a
        // double AddUKBatchApi call does not double-register.
        services.TryAddSingleton<IWorkerRegistry, InMemoryWorkerRegistry>();

        return services;
    }
}
