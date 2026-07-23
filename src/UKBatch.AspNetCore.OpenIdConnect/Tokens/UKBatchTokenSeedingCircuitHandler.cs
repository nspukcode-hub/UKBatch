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
/// so <see cref="IHttpContextAccessor.HttpContext"/> is live and authenticated at construction. The
/// reference is snapshotted there and read on circuit open; reading it later would race a recycled
/// context. Seeding is best-effort and never throws out of the circuit lifecycle.
/// </remarks>
internal sealed class UKBatchTokenSeedingCircuitHandler : CircuitHandler
{
    private readonly UKBatchUserTokenStore _store;
    private readonly HttpContext? _httpContext;

    public UKBatchTokenSeedingCircuitHandler(IHttpContextAccessor httpContextAccessor, UKBatchUserTokenStore store)
    {
        _store = store;
        _httpContext = httpContextAccessor.HttpContext;
    }

    /// <inheritdoc/>
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        await SeedAsync().ConfigureAwait(false);
        await base.OnCircuitOpenedAsync(circuit, cancellationToken).ConfigureAwait(false);
    }

    private async Task SeedAsync()
    {
        var httpContext = _httpContext;
        if (httpContext is null)
        {
            return;
        }

        var key = UKBatchUserTokenStore.BuildKey(httpContext.User);
        if (key is null)
        {
            return;
        }

        try
        {
            var tokens = await UKBatchUserTokenStore.ReadFromHttpContextAsync(httpContext).ConfigureAwait(false);
            if (tokens is not null)
            {
                _store.Seed(key, tokens);
            }
        }
        catch (ObjectDisposedException)
        {
            // Connection context torn down mid-seed; the request-scoped read path re-seeds on the next
            // outbound call while a context is still live.
        }
    }
}
