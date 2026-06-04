using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UKBatch.AspNetCore.HealthChecks;
using UKBatch.AspNetCore.Triggering;
using UKBatch.Builders;

namespace UKBatch.AspNetCore;

/// <summary>Entry point for ASP.NET Core integration of UKBatch.</summary>
public static class ServiceCollectionExtensions
{
    // UKBatchHost is internal to UKBatch.Core — resolved via reflection so this package does not
    // need to amend UKBatch.Core. The full type name is stable across alpha versions.
    // Hardening: if the type cannot be resolved (Core present but type relocated or removed in
    // a future Core version), fail-fast at the first AddUKBatchAspNetCore call instead of silently
    // disabling the double-registration guard.
    private static readonly Type? UKBatchHostType =
        Type.GetType("UKBatch.Runtime.UKBatchHost, UKBatch.Core", throwOnError: false);

    // CA1861: avoid allocating a fresh array on every invocation by hoisting the tag list.
    private static readonly string[] HealthCheckTags = { "ukbatch", "ready" };

    /// <summary>
    /// Registers the UKBatch runtime AND the ASP.NET-Core-specific services (HttpContext enricher,
    /// trace propagation, readiness health check). If <paramref name="configure"/> is non-null this
    /// method also calls <see cref="UKBatch.ServiceCollectionExtensions.AddUKBatch"/> with the same
    /// callback. If <paramref name="configure"/> is null, the caller is expected to have called
    /// <c>services.AddUKBatch(...)</c> separately — only the AspNetCore-specific services are added.
    /// </summary>
    /// <remarks>
    /// <para>Registration order (idempotent for the AspNetCore-specific services):</para>
    /// <list type="number">
    ///   <item><c>services.AddHttpContextAccessor()</c> (idempotent).</item>
    ///   <item>UKBatch core via <c>AddUKBatch(configure)</c> if a callback was supplied — guarded
    ///         against double-registration (throws <see cref="InvalidOperationException"/> if
    ///         <c>AddUKBatch</c> was already called and <paramref name="configure"/> is non-null).</item>
    ///   <item><c>HttpContextJobTriggerContext</c> as a singleton concrete + two service descriptors:
    ///         <c>IJobTriggerContext</c> and <c>IJobTraceContext</c> both resolve to the same instance.</item>
    ///   <item><c>UKBatchHealthCheck</c> registered via
    ///         <c>services.AddHealthChecks().AddCheck&lt;UKBatchHealthCheck&gt;("ukbatch", tags: ["ukbatch", "ready"])</c>.</item>
    /// </list>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Thrown when <paramref name="configure"/> is non-null AND <c>AddUKBatch</c> has already been
    /// registered. Pick ONE call site for core registration: either
    /// <c>AddUKBatchAspNetCore(configure)</c> as the single entry, OR
    /// <c>AddUKBatch(configure) + AddUKBatchAspNetCore()</c> (no args on the bridge).
    /// </exception>
    public static IServiceCollection AddUKBatchAspNetCore(
        this IServiceCollection services,
        Action<UKBatchBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            // If the UKBatchHost type cannot be located in the loaded UKBatch.Core, refuse to
            // proceed silently — that would disable the double-registration guard and risk a double
            // host registration. Hard-fail with an actionable message instead.
            if (UKBatchHostType is null)
            {
                throw new InvalidOperationException(
                    "UKBatch.AspNetCore could not resolve 'UKBatch.Runtime.UKBatchHost, UKBatch.Core' " +
                    "in the loaded UKBatch.Core assembly. The loaded UKBatch.Core version is incompatible " +
                    "with this UKBatch.AspNetCore build — update to a matching UKBatch.Core version.");
            }

            // Double-registration guard: refuse to layer a second AddUKBatch on top of an existing one.
            var alreadyRegistered = false;
            foreach (var d in services)
            {
                if (d.ServiceType == typeof(IHostedService) &&
                    d.ImplementationType == UKBatchHostType)
                {
                    alreadyRegistered = true;
                    break;
                }
            }
            if (alreadyRegistered)
            {
                throw new InvalidOperationException(
                    "AddUKBatchAspNetCore was called with a configure callback after AddUKBatch had already been registered. " +
                    "Choose one: call AddUKBatchAspNetCore(configure) as the single entry, " +
                    "or call AddUKBatch(configure) + AddUKBatchAspNetCore() (no args).");
            }
            services.AddUKBatch(configure);
        }

        services.AddHttpContextAccessor();

        // One concrete, two interface descriptors. Both interfaces resolve to the
        // SAME singleton instance via factory delegates that forward to the concrete singleton.
        services.TryAddSingleton<HttpContextJobTriggerContext>();
        services.TryAddSingleton<IJobTriggerContext>(sp => sp.GetRequiredService<HttpContextJobTriggerContext>());
        services.TryAddSingleton<IJobTraceContext>(sp => sp.GetRequiredService<HttpContextJobTriggerContext>());

        services.AddOptions<UKBatchHealthCheckOptions>();
        services
            .AddHealthChecks()
            .AddCheck<UKBatchHealthCheck>("ukbatch", tags: HealthCheckTags);

        return services;
    }
}
