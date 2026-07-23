using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;

namespace UKBatch.AspNetCore.OpenIdConnect;

/// <summary>
/// Endpoint routing extensions for the OpenID Connect integration.
/// </summary>
public static class OpenIdConnectEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps a <c>POST</c> sign-out endpoint that clears the local cookie session and starts the identity
    /// provider's sign-out flow, then redirects to <paramref name="redirectUri"/>. The dashboard's log-out
    /// button posts to this endpoint.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder to add the endpoint to.</param>
    /// <param name="pattern">The route pattern for the sign-out endpoint. Defaults to <c>/signout</c>.</param>
    /// <param name="redirectUri">Where to land after sign-out completes. Defaults to the site root
    /// <c>/</c>; set it to the dashboard's path (for example <c>/dashboard</c>) when the dashboard is not
    /// mounted at the root, so the post-logout redirect resolves to a real page instead of a 404. The
    /// value must also be registered as a post-logout redirect URI with the identity provider.</param>
    /// <returns>The same <paramref name="endpoints"/> instance for chaining.</returns>
    public static IEndpointRouteBuilder MapUKBatchSignOut(
        this IEndpointRouteBuilder endpoints,
        string pattern = "/signout",
        string redirectUri = "/")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
        ArgumentException.ThrowIfNullOrWhiteSpace(redirectUri);

        endpoints.MapPost(pattern, (HttpContext httpContext) =>
        {
            // Evict the user's server-side tokens FIRST: a still-open circuit resolves its token from
            // the store on every call, so without eviction a signed-out session's dashboard would keep
            // calling the API with the cached token until it expired.
            var key = UKBatchUserTokenStore.BuildKey(httpContext.User);
            if (key is not null)
            {
                httpContext.RequestServices.GetService<UKBatchUserTokenStore>()?.Remove(key);
            }

            return Results.SignOut(
                new AuthenticationProperties { RedirectUri = redirectUri },
                [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
        });

        return endpoints;
    }
}
