using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.AspNetCore.DevAuth;

/// <summary>Opt-in registration for the development-only header-trusting authentication scheme.</summary>
public static class DevAuthServiceCollectionExtensions
{
    /// <summary>
    /// Registers a development-only authentication scheme that trusts the <c>X-Dev-User</c> /
    /// <c>X-Dev-Roles</c> request headers verbatim (no password, token, or signature verification), plus
    /// authorization. This lets the dashboard approval buttons and role-gated endpoints work in local
    /// demos without standing up a real identity provider.
    /// </summary>
    /// <remarks>
    /// <para>
    /// SECURITY: the scheme lets any caller self-assert any identity and roles. It is intended for local
    /// development and demos ONLY. A startup guard refuses to start the host in the Production environment
    /// unless <see cref="UKBatchDevAuthOptions.AllowInProduction"/> is set, and a loud warning is logged
    /// whenever the scheme is active. Use OIDC (or another real authentication scheme) in production.
    /// </para>
    /// <para>
    /// Calling this method more than once is safe: the scheme, authorization, and the startup guard are
    /// each registered at most once.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the dev-auth scheme to.</param>
    /// <param name="configure">Optional configuration for <see cref="UKBatchDevAuthOptions"/>.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddUKBatchDevAuth(
        this IServiceCollection services,
        Action<UKBatchDevAuthOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (configure is not null)
        {
            services.Configure(configure);
        }
        else
        {
            // Ensure the options instance exists even when no callback is supplied, so the startup
            // guard always resolves valid (default) options.
            services.AddOptions<UKBatchDevAuthOptions>();
        }

        // Idempotency: skip the scheme + guard registration if a previous call already added them.
        // The marker service makes a double AddUKBatchDevAuth() a no-op rather than registering the
        // scheme twice (which AddScheme would reject).
        if (services.Any(d => d.ServiceType == typeof(DevAuthRegistrationMarker)))
        {
            return services;
        }
        services.AddSingleton<DevAuthRegistrationMarker>();

        services
            .AddAuthentication(DevAuthSchemeOptions.SchemeName)
            .AddScheme<DevAuthSchemeOptions, DevAuthHandler>(DevAuthSchemeOptions.SchemeName, _ => { });
        services.AddAuthorization();

        services.AddHostedService<DevAuthStartupGuard>();

        return services;
    }

    /// <summary>Marker singleton used to make <see cref="AddUKBatchDevAuth"/> idempotent.</summary>
    private sealed class DevAuthRegistrationMarker;
}
