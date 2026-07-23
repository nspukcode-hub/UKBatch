using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace UKBatch.AspNetCore.OpenIdConnect.Tokens;

/// <summary>
/// Seeds the singleton <see cref="UKBatchUserTokenStore"/> with the signed-in user's tokens when a
/// Blazor circuit opens, so the interactive circuit (which has no <c>HttpContext</c>) can still forward
/// the user's bearer token to the API.
/// </summary>
/// <remarks>
/// The circuit's DI scope is built during the connection request, whose context carries the auth cookie,
/// so <see cref="IHttpContextAccessor.HttpContext"/> is live and authenticated at construction. BOTH the
/// user key and the token read are taken in the constructor, while the context is provably still bound
/// to this circuit's own connect request: on a non-WebSocket transport (long polling / SSE) the pooled
/// context can be recycled to a DIFFERENT user's request once the connect request completes, so reading
/// the retained reference any later — e.g. on circuit open — could pair one user's key with another
/// user's tokens. Seeding is best-effort and never throws out of the circuit lifecycle.
/// </remarks>
internal sealed class UKBatchTokenSeedingCircuitHandler : CircuitHandler
{
    private readonly UKBatchUserTokenStore _store;
    private readonly string? _key;
    private readonly Task<TokenSet?>? _tokensTask;

    public UKBatchTokenSeedingCircuitHandler(IHttpContextAccessor httpContextAccessor, UKBatchUserTokenStore store)
    {
        _store = store;
        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null)
        {
            return;
        }

        _key = UKBatchUserTokenStore.BuildKey(httpContext.User);
        if (_key is not null)
        {
            _tokensTask = ReadTokensAsync(httpContext);
        }
    }

    /// <inheritdoc/>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (_key is not null && _tokensTask is not null)
        {
            var tokens = await _tokensTask.ConfigureAwait(false);
            if (tokens is not null)
            {
                _store.Seed(_key, tokens);
            }
        }

        await base.OnCircuitOpenedAsync(circuit, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<TokenSet?> ReadTokensAsync(HttpContext httpContext)
    {
        try
        {
            return await UKBatchUserTokenStore.ReadFromHttpContextAsync(httpContext).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Connection context torn down mid-read; the request-scoped read path re-seeds on the next
            // outbound call while a context is still live.
            return null;
        }
    }
}
