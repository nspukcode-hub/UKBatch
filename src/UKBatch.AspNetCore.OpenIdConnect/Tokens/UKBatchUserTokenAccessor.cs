using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.AspNetCore.OpenIdConnect.Tokens;

/// <summary>
/// Resolves the current user's access token from the singleton <see cref="UKBatchUserTokenStore"/>,
/// keyed by the user identifying claims. Works in both render modes of a Blazor Server dashboard: in a
/// request scope it reads (and seeds from) the live <c>HttpContext</c>; in an interactive circuit it
/// resolves the user from the circuit's authentication state.
/// </summary>
internal sealed class UKBatchUserTokenAccessor : IUKBatchUserTokenAccessor
{
    private readonly UKBatchUserTokenStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IServiceProvider _services;

    public UKBatchUserTokenAccessor(
        UKBatchUserTokenStore store,
        IHttpContextAccessor httpContextAccessor,
        IServiceProvider services)
    {
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _services = services;
    }

    /// <inheritdoc/>
    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        var httpContext = _httpContextAccessor.HttpContext;
        if (httpContext?.User?.Identity?.IsAuthenticated == true)
        {
            // Request scope (static render or any direct request): the tokens live in the auth cookie.
            // Seed the store opportunistically so the interactive circuit and any refresh can find them.
            var requestKey = UKBatchUserTokenStore.BuildKey(httpContext.User);
            if (requestKey is null)
            {
                return null;
            }

            var tokens = await UKBatchUserTokenStore.ReadFromHttpContextAsync(httpContext).ConfigureAwait(false);
            if (tokens is not null)
            {
                _store.Seed(requestKey, tokens);
            }

            return await _store.GetAccessTokenAsync(requestKey, cancellationToken).ConfigureAwait(false);
        }

        // Interactive circuit: no HttpContext. Identify the user from the circuit's authentication state.
        var authStateProvider = _services.GetService<AuthenticationStateProvider>();
        if (authStateProvider is null)
        {
            return null;
        }

        var authState = await authStateProvider.GetAuthenticationStateAsync().ConfigureAwait(false);
        var circuitKey = UKBatchUserTokenStore.BuildKey(authState.User);
        if (circuitKey is null)
        {
            return null;
        }

        return await _store.GetAccessTokenAsync(circuitKey, cancellationToken).ConfigureAwait(false);
    }
}
