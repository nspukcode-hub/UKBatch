using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using UKBatch.Transport.Http.Auth;

namespace UKBatch.Transport.Http.Tests.Common;

/// <summary>
/// Test helper for hand-crafting HMAC-signed <see cref="HttpRequestMessage"/> instances against the
/// receiver. Production code uses <see cref="HmacSigningHandler"/> via the
/// <see cref="System.Net.Http.IHttpClientFactory"/> pipeline; tests that want to control nonce /
/// timestamp / signature explicitly (e.g. tamper tests, clock-skew tests) use this builder.
/// </summary>
internal static class TestHmacHeaders
{
    public const string TestSecret = "TEST-SECRET-FOR-HMAC-SIGN-AND-VERIFY-32B+";

    public static void Attach(
        HttpRequestMessage request,
        string canonicalPath,
        string secret,
        long? timestampMillis = null,
        string? nonceOverride = null,
        ReadOnlyMemory<byte>? bodyBytes = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(canonicalPath);
        var ts = timestampMillis ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var nonce = nonceOverride ?? GenerateNonce();
        var body = bodyBytes ?? ReadOnlyMemory<byte>.Empty;
        var canonical = HmacCanonicalForm.Build(
            httpMethod: request.Method.Method,
            canonicalPath: canonicalPath,
            timestampMillis: ts,
            nonce: nonce,
            bodyBytes: body.Span);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var sigBytes = hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        var signature = Convert.ToBase64String(sigBytes);
        request.Headers.Remove("X-UKBatch-Signature");
        request.Headers.Remove("X-UKBatch-Timestamp");
        request.Headers.Remove("X-UKBatch-Nonce");
        request.Headers.TryAddWithoutValidation("X-UKBatch-Signature", signature);
        request.Headers.TryAddWithoutValidation("X-UKBatch-Timestamp", ts.ToString(CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation("X-UKBatch-Nonce", nonce);
    }

    public static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static StringContent JsonContent(string json)
    {
        var content = new StringContent(json, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return content;
    }

    public static ByteArrayContent JsonContent(byte[] bodyBytes)
    {
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8",
        };
        return content;
    }
}
