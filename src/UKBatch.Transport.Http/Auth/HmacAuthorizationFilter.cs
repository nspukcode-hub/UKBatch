using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Transport.Http.Common;

namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// Per-request HMAC SHA256 authorization filter for the
/// <c>/ukbatch/internal/jobs/*</c> receiver endpoints. Validates the three signed headers
/// (signature, timestamp, nonce), enforces clock-skew window, and gates replay via a bounded LRU
/// <see cref="NonceDedupeCache"/>.
/// </summary>
/// <remarks>
/// <para><b>OWASP fold:</b> missing header / bad signature / replay nonce all return
/// <c>401 ukbatch:transport-auth-failed</c> with the SAME body — no information leak about which
/// step failed. Clock skew is differentiated (<c>ukbatch:transport-clock-skew</c>) because NTP
/// drift is an operator-visible concern that warrants its own metric.</para>
/// <para><b>Body buffering:</b> ASP.NET Core's request stream is forward-only by default; this
/// filter calls <see cref="HttpRequestRewindExtensions.EnableBuffering(HttpRequest)"/> so the
/// downstream handler can re-read the body for JSON deserialization after the filter computes the
/// body hash.</para>
/// </remarks>
internal sealed class HmacAuthorizationFilter : IEndpointFilter
{
    private readonly HmacSignatureService _signer;
    private readonly NonceDedupeCache _nonceCache;
    private readonly IOptions<HttpTransportOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<HmacAuthorizationFilter> _logger;

    public HmacAuthorizationFilter(
        HmacSignatureService signer,
        NonceDedupeCache nonceCache,
        IOptions<HttpTransportOptions> options,
        TimeProvider timeProvider,
        ILogger<HmacAuthorizationFilter> logger)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(nonceCache);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        _signer = signer;
        _nonceCache = nonceCache;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        var http = context.HttpContext;
        var headers = http.Request.Headers;

        if (!headers.TryGetValue("X-UKBatch-Signature", out var sigBundle)
            || !headers.TryGetValue("X-UKBatch-Timestamp", out var tsBundle)
            || !headers.TryGetValue("X-UKBatch-Nonce", out var nonceBundle))
        {
            _logger.LogDebug("HMAC auth rejected: missing signed header.");
            return AuthFailed(http);
        }

        var signature = sigBundle.ToString();
        var nonce = nonceBundle.ToString();
        if (!long.TryParse(tsBundle.ToString(), out var timestampMillis))
        {
            _logger.LogDebug("HMAC auth rejected: timestamp not parseable.");
            return AuthFailed(http);
        }

        var nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var skewMs = Math.Abs(nowMs - timestampMillis);
        if (skewMs > _options.Value.MaxClockSkew.TotalMilliseconds)
        {
            _logger.LogInformation(
                "HMAC auth rejected: clock skew {SkewMs}ms exceeds {MaxSkewMs}ms window.",
                skewMs, _options.Value.MaxClockSkew.TotalMilliseconds);
            return ClockSkewFailed(http, skewMs);
        }

        // Bounded body buffering — pass bufferLimit so ASP.NET Core enforces the
        // cap during the buffer copy (an unbounded EnableBuffering() would admit the
        // full body into memory before checking length). An adversarial 29MB body against the
        // default 1MB cap would cost full memory pressure; ASP.NET Core throws during CopyToAsync instead.
        var maxBodyBytes = _options.Value.MaxBodyBytes;

        // Fast-path: if Content-Length header reports a size > cap, reject without buffering.
        if (http.Request.ContentLength.HasValue && http.Request.ContentLength.Value > maxBodyBytes)
        {
            _logger.LogWarning(
                "HMAC auth rejected: Content-Length {ContentLength}B exceeds configured cap {MaxBytes}B.",
                http.Request.ContentLength.Value, maxBodyBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Bounded buffer for the body — bufferThreshold = 32KB spillover trigger, bufferLimit caps
        // total ingest. ASP.NET Core throws IOException if the body exceeds bufferLimit during copy.
        http.Request.EnableBuffering(bufferThreshold: 32 * 1024, bufferLimit: maxBodyBytes);
        byte[] bodyBytes;
        try
        {
            using var ms = new MemoryStream(capacity: 32 * 1024);
            await http.Request.Body.CopyToAsync(ms, http.RequestAborted).ConfigureAwait(false);
            http.Request.Body.Position = 0;
            bodyBytes = ms.ToArray();
        }
        catch (BadHttpRequestException)
        {
            // Kestrel's body size limit fires here as BadHttpRequestException — let it propagate
            // (Kestrel already produces a 413 status code in this path).
            throw;
        }
        catch (IOException ex) when (ex.Message.Contains("buffer", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("limit", StringComparison.OrdinalIgnoreCase))
        {
            // EnableBuffering(bufferLimit) throws IOException when the bound is breached during
            // CopyToAsync. Defense in depth — Kestrel may also enforce its own limit earlier.
            _logger.LogWarning(
                "HMAC auth rejected: body exceeded {MaxBytes}B buffer cap during read.",
                maxBodyBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // Defense-in-depth: post-buffer length check (Content-Length may be absent on chunked).
        if (bodyBytes.Length > maxBodyBytes)
        {
            _logger.LogWarning(
                "HMAC auth rejected: body size {ActualBytes}B exceeds configured cap {MaxBytes}B.",
                bodyBytes.Length, maxBodyBytes);
            return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        // The filter MUST use the canonical-path helper, NOT raw concat. Without this line,
        // query reordering / trailing slash / plus-vs-percent20 silently 401 in production.
        var canonical = HmacCanonicalForm.Build(
            httpMethod: http.Request.Method,
            canonicalPath: HmacCanonicalForm.BuildCanonicalPathFromRequest(http.Request),
            timestampMillis: timestampMillis,
            nonce: nonce,
            bodyBytes: bodyBytes);

        if (!_signer.Verify(canonical, signature))
        {
            _logger.LogWarning("HMAC auth rejected: signature mismatch (path={Path}).", http.Request.Path);
            return AuthFailed(http);
        }

        // Replay check is LAST so the unique nonce only gets consumed on otherwise-valid requests
        // (replay attempts on the same canonical envelope still get rejected via nonce dedupe).
        if (!_nonceCache.TryAdd(nonce))
        {
            _logger.LogWarning("HMAC auth rejected: nonce replay (path={Path}).", http.Request.Path);
            return AuthFailed(http);
        }

        return await next(context).ConfigureAwait(false);
    }

    private static IResult AuthFailed(HttpContext http) => Results.Problem(
        type: TransportProblemDetailsConventions.TransportAuthFailed,
        title: "Transport authorization failed.",
        statusCode: StatusCodes.Status401Unauthorized,
        detail: "The signed envelope did not validate.",
        instance: http.Request.Path);

    private static IResult ClockSkewFailed(HttpContext http, long skewMs) => Results.Problem(
        type: TransportProblemDetailsConventions.TransportClockSkew,
        title: "Transport timestamp outside clock skew window.",
        statusCode: StatusCodes.Status401Unauthorized,
        detail: $"Observed skew {skewMs}ms exceeds the configured window.",
        instance: http.Request.Path);
}
