using System.Globalization;
using System.Security.Cryptography;

namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// <see cref="DelegatingHandler"/> that signs outbound <see cref="HttpRequestMessage"/>s with the
/// HMAC envelope (X-UKBatch-Signature / X-UKBatch-Timestamp / X-UKBatch-Nonce) immediately before
/// dispatch. Registered AFTER the Polly resilience handler so each retry attempt re-runs through
/// this handler — timestamp + nonce ROTATE per attempt.
/// </summary>
/// <remarks>
/// <para><b>Why per-attempt signing:</b> Polly v8 retry replays the SAME
/// <see cref="HttpRequestMessage"/>. If HMAC headers are baked in at construction time, retry
/// resends stale nonce → receiver's <c>NonceDedupeCache</c> rejects as replay → 401. This handler
/// ensures every attempt carries a fresh nonce + current-clock timestamp.</para>
/// <para><b>Canonical path attachment:</b> <see cref="HttpTransport"/> attaches the pre-computed
/// canonical path via <see cref="HttpRequestOptionsKey{T}"/> on the request so this handler can
/// rebuild the canonical envelope without re-parsing the URL. Sender + receiver MUST agree on
/// canonical form regardless of retry attempt count.</para>
/// <para><b>Thread-safety:</b> singleton; stateless after construction (immutable
/// <c>_signer</c> + <c>_timeProvider</c>).</para>
/// </remarks>
internal sealed class HmacSigningHandler : DelegatingHandler
{
    /// <summary>
    /// Per-request canonical path slot. <see cref="HttpTransport"/> sets this on the outbound
    /// request; this handler reads it inside <see cref="SendAsync"/> to rebuild the canonical
    /// envelope per Polly attempt.
    /// </summary>
    internal static readonly HttpRequestOptionsKey<string> CanonicalPathKey = new("ukbatch.canonical-path");

    private readonly HmacSignatureService _signer;
    private readonly TimeProvider _timeProvider;

    public HmacSigningHandler(HmacSignatureService signer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(signer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _signer = signer;
        _timeProvider = timeProvider;
    }

    /// <summary>
    /// Attaches the canonical-path slot to the request so <see cref="HmacSigningHandler"/> can
    /// rebuild the envelope per Polly attempt. Call BEFORE handing the request to the named
    /// <c>HttpClient</c>.
    /// </summary>
    public static void AttachCanonicalPath(HttpRequestMessage request, string canonicalPath)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrEmpty(canonicalPath);
        request.Options.Set(CanonicalPathKey, canonicalPath);
    }

    /// <inheritdoc/>
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!request.Options.TryGetValue(CanonicalPathKey, out var canonicalPath) || string.IsNullOrEmpty(canonicalPath))
        {
            // Defensive: HttpTransport ALWAYS attaches the canonical path. If absent, the request
            // didn't originate from HttpTransport — bail rather than sign with the wrong canonical.
            throw new InvalidOperationException(
                "HmacSigningHandler invoked on a request without an attached canonical path. "
                + "This handler is intended exclusively for the named ukbatch-http-transport client.");
        }

        // Re-read the body each attempt — Polly retry already buffered via the resilience handler;
        // ReadAsByteArrayAsync is safe to call repeatedly on a ByteArrayContent (used by
        // HttpTransport.BuildSignedRequest).
        ReadOnlyMemory<byte> bodyBytes = ReadOnlyMemory<byte>.Empty;
        if (request.Content is not null)
        {
            var bytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            bodyBytes = bytes;
        }

        var timestamp = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var nonce = GenerateNonce();
        var canonical = HmacCanonicalForm.Build(
            httpMethod: request.Method.Method,
            canonicalPath: canonicalPath,
            timestampMillis: timestamp,
            nonce: nonce,
            bodyBytes: bodyBytes.Span);
        var signature = _signer.Sign(canonical);

        // Replace any prior headers (idempotent on first attempt; rotates on Polly retry).
        request.Headers.Remove("X-UKBatch-Signature");
        request.Headers.Remove("X-UKBatch-Timestamp");
        request.Headers.Remove("X-UKBatch-Nonce");
        request.Headers.TryAddWithoutValidation("X-UKBatch-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-UKBatch-Timestamp", timestamp.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-UKBatch-Nonce", nonce);

        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    internal static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
