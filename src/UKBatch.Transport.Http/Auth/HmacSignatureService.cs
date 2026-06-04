using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// HMAC SHA256 signer / verifier scoped to the configured
/// <see cref="HttpTransportOptions.SharedSecret"/>. Singleton — secret captured at construction;
/// reload requires host restart (v0.1 simplification).
/// </summary>
/// <remarks>
/// <para><b>Constant-time verify:</b> uses
/// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
/// to prevent timing-attack signature recovery. Naive <see cref="string.Equals(string?, string?)"/>
/// would leak signature byte-by-byte across many requests.</para>
/// <para><b>Thread-safety:</b> <see cref="HMACSHA256"/> instances are NOT thread-safe across
/// concurrent transforms, so each call creates a short-lived instance. Allocation cost is low
/// (~200 bytes); throughput well above 10K ops/s on a modern CPU.</para>
/// </remarks>
internal sealed class HmacSignatureService
{
    private readonly byte[] _secretBytes;

    public HmacSignatureService(IOptions<HttpTransportOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var secret = options.Value.SharedSecret;
        // Validator will fail at host start if secret is empty; defense-in-depth here.
        _secretBytes = string.IsNullOrEmpty(secret)
            ? Array.Empty<byte>()
            : Encoding.UTF8.GetBytes(secret);
    }

    /// <summary>Signs the canonical string and returns the base64-encoded signature.</summary>
    public string Sign(string canonical)
    {
        ArgumentException.ThrowIfNullOrEmpty(canonical);
        var signature = ComputeSignatureBytes(canonical);
        return Convert.ToBase64String(signature);
    }

    /// <summary>
    /// Verifies the supplied base64 signature against the canonical string. Constant-time compare.
    /// Returns <c>false</c> on malformed base64 (does NOT throw).
    /// </summary>
    public bool Verify(string canonical, string signatureBase64)
    {
        if (string.IsNullOrEmpty(canonical) || string.IsNullOrEmpty(signatureBase64))
        {
            return false;
        }
        byte[] actual;
        try
        {
            actual = Convert.FromBase64String(signatureBase64);
        }
        catch (FormatException)
        {
            return false;
        }
        var expected = ComputeSignatureBytes(canonical);
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private byte[] ComputeSignatureBytes(string canonical)
    {
        using var hmac = new HMACSHA256(_secretBytes);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
    }
}
