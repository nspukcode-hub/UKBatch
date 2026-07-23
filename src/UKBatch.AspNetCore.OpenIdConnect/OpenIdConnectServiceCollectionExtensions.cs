using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;
using JwtBearerMessageReceivedContext = Microsoft.AspNetCore.Authentication.JwtBearer.MessageReceivedContext;

namespace UKBatch.AspNetCore.OpenIdConnect;

/// <summary>Opt-in registration for OpenID Connect login, viewer/operator role-gating, and per-user token forwarding.</summary>
public static class OpenIdConnectServiceCollectionExtensions
{
    private const string ViewerPolicyName = "UKBatch:Viewer";
    private const string OperatorPolicyName = "UKBatch:Operator";
    private const string DefaultHubPath = "/hubs/jobs";

    /// <summary>
    /// Registers OpenID Connect authentication for a UKBatch dashboard and/or API: a cookie session, an
    /// OpenID Connect challenge for interactive login, and JWT bearer validation for API calls. It also
    /// flattens nested provider roles into standard role claims, registers the
    /// <see cref="ViewerPolicyName"/> and <see cref="OperatorPolicyName"/> authorization policies, and
    /// registers the server-side per-user token store that lets the dashboard forward the signed-in
    /// user's token to the API.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The identity provider is reached purely by its authority URL, so any standard OpenID Connect
    /// provider (Keycloak, Azure AD, Auth0, IdentityServer) works without provider-specific code.
    /// </para>
    /// <para>
    /// Calling this method more than once is safe: the schemes, policies, and services are each
    /// registered at most once.
    /// </para>
    /// </remarks>
    /// <param name="services">The service collection to add the OpenID Connect stack to.</param>
    /// <param name="configure">Configures <see cref="UKBatchOpenIdConnectOptions"/> (authority, client, roles).</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    public static IServiceCollection AddUKBatchOpenIdConnect(
        this IServiceCollection services,
        Action<UKBatchOpenIdConnectOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        services.Configure(configure);
        services.AddOptions<UKBatchOpenIdConnectOptions>().ValidateOnStart();
        services.TryAddSingleton<IValidateOptions<UKBatchOpenIdConnectOptions>, UKBatchOpenIdConnectOptionsValidator>();
        services.AddHttpContextAccessor();

        // Idempotency: a second call must not re-register the authentication schemes (AddScheme rejects
        // a duplicate) or re-add the policies.
        if (services.Any(d => d.ServiceType == typeof(OpenIdConnectRegistrationMarker)))
        {
            return services;
        }
        services.AddSingleton<OpenIdConnectRegistrationMarker>();

        // Per-user token forwarding: a singleton store keyed by the user, a scoped accessor the dashboard
        // resolves, and a circuit handler that seeds the store while the connecting request context is live.
        services.AddHttpClient(UKBatchUserTokenStore.RefreshHttpClientName);
        services.TryAddSingleton<UKBatchUserTokenStore>();
        services.TryAddScoped<IUKBatchUserTokenAccessor, UKBatchUserTokenAccessor>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, UKBatchTokenSeedingCircuitHandler>());

        // Registered last so the flattening runs even if the host registered its own transformation
        // (the framework resolves a single IClaimsTransformation, and the last registration wins).
        services.AddScoped<IClaimsTransformation, KeycloakRoleFlatteningTransformation>();

        services
            .AddAuthentication(options =>
            {
                options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
            })
            .AddCookie()
            .AddOpenIdConnect()
            .AddJwtBearer();

        // Bind the handler options from UKBatchOpenIdConnectOptions via DI so any host-side configuration
        // binding is honoured, not just the callback snapshot.
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<UKBatchOpenIdConnectOptions>>(ConfigureOpenIdConnect);
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptionsMonitor<UKBatchOpenIdConnectOptions>>(ConfigureJwtBearer);

        services.AddAuthorization();
        services.AddOptions<AuthorizationOptions>()
            .Configure<IOptionsMonitor<UKBatchOpenIdConnectOptions>>(ConfigureAuthorizationPolicies);

        return services;
    }

    private static void ConfigureOpenIdConnect(
        OpenIdConnectOptions oidc,
        IOptionsMonitor<UKBatchOpenIdConnectOptions> monitor)
    {
        var options = monitor.CurrentValue;

        oidc.Authority = options.Authority;
        oidc.ClientId = options.ClientId;
        oidc.ClientSecret = options.ClientSecret;
        oidc.ResponseType = "code";
        oidc.SaveTokens = true;
        oidc.GetClaimsFromUserInfoEndpoint = true;
        // Keep provider claim types verbatim; the 7.x (net8) and 8.x (net10) IdentityModel lines map
        // inbound claims differently, and stable claim names (sub, sid, preferred_username) keep the
        // user key and role flattening consistent across target frameworks.
        oidc.MapInboundClaims = false;
        oidc.RequireHttpsMetadata = options.RequireHttpsMetadata;
        oidc.CallbackPath = options.CallbackPath;
        oidc.SignedOutCallbackPath = options.SignedOutCallbackPath;
        oidc.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;

        // Over plain HTTP — a local development provider (RequireHttpsMetadata=false, a conscious
        // opt-out whose default is true) — the default form_post callback is a cross-site POST whose
        // correlation/nonce cookies a browser will not send. Switch to a top-level GET callback (query
        // response mode) with Lax cookies: Lax is the STRICTER SameSite for a GET callback (None would
        // also flow on cross-site POSTs), and the flow stays authorization code + PKCE, so the query
        // redirect does not expose a usable code. SecurePolicy is SameAsRequest rather than None: the
        // handler's default Always-Secure cookie is dropped by a browser on a plain-HTTP non-localhost
        // hostname (which is what breaks correlation), but any HTTPS request still gets a Secure cookie
        // even in this branch — the relaxation never strips Secure from an HTTPS deployment that merely
        // left the flag false. Behind the production default (RequireHttpsMetadata=true) none of this
        // applies: form_post + SameSite=None + Secure stay as shipped by the framework.
        if (!options.RequireHttpsMetadata)
        {
            oidc.ResponseMode = OpenIdConnectResponseMode.Query;
            oidc.CorrelationCookie.SameSite = SameSiteMode.Lax;
            oidc.CorrelationCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            oidc.NonceCookie.SameSite = SameSiteMode.Lax;
            oidc.NonceCookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        }

        // Scopes come from configuration (default: openid, profile). offline_access is not forced: many
        // providers and clients refuse offline tokens (login would fail at the token exchange), and a
        // dashboard's active session only needs the normal session-bound refresh token that the code flow
        // already returns. A host that wants long-lived offline refresh adds "offline_access" to Scope.
        oidc.Scope.Clear();
        foreach (var scope in options.Scope)
        {
            oidc.Scope.Add(scope);
        }

        oidc.TokenValidationParameters.NameClaimType = "preferred_username";
        oidc.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        oidc.Events = new OpenIdConnectEvents
        {
            OnTokenValidated = context =>
            {
                // The cookie principal is built from the id token, but Keycloak — and some other
                // providers — put roles only in the access token. Copy the role-source claims (the roots
                // of RoleClaimPaths, e.g. realm_access / resource_access) from the access token onto the
                // principal at sign-in, so they persist in the cookie and the role flattening works on the
                // cookie path exactly as it already does on the bearer path. The access token arrived over
                // the backchannel token endpoint (TLS + PKCE) and the cookie's own Data Protection signing
                // keeps the copied claim tamper-proof, so reading it here without re-validating its
                // signature is safe. A non-JWT (opaque) access token is skipped.
                var accessToken = context.TokenEndpointResponse?.AccessToken;
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.Principal?.Identity is ClaimsIdentity identity)
                {
                    var handler = new JsonWebTokenHandler();
                    if (handler.CanReadToken(accessToken))
                    {
                        var accessJwt = handler.ReadJsonWebToken(accessToken);
                        foreach (var path in monitor.CurrentValue.RoleClaimPaths)
                        {
                            var claimName = path.Split('.', 2)[0];
                            if (string.IsNullOrEmpty(claimName) || identity.HasClaim(c => c.Type == claimName))
                            {
                                continue;
                            }

                            if (accessJwt.TryGetPayloadValue<JsonElement>(claimName, out var element))
                            {
                                identity.AddClaim(new Claim(claimName, element.GetRawText()));
                            }
                        }
                    }
                }

                return Task.CompletedTask;
            },
        };

#if NET10_0_OR_GREATER
        // Keycloak advertises Pushed Authorization Requests, which the net9+ handler would use by default
        // while the net8 handler cannot. Pin it off for an identical login flow across target frameworks.
        oidc.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
#endif
    }

    private static void ConfigureJwtBearer(
        JwtBearerOptions jwt,
        IOptionsMonitor<UKBatchOpenIdConnectOptions> monitor)
    {
        var options = monitor.CurrentValue;

        jwt.Authority = options.Authority;
        jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
        jwt.MapInboundClaims = false;
        jwt.TokenValidationParameters.NameClaimType = "preferred_username";
        jwt.TokenValidationParameters.RoleClaimType = ClaimTypes.Role;

        if (options.Audience is not null)
        {
            jwt.Audience = options.Audience;
            jwt.TokenValidationParameters.ValidateAudience = true;
            jwt.TokenValidationParameters.ValidAudience = options.Audience;
        }
        else
        {
            // Keycloak's default access-token audience is "account"; with no configured audience, accept
            // the token on issuer/signature alone rather than reject every call.
            jwt.TokenValidationParameters.ValidateAudience = false;
        }

        jwt.Events = new JwtBearerEvents
        {
            OnMessageReceived = OnHubMessageReceived,
        };
    }

    private static Task OnHubMessageReceived(JwtBearerMessageReceivedContext context)
    {
        // Browsers and the SignalR JS/.NET clients send the token as an access_token query parameter on
        // the WebSocket handshake (headers cannot be set there). Accept it ONLY for the hub path. The hub
        // is often mounted under a prefix (for example the API group at /api makes the hub /api/hubs/jobs),
        // so the configured hub path may appear at any mount depth — but it must match on whole path
        // segments, otherwise the WebSocket handshake is rejected (401) and live updates never connect,
        // or worse, a lookalike route ("/api/hubs/jobs-exfil") would start accepting query-string tokens,
        // widening where a bearer can ride in a loggable URL.
        string? accessToken = context.Request.Query["access_token"];
        if (!string.IsNullOrEmpty(accessToken))
        {
            var hubPath = context.HttpContext.RequestServices
                .GetService<IOptions<UKBatchOptions>>()?.Value.HubPath ?? DefaultHubPath;
            if (IsHubTokenRequest(context.HttpContext.Request.Path.Value, hubPath))
            {
                context.Token = accessToken;
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// True when <paramref name="requestPath"/> targets the SignalR hub at <paramref name="hubPath"/> —
    /// the hub path must appear as whole path segments (at any mount depth, e.g. <c>/api/hubs/jobs</c>
    /// or its <c>/negotiate</c> sub-path), never as a mere substring of another segment
    /// (<c>/api/hubs/jobs-exfil</c> does not match). Query-string bearer tokens are accepted only on
    /// these requests.
    /// </summary>
    internal static bool IsHubTokenRequest(string? requestPath, string hubPath)
    {
        if (string.IsNullOrEmpty(requestPath) || string.IsNullOrEmpty(hubPath))
        {
            return false;
        }

        // A leading slash anchors the match at a segment start ("/apihubs/jobs" cannot match "/hubs/jobs").
        if (hubPath[0] != '/')
        {
            hubPath = "/" + hubPath;
        }

        var searchFrom = 0;
        while (true)
        {
            var idx = requestPath.IndexOf(hubPath, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
            {
                return false;
            }

            // Require a segment boundary AFTER the match too: end of path, or a '/' (the negotiate and
            // transport sub-paths). "-exfil" style suffixes fail this check.
            var end = idx + hubPath.Length;
            if (end == requestPath.Length || requestPath[end] == '/')
            {
                return true;
            }

            searchFrom = idx + 1;
        }
    }

    private static void ConfigureAuthorizationPolicies(
        AuthorizationOptions authorization,
        IOptionsMonitor<UKBatchOpenIdConnectOptions> monitor)
    {
        // Both policies accept a cookie (dashboard Razor endpoints) OR a forwarded bearer (API), so one
        // policy pair covers the embedded and central-dashboard topologies.
        authorization.AddPolicy(ViewerPolicyName, policy => policy
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                JwtBearerDefaults.AuthenticationScheme));

        authorization.AddPolicy(OperatorPolicyName, policy => policy
            .RequireAuthenticatedUser()
            .AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                JwtBearerDefaults.AuthenticationScheme)
            // Evaluate the operator roles at request time so live configuration is honoured. A snapshot
            // captured at build time would be empty before configuration binds, and RequireRole on an
            // empty set throws at policy build.
            .RequireAssertion(context =>
            {
                var operatorRoles = monitor.CurrentValue.OperatorRoles;
                foreach (var role in operatorRoles)
                {
                    if (context.User.IsInRole(role))
                    {
                        return true;
                    }
                }

                return false;
            }));
    }

    /// <summary>Marker singleton used to make <see cref="AddUKBatchOpenIdConnect"/> idempotent.</summary>
    private sealed class OpenIdConnectRegistrationMarker;
}
