using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace UKBatch.Transport.Http.Auth;

/// <summary>
/// Strict canonical-form builder for the HMAC signing envelope.
/// Sender and receiver compute IDENTICAL canonical strings from the same logical request — query
/// reordering, percent-encoding variance, trailing-slash drift, and plus-vs-percent20 ambiguity all
/// collapse to one form.
/// </summary>
/// <remarks>
/// <para><b>Canonical string layout (5 fields, newline-delimited, UTF-8):</b></para>
/// <code>
/// {HTTP-METHOD}\n
/// {canonical-path}\n
/// {timestamp-ms}\n
/// {nonce}\n
/// {base64(sha256(body))}
/// </code>
/// <para>Canonical path normalization rules: (1) trailing slash stripped unless path is exactly
/// <c>/</c>; (2) query parameters sorted by key (ordinal); (3) values sorted within key; (4) keys
/// and values percent-encoded via <c>Uri.EscapeDataString</c> (RFC 3986: space = <c>%20</c>,
/// NOT <c>+</c>); (5) NO trailing <c>?</c> when query is empty.</para>
/// </remarks>
internal static class HmacCanonicalForm
{
    /// <summary>
    /// Builds the canonical string-to-sign (strict normalization). Sender and receiver
    /// call this with the SAME inputs; the result is the message argument to HMAC SHA256.
    /// </summary>
    /// <param name="httpMethod">HTTP method (typically <c>"GET"</c> or <c>"POST"</c> — upper case, ASCII).</param>
    /// <param name="canonicalPath">Canonical path produced by
    /// <see cref="BuildCanonicalPathFromRequest"/> (receiver) or
    /// <see cref="BuildCanonicalPathForSender"/> (sender).</param>
    /// <param name="timestampMillis">Unix epoch milliseconds at request creation.</param>
    /// <param name="nonce">URL-safe base64-encoded 16 random bytes.</param>
    /// <param name="bodyBytes">Raw request body bytes (empty span for GET requests).</param>
    public static string Build(
        string httpMethod,
        string canonicalPath,
        long timestampMillis,
        string nonce,
        ReadOnlySpan<byte> bodyBytes)
    {
        ArgumentException.ThrowIfNullOrEmpty(httpMethod);
        ArgumentException.ThrowIfNullOrEmpty(canonicalPath);
        ArgumentException.ThrowIfNullOrEmpty(nonce);

        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bodyBytes, hash);
        var bodyHashB64 = Convert.ToBase64String(hash);

        // Compose with StringBuilder — predictable allocation, easy to audit.
        // For v0.1 alpha the perf delta vs `string.Create` is < 1µs per call.
        var sb = new StringBuilder(
            httpMethod.Length + canonicalPath.Length + 22 + nonce.Length + bodyHashB64.Length + 4);
        sb.Append(httpMethod);
        sb.Append('\n');
        sb.Append(canonicalPath);
        sb.Append('\n');
        sb.Append(timestampMillis);
        sb.Append('\n');
        sb.Append(nonce);
        sb.Append('\n');
        sb.Append(bodyHashB64);
        return sb.ToString();
    }

    /// <summary>
    /// Builds the canonical path from a receiver-side <see cref="HttpRequest"/>. ASP.NET Core
    /// routing has already URL-DECODED <see cref="HttpRequest.Path"/> by the time the filter runs.
    /// </summary>
    public static string BuildCanonicalPathFromRequest(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = request.Path.Value ?? "/";
        return CanonicalizePath(path, ToEntries(request.Query));
    }

    /// <summary>
    /// Sender-side canonical path computation. Called by <see cref="HttpTransport"/> before signing.
    /// Sender MUST percent-encode any non-ASCII or reserved characters in
    /// <paramref name="absolutePath"/> BEFORE calling.
    /// </summary>
    /// <param name="absolutePath">Absolute path portion of the URL
    /// (e.g. <c>/ukbatch/internal/jobs/publish</c>). Must be non-empty.</param>
    /// <param name="queryParams">Optional query parameter dictionary. Multi-value supported. Null
    /// or empty = no query string.</param>
    public static string BuildCanonicalPathForSender(
        string absolutePath,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>>? queryParams)
    {
        ArgumentException.ThrowIfNullOrEmpty(absolutePath);
        var entries = queryParams is null
            ? Array.Empty<KeyValuePair<string, IReadOnlyList<string>>>()
            : queryParams.ToArray();
        return CanonicalizePath(absolutePath, entries);
    }

    private static string CanonicalizePath(
        string path,
        IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> queryEntries)
    {
        // Rule 1: trailing slash strip (unless path == "/").
        if (path.Length > 1 && path.EndsWith('/'))
        {
            path = path.TrimEnd('/');
        }
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var entries = queryEntries.ToList();
        if (entries.Count == 0)
        {
            return path;   // Rule 3: NO trailing "?" when query is empty.
        }

        // Rules 2 + 4 + 5: sort keys ordinally; for each key, sort values ordinally;
        // percent-encode via Uri.EscapeDataString.
        var sb = new StringBuilder(path.Length + 64);
        sb.Append(path);
        sb.Append('?');
        var first = true;
        foreach (var entry in entries.OrderBy(static kv => kv.Key, StringComparer.Ordinal))
        {
            // Values sorted within key. Null values are treated as empty strings.
            var values = entry.Value ?? Array.Empty<string>();
            foreach (var v in values.OrderBy(static s => s, StringComparer.Ordinal))
            {
                if (!first) sb.Append('&');
                sb.Append(Uri.EscapeDataString(entry.Key));
                sb.Append('=');
                sb.Append(Uri.EscapeDataString(v ?? string.Empty));
                first = false;
            }
        }
        return sb.ToString();
    }

    private static IEnumerable<KeyValuePair<string, IReadOnlyList<string>>> ToEntries(IQueryCollection query)
    {
        foreach (var kv in query)
        {
            yield return new KeyValuePair<string, IReadOnlyList<string>>(kv.Key, kv.Value.ToArray()!);
        }
    }
}
