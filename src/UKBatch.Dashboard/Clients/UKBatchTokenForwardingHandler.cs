using System.Net.Http.Headers;
using UKBatch.AspNetCore;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Attaches the signed-in user's bearer token to outbound REST requests the dashboard makes to a
/// UKBatch API on the user's behalf. Bound to the accessor of the scope that built it (a per-circuit
/// provider under per-user authentication), so the token belongs to the current user. When the accessor
/// yields no token (no session), the request is sent unchanged.
/// </summary>
internal sealed class UKBatchTokenForwardingHandler : DelegatingHandler
{
    private readonly IUKBatchUserTokenAccessor _tokenAccessor;

    public UKBatchTokenForwardingHandler(IUKBatchUserTokenAccessor tokenAccessor)
    {
        ArgumentNullException.ThrowIfNull(tokenAccessor);
        _tokenAccessor = tokenAccessor;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var token = await _tokenAccessor.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
