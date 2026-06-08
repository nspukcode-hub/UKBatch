using Microsoft.Extensions.Options;

namespace UKBatch.Transport.Http;

/// <summary>
/// Host-start validator for <see cref="HttpTransportOptions"/>. Each
/// violation becomes one entry in the resulting <see cref="OptionsValidationException"/>.
/// </summary>
/// <remarks>
/// <para>Empty <see cref="HttpTransportOptions.Services"/> is explicitly valid — receiver-only
/// nodes (worker microservices) have no outbound targets. Sender-only nodes simply do not call
/// <see cref="EndpointRouteBuilderExtensions.MapUKBatchHttpTransport"/>.</para>
/// </remarks>
internal sealed class HttpTransportOptionsValidator : IValidateOptions<HttpTransportOptions>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, HttpTransportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (string.IsNullOrEmpty(options.SharedSecret))
        {
            failures.Add("HttpTransportOptions.SharedSecret is required (got empty).");
        }
        else if (options.SharedSecret.Length < 32)
        {
            failures.Add(
                $"HttpTransportOptions.SharedSecret must be at least 32 characters for HMAC-SHA256 " +
                $"(got {options.SharedSecret.Length}). Use a high-entropy secret from a secure source.");
        }

        if (options.DefaultRequestTimeout <= TimeSpan.Zero
            || options.DefaultRequestTimeout > TimeSpan.FromMinutes(10))
        {
            failures.Add($"DefaultRequestTimeout must be in (0, 10 min] (got {options.DefaultRequestTimeout}).");
        }

        if (options.LongPollMaxWait <= TimeSpan.Zero
            || options.LongPollMaxWait > TimeSpan.FromMinutes(5))
        {
            failures.Add($"LongPollMaxWait must be in (0, 5 min] (got {options.LongPollMaxWait}).");
        }

        // Inter-field constraint: HTTP timeout must exceed long-poll hold by at least 5s slack so
        // the server returns the empty array before the client times out.
        if (options.DefaultRequestTimeout > TimeSpan.Zero
            && options.LongPollMaxWait > TimeSpan.Zero
            && options.DefaultRequestTimeout < options.LongPollMaxWait + TimeSpan.FromSeconds(5))
        {
            failures.Add(
                $"DefaultRequestTimeout ({options.DefaultRequestTimeout}) must exceed LongPollMaxWait ({options.LongPollMaxWait}) + 5s slack.");
        }

        if (options.MaxClockSkew < TimeSpan.FromSeconds(1)
            || options.MaxClockSkew > TimeSpan.FromHours(1))
        {
            failures.Add($"MaxClockSkew must be in [1s, 1h] (got {options.MaxClockSkew}).");
        }

        if (options.NonceCacheCapacity < 16)
        {
            failures.Add($"NonceCacheCapacity must be >= 16 (got {options.NonceCacheCapacity}).");
        }

        if (options.MessageIdCacheCapacity < 64)
        {
            failures.Add($"MessageIdCacheCapacity must be >= 64 (got {options.MessageIdCacheCapacity}).");
        }

        if (options.CircuitBreakerThreshold < 1)
        {
            failures.Add($"CircuitBreakerThreshold must be >= 1 (got {options.CircuitBreakerThreshold}).");
        }

        if (options.CircuitBreakerWindow < TimeSpan.FromSeconds(1))
        {
            failures.Add($"CircuitBreakerWindow must be >= 1s (got {options.CircuitBreakerWindow}).");
        }

        // Upper bound: the HMAC filter buffers up to MaxBodyBytes in memory BEFORE signature
        // verification, so this value is an unauthenticated per-request memory ceiling. Cap it so a
        // misconfiguration cannot turn the receiver into a pre-auth memory-exhaustion target.
        const int MaxBodyBytesCeiling = 16 * 1024 * 1024; // 16 MB
        if (options.MaxBodyBytes < 1 || options.MaxBodyBytes > MaxBodyBytesCeiling)
        {
            failures.Add(
                $"MaxBodyBytes must be in [1, {MaxBodyBytesCeiling}] (got {options.MaxBodyBytes}). " +
                $"The HMAC filter buffers up to this size before authenticating the request.");
        }

        if (options.RetryDelays is not null)
        {
            if (options.RetryDelays.Count < 1 || options.RetryDelays.Count > 10)
            {
                failures.Add($"RetryDelays.Count must be in [1, 10] (got {options.RetryDelays.Count}).");
            }
            else
            {
                for (var i = 0; i < options.RetryDelays.Count; i++)
                {
                    var d = options.RetryDelays[i];
                    if (d <= TimeSpan.Zero || d > TimeSpan.FromMinutes(10))
                    {
                        failures.Add($"RetryDelays[{i}] must be in (0, 10 min] (got {d}).");
                    }
                }
            }
        }

        if (options.Services is not null)
        {
            foreach (var (key, endpoint) in options.Services)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    failures.Add("HttpTransportOptions.Services contains an empty or whitespace key.");
                    continue;
                }
                if (endpoint is null)
                {
                    failures.Add($"HttpTransportOptions.Services['{key}'] is null.");
                    continue;
                }
                if (!endpoint.BaseUrl.IsAbsoluteUri
                    || (endpoint.BaseUrl.Scheme != Uri.UriSchemeHttp && endpoint.BaseUrl.Scheme != Uri.UriSchemeHttps))
                {
                    failures.Add(
                        $"Services['{key}'].BaseUrl must be an absolute http/https URI (got {endpoint.BaseUrl}).");
                }
                else if (endpoint.BaseUrl.Scheme == Uri.UriSchemeHttp
                    && !options.AllowInsecureHttp
                    && !IsLoopback(endpoint.BaseUrl))
                {
                    failures.Add(
                        $"Services['{key}'].BaseUrl uses cleartext http on a non-loopback host ({endpoint.BaseUrl.Host}). " +
                        $"HMAC does not encrypt payloads — use https, target a loopback host, or set " +
                        $"HttpTransportOptions.AllowInsecureHttp=true to opt in explicitly.");
                }
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }

    private static bool IsLoopback(Uri uri)
    {
        if (uri.IsLoopback)   // covers localhost, 127.0.0.1, ::1 per Uri's own rule
        {
            return true;
        }
        return System.Net.IPAddress.TryParse(uri.Host, out var ip) && System.Net.IPAddress.IsLoopback(ip);
    }
}
