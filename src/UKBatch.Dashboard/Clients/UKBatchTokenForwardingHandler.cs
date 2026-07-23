using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using UKBatch.AspNetCore;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Attaches the signed-in user's bearer token to outbound REST requests the dashboard makes to a
/// UKBatch API on the user's behalf. Bound to the accessor of the scope that built it (a per-circuit
/// provider under per-user authentication), so the token belongs to the current user. When the accessor
/// yields no token (no session), the request is sent unchanged.
/// </summary>
/// <remarks>
/// The bearer only travels over a channel that cannot leak it in cleartext: HTTPS, or plain HTTP to a
/// loopback address (the embedded topology where the dashboard calls its own host). Any other plain-HTTP
/// target gets the request WITHOUT the token — the API answers 401 rather than the user's token crossing
/// the network unencrypted — and a warning names the misconfigured base address once.
/// </remarks>
internal sealed class UKBatchTokenForwardingHandler : DelegatingHandler
{
    private readonly IUKBatchUserTokenAccessor _tokenAccessor;
    private readonly ILogger? _logger;
    private int _warnedInsecureTarget;

    public UKBatchTokenForwardingHandler(IUKBatchUserTokenAccessor tokenAccessor, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tokenAccessor);
        _tokenAccessor = tokenAccessor;
        _logger = logger;
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var uri = request.RequestUri;
        var channelProtectsToken = uri is not null
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) || uri.IsLoopback);
        if (!channelProtectsToken)
        {
            if (_logger is not null && Interlocked.Exchange(ref _warnedInsecureTarget, 1) == 0)
            {
                _logger.LogWarning(
                    "Not forwarding the signed-in user's bearer token to {Target}: the target is plain HTTP on a "
                    + "non-loopback host, which would expose the token in cleartext. Use an https:// BaseUrl for "
                    + "this service.",
                    uri is null ? "(no request URI)" : $"{uri.Scheme}://{uri.Authority}");
            }

            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var token = await _tokenAccessor.GetAccessTokenAsync(cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}
