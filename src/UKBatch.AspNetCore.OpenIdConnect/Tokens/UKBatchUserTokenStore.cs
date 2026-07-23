using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UKBatch.AspNetCore.OpenIdConnect.Tokens;

/// <summary>
/// The captured access/refresh token pair for one signed-in user, plus its expiry.
/// </summary>
internal sealed record TokenSet(string AccessToken, DateTimeOffset ExpiresAtUtc, string? RefreshToken);

/// <summary>
/// Process-wide, per-user server-side token store for the dashboard's on-behalf-of API calls.
/// </summary>
/// <remarks>
/// <para>
/// A Blazor Server page renders twice: first in the request scope (static render, <c>HttpContext</c>
/// live and holding the auth cookie), then in a long-lived interactive circuit that runs in a different
/// scope with no <c>HttpContext</c>. A scoped store seeded during the first render is a different
/// instance in the circuit, so the token would read back null there. This store is therefore a
/// singleton keyed by a stable per-user key, seeded while the request context is live and read back
/// in-circuit by resolving the same key from the circuit's authentication state. No token is ever
/// passed across the render-mode boundary as a component parameter.
/// </para>
/// <para>
/// When a token is near expiry and a refresh token is present, the store refreshes against the identity
/// provider's token endpoint under a per-user single-flight lock. Refresh is best-effort: a failure (or
/// a store already torn down at shutdown) returns the last known token so the API call can proceed and
/// surface its own 401 if the token has truly expired.
/// </para>
/// </remarks>
internal sealed class UKBatchUserTokenStore : IDisposable
{
    /// <summary>Name of the <see cref="IHttpClientFactory"/> client used for token refresh.</summary>
    internal const string RefreshHttpClientName = "UKBatch.OpenIdConnect.TokenRefresh";

    // Refresh once the access token is within this window of expiry.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromSeconds(60);

    private readonly ConcurrentDictionary<string, TokenSet> _tokens = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshGates = new(StringComparer.Ordinal);
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _oidcOptions;
    private readonly ILogger<UKBatchUserTokenStore> _logger;
    private int _disposed;

    public UKBatchUserTokenStore(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<OpenIdConnectOptions> oidcOptions,
        ILogger<UKBatchUserTokenStore> logger)
    {
        _httpClientFactory = httpClientFactory;
        _oidcOptions = oidcOptions;
        _logger = logger;
    }

    /// <summary>
    /// Builds the stable per-user key (<c>sub</c>, optionally suffixed with the session id <c>sid</c>)
    /// used to store and retrieve a user's tokens. Returns <c>null</c> when the principal is
    /// unauthenticated or carries no subject. Both the seeding path (request scope) and the read path
    /// (circuit) call this so the keys match.
    /// </summary>
    internal static string? BuildKey(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        // Length-prefixing the subject makes the key injective: no subject or session content can make
        // two different (subject, session) pairs — or a subject-only key — render the same string, so a
        // crafted claim value can never collide into another user's entry.
        var sessionId = principal.FindFirst("sid")?.Value;
        return string.IsNullOrEmpty(sessionId)
            ? string.Create(CultureInfo.InvariantCulture, $"{subject.Length}|{subject}")
            : string.Create(CultureInfo.InvariantCulture, $"{subject.Length}|{subject}|{sessionId}");
    }

    /// <summary>
    /// Reads the tokens the OpenID Connect handler saved (<c>SaveTokens = true</c>) from a live request
    /// context. Returns <c>null</c> when no access token is present.
    /// </summary>
    internal static async Task<TokenSet?> ReadFromHttpContextAsync(HttpContext httpContext)
    {
        string? accessToken;
        try
        {
            accessToken = await httpContext.GetTokenAsync("access_token").ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // No authentication handler that can surface tokens on this request.
            return null;
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        var refreshToken = await httpContext.GetTokenAsync("refresh_token").ConfigureAwait(false);
        var expiresRaw = await httpContext.GetTokenAsync("expires_at").ConfigureAwait(false);
        var expiresAt = ParseExpiresAt(expiresRaw);
        return new TokenSet(accessToken, expiresAt, refreshToken);
    }

    /// <summary>
    /// Stores a user's tokens if the key is not already present (first write wins). The cookie-captured
    /// token seeds the store once; later refreshes replace it, and re-seeding from the same cookie must
    /// not regress a refreshed token.
    /// </summary>
    public void Seed(string key, TokenSet tokens)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(tokens);
        _tokens.TryAdd(key, tokens);
    }

    /// <summary>
    /// Removes a user's tokens, called at sign-out. Without eviction the signed-out session's
    /// access/refresh tokens would sit in process memory for the process lifetime, and an already-open
    /// circuit — which resolves its token from this store on every call — would keep calling the API
    /// with the cached token after the user signed out.
    /// </summary>
    public void Remove(string key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        _tokens.TryRemove(key, out _);
        if (_refreshGates.TryRemove(key, out var gate))
        {
            // An in-flight refresh racing this dispose lands in the ObjectDisposedException arms below,
            // which fall back to the last known token — no unhandled throw.
            gate.Dispose();
        }
    }

    /// <summary>
    /// Returns the current access token for <paramref name="key"/>, refreshing it first when it is near
    /// expiry and a refresh token is available. Returns <c>null</c> when the key is unknown.
    /// </summary>
    public async Task<string?> GetAccessTokenAsync(string key, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(key) || !_tokens.TryGetValue(key, out var current))
        {
            return null;
        }

        var stillFresh = current.ExpiresAtUtc - DateTimeOffset.UtcNow > RefreshSkew;
        if (stillFresh || string.IsNullOrEmpty(current.RefreshToken) || Volatile.Read(ref _disposed) != 0)
        {
            return current.AccessToken;
        }

        return await RefreshAsync(key, current, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> RefreshAsync(string key, TokenSet current, CancellationToken cancellationToken)
    {
        var gate = _refreshGates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        try
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            // Store torn down while we waited — hand back what we have.
            return current.AccessToken;
        }

        try
        {
            // Another caller may have refreshed while we waited on the gate.
            if (_tokens.TryGetValue(key, out var latest) &&
                latest.ExpiresAtUtc - DateTimeOffset.UtcNow > RefreshSkew)
            {
                return latest.AccessToken;
            }

            if (Volatile.Read(ref _disposed) != 0)
            {
                return current.AccessToken;
            }

            var refreshed = await RequestRefreshAsync(current.RefreshToken!, cancellationToken).ConfigureAwait(false);
            if (refreshed is null)
            {
                return current.AccessToken;
            }

            // Write back conditionally: sign-out may have evicted this user while the refresh was in
            // flight, and TryUpdate cannot insert, so eviction wins. An unconditional indexer write here
            // would silently resurrect the signed-out session's tokens and keep a still-open circuit
            // calling the API after sign-out.
            if (_tokens.TryGetValue(key, out var existing) && _tokens.TryUpdate(key, refreshed, existing))
            {
                return refreshed.AccessToken;
            }

            return null;
        }
        catch (ObjectDisposedException)
        {
            return current.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Token refresh failed; using the existing access token.");
            return current.AccessToken;
        }
        finally
        {
            try
            {
                gate.Release();
            }
            catch (ObjectDisposedException)
            {
                // Store disposed under us; nothing to release.
            }
        }
    }

    private async Task<TokenSet?> RequestRefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var oidc = _oidcOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
        if (oidc.ConfigurationManager is null)
        {
            _logger.LogWarning("Cannot refresh the access token: the OpenID Connect authority is not configured.");
            return null;
        }

        var configuration = await oidc.ConfigurationManager.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var tokenEndpoint = configuration.TokenEndpoint;
        if (string.IsNullOrEmpty(tokenEndpoint))
        {
            _logger.LogWarning("Cannot refresh the access token: the discovery document has no token endpoint.");
            return null;
        }

        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("refresh_token", refreshToken),
        };
        if (!string.IsNullOrEmpty(oidc.ClientId))
        {
            form.Add(new KeyValuePair<string, string>("client_id", oidc.ClientId));
        }
        if (!string.IsNullOrEmpty(oidc.ClientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", oidc.ClientSecret));
        }

        var httpClient = _httpClientFactory.CreateClient(RefreshHttpClientName);
        using var content = new FormUrlEncodedContent(form);
        using var response = await httpClient.PostAsync(tokenEndpoint, content, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Token refresh returned HTTP {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("access_token", out var accessTokenElement) ||
            accessTokenElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var accessToken = accessTokenElement.GetString();
        if (string.IsNullOrEmpty(accessToken))
        {
            return null;
        }

        var expiresAt = DateTimeOffset.UtcNow;
        if (root.TryGetProperty("expires_in", out var expiresIn) &&
            expiresIn.ValueKind == JsonValueKind.Number &&
            expiresIn.TryGetInt64(out var seconds))
        {
            expiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds);
        }

        var rotatedRefresh = refreshToken;
        if (root.TryGetProperty("refresh_token", out var refreshElement) &&
            refreshElement.ValueKind == JsonValueKind.String)
        {
            rotatedRefresh = refreshElement.GetString() ?? refreshToken;
        }

        return new TokenSet(accessToken, expiresAt, rotatedRefresh);
    }

    private static DateTimeOffset ParseExpiresAt(string? raw)
    {
        // The handler stores expires_at as a round-trippable timestamp. If it is missing or unparseable,
        // treat the token as already expired so the next read attempts a refresh rather than trusting a
        // token of unknown age.
        if (!string.IsNullOrEmpty(raw) &&
            DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed;
        }

        return DateTimeOffset.UtcNow;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var gate in _refreshGates.Values)
        {
            gate.Dispose();
        }

        _refreshGates.Clear();
        _tokens.Clear();
    }
}
