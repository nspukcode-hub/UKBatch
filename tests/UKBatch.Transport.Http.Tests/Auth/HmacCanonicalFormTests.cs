using FluentAssertions;
using Microsoft.AspNetCore.Http;
using UKBatch.Transport.Http.Auth;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Auth;

/// <summary>
/// strict canonical form byte-for-byte regression locks.
/// All tests are deterministic, pure CPU — no fixtures.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HmacCanonicalFormTests
{
    private static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Q(params (string, string)[] pairs)
        => pairs
            .GroupBy(p => p.Item1, StringComparer.Ordinal)
            .Select(g => new KeyValuePair<string, IReadOnlyList<string>>(g.Key, g.Select(x => x.Item2).ToList()))
            .ToList();

    // === enumerated tests (6) ===

    [Fact]
    public void CanonicalPath_QueryReordering_ProducesIdenticalCanonical()
    {
        // ?topic=X&waitMs=30000 and ?waitMs=30000&topic=X must yield IDENTICAL canonical strings.
        var a = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("topic", "X"), ("waitMs", "30000")));
        var b = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("waitMs", "30000"), ("topic", "X")));
        a.Should().Be(b);
        a.Should().Be("/jobs/poll?topic=X&waitMs=30000");
    }

    [Fact]
    public void CanonicalPath_PlusVsPercent20_NormalizesToPercent20()
    {
        // Uri.EscapeDataString emits %20 for space (NOT).
        var path = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("topic", "hello world")));
        path.Should().Be("/jobs/poll?topic=hello%20world");
        path.Should().NotContain("+");
    }

    [Fact]
    public void CanonicalPath_TrailingSlash_Stripped()
    {
        var withSlash = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/publish/", queryParams: null);
        var without = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/publish", queryParams: null);
        withSlash.Should().Be(without);
    }

    [Fact]
    public void CanonicalPath_RootSlash_Preserved()
    {
        var root = HmacCanonicalForm.BuildCanonicalPathForSender("/", queryParams: null);
        root.Should().Be("/");
    }

    [Fact]
    public void CanonicalPath_EmptyQuery_OmitsQuestionMark()
    {
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/publish", queryParams: null);
        p.Should().Be("/jobs/publish");
        p.Should().NotContain("?");
    }

    [Fact]
    public void CanonicalPath_MultiValueParam_SortedLexically()
    {
        var a = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("tag", "z"), ("tag", "a")));
        var b = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("tag", "a"), ("tag", "z")));
        a.Should().Be(b);
        a.Should().Be("/jobs/poll?tag=a&tag=z");
    }

    [Fact]
    public void CanonicalPath_PercentEncodedKey_DoublyEncoded()
    {
        // Sender sends a key that already contains percent-encoding, e.g. raw "to%70ic". The canonical
        // form percent-encodes the literal characters including the % sign → "to%2570ic".
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("to%70ic", "foo")));
        p.Should().Contain("to%2570ic");
    }

    // === NEW edge cases (4) ===

    [Fact]
    public void CanonicalPath_UnicodeUTF8_ProducesIdenticalCanonical()
    {
        // UTF-8 encoded "İstanbul" → "%C4%B0stanbul" via Uri.EscapeDataString.
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/", Q(("city", "İstanbul")));
        p.Should().Contain("%C4%B0stanbul");
    }

    [Fact]
    public void CanonicalPath_RepeatedParam_SameKeyDifferentValues()
    {
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/list", Q(("status", "Pending"), ("status", "Running")));
        p.Should().Be("/jobs/list?status=Pending&status=Running");
    }

    [Fact]
    public void CanonicalPath_NullValue_OmittedOrEmpty()
    {
        // Empty-string value preserved as `?topic=`.
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("topic", string.Empty)));
        p.Should().Be("/jobs/poll?topic=");
    }

    [Fact]
    public void CanonicalPath_CaseInsensitiveQueryKey_TreatedAsDistinct()
    {
        // ASCII ordinal sort: "Topic" (T=0x54) < "topic" (t=0x74).
        var p = HmacCanonicalForm.BuildCanonicalPathForSender("/jobs/poll", Q(("Topic", "A"), ("topic", "B")));
        p.Should().Be("/jobs/poll?Topic=A&topic=B");
    }

    // === sender↔receiver symmetry test ===

    [Fact]
    public void HmacCanonicalForm_SenderAndReceiver_ProduceIdenticalCanonical_OnEdgeCases()
    {
        // Receiver-side: build an HttpRequest mock with the same wire form; assert byte-equality
        // against BuildCanonicalPathForSender. Locks sender/receiver implementation drift.
        // Case-insensitive key edge case is exercised in CanonicalPath_CaseInsensitiveQueryKey_TreatedAsDistinct
        // (sender-only — ASP.NET's QueryCollection collapses case in some host configurations).
        var cases = new (string path, (string key, string value)[] qs)[]
        {
            ("/jobs/poll", new[] { ("topic", "X"), ("waitMs", "30000") }),
            ("/jobs/poll", new[] { ("topic", "hello world") }),
            ("/jobs/publish", Array.Empty<(string, string)>()),
            ("/jobs/poll", new[] { ("tag", "z"), ("tag", "a") }),
            ("/", new[] { ("city", "İstanbul") }),
            ("/jobs/list", new[] { ("status", "Pending"), ("status", "Running") }),
            ("/jobs/poll", new[] { ("topic", string.Empty) }),
        };
        foreach (var (path, qs) in cases)
        {
            var sender = HmacCanonicalForm.BuildCanonicalPathForSender(path, Q(qs));
            var http = new DefaultHttpContext().Request;
            http.Path = path;
            // QueryCollection from a string built via Uri.EscapeDataString simulating the wire form.
            var queryStr = string.Empty;
            if (qs.Length > 0)
            {
                queryStr = "?" + string.Join("&", qs
                    .OrderBy(p => p.key, StringComparer.Ordinal)
                    .ThenBy(p => p.value, StringComparer.Ordinal)
                    .Select(p => $"{Uri.EscapeDataString(p.key)}={Uri.EscapeDataString(p.value ?? string.Empty)}"));
            }
            http.QueryString = new QueryString(queryStr);
            var receiver = HmacCanonicalForm.BuildCanonicalPathFromRequest(http);
            receiver.Should().Be(sender, $"sender/receiver must agree on path={path} qs.Length={qs.Length}");
        }
    }

    [Fact]
    public void Build_ProducesByteForByteExpectedString_OnFixedInputs()
    {
        // Golden snapshot — pinned canonical string for deterministic inputs.
        var body = "hello"u8.ToArray();
        var canonical = HmacCanonicalForm.Build(
            httpMethod: "POST",
            canonicalPath: "/jobs/publish",
            timestampMillis: 1234567890123,
            nonce: "abc123",
            bodyBytes: body);
        // SHA256("hello") in base64 = "LPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ="
        canonical.Should().Be("POST\n/jobs/publish\n1234567890123\nabc123\nLPJNul+wow4m6DsqxbninhsWHlwfp0JecwQzYpOLmCQ=");
    }

    [Fact]
    public void Build_EmptyBody_ProducesValidCanonical()
    {
        var canonical = HmacCanonicalForm.Build(
            httpMethod: "GET",
            canonicalPath: "/jobs/poll?topic=t",
            timestampMillis: 100,
            nonce: "n",
            bodyBytes: ReadOnlyMemory<byte>.Empty.Span);
        // SHA256("") in base64 = "47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU="
        canonical.Should().Contain("47DEQpj8HBSa+/TImW+5JCeuQeRkm5NMpJWZG3hSuFU=");
        canonical.Should().StartWith("GET\n/jobs/poll?topic=t\n100\nn\n");
    }
}
