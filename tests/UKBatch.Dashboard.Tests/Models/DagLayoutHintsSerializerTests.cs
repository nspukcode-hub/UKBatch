using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure unit tests for <see cref="DagLayoutHintsSerializer"/>.
/// Parse / Serialize all-or-nothing tolerance, forward-compat foreign key merge (reset =
/// key removal), NaN/Infinity rejection, mutation safety, tr-TR culture.
/// </summary>
public sealed class DagLayoutHintsSerializerTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────

    private static BatchDefinition DefWithMetadata(IReadOnlyDictionary<string, object?>? metadata) => new()
    {
        Id = "def-1",
        Name = "test",
        Source = BatchSource.Dashboard,
        Steps = Array.Empty<BatchStep>(),
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
        Metadata = metadata,
    };

    /// <summary>Builds a JsonElement-shaped Metadata payload (mirrors EF Core JSON round-trip).</summary>
    private static IReadOnlyDictionary<string, object?> MetadataFromJson(string json)
    {
        using var doc = JsonDocument.Parse($"{{ \"dashboard.layoutHints\": {json} }}");
        var root = doc.RootElement.Clone();
        // Re-wrap into a dict whose value type is JsonElement (mirrors EF read shape).
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = root.GetProperty("dashboard.layoutHints").Clone(),
        };
    }

    // ── Parse_NullMetadata_ReturnsEmpty ────────────────────────────────

    [Fact]
    public void Parse_BatchDefinitionWithoutMetadata_ReturnsEmpty()
    {
        var def = DefWithMetadata(null);
        var result = DagLayoutHintsSerializer.Parse(def);
        result.Should().BeEmpty("definition without Metadata yields an empty hint dict");
    }

    // ── Parse_MissingKey_ReturnsEmpty ─────────────────────────────────

    [Fact]
    public void Parse_MetadataWithoutLayoutHintsKey_ReturnsEmpty()
    {
        var def = DefWithMetadata(new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["other.key"] = "some-value",
        });
        var result = DagLayoutHintsSerializer.Parse(def);
        result.Should().BeEmpty("Metadata without the reserved key yields an empty hint dict");
    }

    // ── Parse_ValidDict_ReturnsAllEntries (in-memory IDictionary path) ─

    [Fact]
    public void Parse_PlainDictionaryPath_ReturnsAllEntries()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["step-a"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 100.0, ["y"] = 200.0 },
                ["step-b"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 300.5, ["y"] = 80.5 },
            },
        };
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().HaveCount(2);
        result["step-a"].X.Should().Be(100.0);
        result["step-a"].Y.Should().Be(200.0);
        result["step-b"].X.Should().Be(300.5);
        result["step-b"].Y.Should().Be(80.5);
    }

    // ── Parse_JsonElement_ReturnsAllEntries (EF round-trip path) ──────

    [Fact]
    public void Parse_JsonElementPath_ReturnsAllEntries()
    {
        var metadata = MetadataFromJson("""{ "step-a": { "x": 100, "y": 200 }, "step-b": { "x": 12.5, "y": 80 } }""");
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().HaveCount(2);
        result["step-a"].X.Should().Be(100);
        result["step-b"].X.Should().Be(12.5);
        result["step-b"].Y.Should().Be(80);
    }

    // ── Parse_MalformedEntry_SkipsBadEntriesKeepsGood ─────────────────

    [Fact]
    public void Parse_MalformedHintEntry_SkipsBadEntriesKeepsGood()
    {
        // Mix of valid entries, missing y, non-number x, wrong-shape value — only the well-formed survive.
        var metadata = MetadataFromJson("""
            {
                "good": { "x": 100, "y": 200 },
                "no-y": { "x": 100 },
                "string-x": { "x": "abc", "y": 100 },
                "wrong": "not-an-object"
            }
        """);
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().ContainKey("good").And.HaveCount(1, "only well-formed entries are kept; malformed silently skipped");
    }

    // ── Parse_NaNCoordinate_Skipped ────────────────────────────────────

    [Fact]
    public void Parse_NaNCoordinate_PlainDict_Skipped()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["nan-x"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = double.NaN, ["y"] = 100.0 },
                ["nan-y"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 50.0, ["y"] = double.NaN },
                ["good"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 50.0, ["y"] = 50.0 },
            },
        };
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().ContainKey("good");
        result.Should().NotContainKey("nan-x");
        result.Should().NotContainKey("nan-y");
    }

    // ── Parse_InfinityCoordinate_Skipped ──────────────────────────────

    [Fact]
    public void Parse_InfinityCoordinate_PlainDict_Skipped()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["inf"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = double.PositiveInfinity, ["y"] = 100.0 },
                ["nin"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = double.NegativeInfinity, ["y"] = 100.0 },
                ["ok"]  = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 50.0, ["y"] = 50.0 },
            },
        };
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().ContainKey("ok");
        result.Should().NotContainKey("inf");
        result.Should().NotContainKey("nin");
    }

    // ── Parse_KeyTooLong_Skipped (129+ char key) ──────────────────────

    [Fact]
    public void Parse_KeyTooLong_Skipped()
    {
        var longKey = new string('k', DagLayoutHintsSerializer.MaxKeyLength + 1);
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                [longKey] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 10.0, ["y"] = 10.0 },
                ["short"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 50.0, ["y"] = 50.0 },
            },
        };
        var result = DagLayoutHintsSerializer.Parse(metadata);
        result.Should().ContainKey("short");
        result.Should().NotContainKey(longKey);
    }

    // ── Parse_TurkishCulture_ParsesCorrectly ────────────────────

    [Fact]
    public void Parse_UnderTurkishCulture_ParsesDoublesCorrectly()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            // System.Text.Json + raw doubles are culture-independent — the parser MUST not regress
            // when the server runs under tr-TR (an invariant the serializer must uphold).
            var metadata = MetadataFromJson("""{ "s": { "x": 12.5, "y": 80.5 } }""");
            var result = DagLayoutHintsSerializer.Parse(metadata);
            result.Should().ContainKey("s");
            result["s"].X.Should().Be(12.5, "InvariantCulture parse keeps '.' as decimal separator under tr-TR");
            result["s"].Y.Should().Be(80.5);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ── Serialize_EmptyHints_EmptyMetadata_ReturnsNull ─────────

    [Fact]
    public void Serialize_EmptyHints_NoForeignKeys_ReturnsNull()
    {
        var result = DagLayoutHintsSerializer.Serialize(
            new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal),
            existingMetadata: null);
        result.Should().BeNull("F-6: reset on a metadata-less batch yields null for wire-byte savings");
    }

    // ── Serialize_EmptyHints_NonEmptyMetadata_RemovesOurKey ──────────

    [Fact]
    public void Serialize_EmptyHints_PreservesForeignKeys_RemovesOurKey()
    {
        var existing = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = new Dictionary<string, object?> { ["s"] = new { x = 1, y = 1 } },
            ["v0.2.featureKey"] = "preserved",
        };
        var result = DagLayoutHintsSerializer.Serialize(
            new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal),
            existing);
        result.Should().NotBeNull();
        result.Should().ContainKey("v0.2.featureKey");
        result.Should().NotContainKey(DagLayoutHintsSerializer.MetadataKey, "F-6: reset removes our key entirely");
    }

    // ── Serialize_WithExistingV02Keys_PreservesThemAlongsideOurs ─────

    [Fact]
    public void Serialize_PreservesForeignKeysAlongsideOurs_ForwardCompatMerge()
    {
        var hints = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal)
        {
            ["s1"] = new() { X = 100, Y = 100 },
        };
        var existing = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["v0.2.foreignKey"] = "kept",
            [DagLayoutHintsSerializer.MetadataKey] = "old-value-to-replace",
        };
        var result = DagLayoutHintsSerializer.Serialize(hints, existing);
        result.Should().NotBeNull();
        result.Should().ContainKey("v0.2.foreignKey");
        result![DagLayoutHintsSerializer.MetadataKey].Should().NotBe("old-value-to-replace",
            "our key was rebuilt — old value dropped");
    }

    // ── Serialize_NaNHint_Skipped ────────────────────────────────────

    [Fact]
    public void Serialize_NaNOrInfinityHint_Skipped()
    {
        var hints = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal)
        {
            ["nan-x"] = new() { X = double.NaN, Y = 0 },
            ["inf-y"] = new() { X = 0, Y = double.PositiveInfinity },
            ["good"]  = new() { X = 10, Y = 20 },
        };
        var result = DagLayoutHintsSerializer.Serialize(hints, existingMetadata: null);
        result.Should().NotBeNull();
        var ourEntry = (IDictionary<string, Dictionary<string, double>>)result![DagLayoutHintsSerializer.MetadataKey]!;
        ourEntry.Should().ContainKey("good");
        ourEntry.Should().NotContainKey("nan-x");
        ourEntry.Should().NotContainKey("inf-y");
    }

    // ── RoundTrip_ParseAfterSerialize_PreservesAllValidHints ─────────

    [Fact]
    public void RoundTrip_SerializeThenJsonThenParse_PreservesAllValidHints()
    {
        // Realistic round-trip: Serialize → JSON over the wire (EF/REST) → Parse. The Serialize
        // output's inner shape (Dictionary<string, double>) is a serializer-internal detail; the
        // wire shape (JsonElement after System.Text.Json) is what Parse encounters in production.
        var hints = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal)
        {
            ["s1"] = new() { X = 100, Y = 200 },
            ["s2"] = new() { X = 12.5, Y = 80.5 },
        };
        var serialized = DagLayoutHintsSerializer.Serialize(hints, existingMetadata: null);
        serialized.Should().NotBeNull();

        // Round-trip through System.Text.Json — same path EF Core JSON column + REST API take.
        var json = JsonSerializer.Serialize(serialized);
        using var doc = JsonDocument.Parse(json);
        var rebuilt = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in doc.RootElement.EnumerateObject())
        {
            rebuilt[prop.Name] = prop.Value.Clone();
        }

        var parsed = DagLayoutHintsSerializer.Parse(rebuilt);
        parsed.Should().HaveCount(2);
        parsed["s1"].X.Should().Be(100);
        parsed["s1"].Y.Should().Be(200);
        parsed["s2"].X.Should().Be(12.5);
        parsed["s2"].Y.Should().Be(80.5);
    }

    // ── Parse_ResultMutation_DoesNotAffectInputMetadata ──────

    [Fact]
    public void Parse_CallerMutatesResult_DoesNotAffectInputMetadata()
    {
        // defensive-copy invariant lock: a caller mutating the returned dict must NOT
        // observe a side effect in the input Metadata (forward-compat round-trip verbatim).
        var inner = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["s1"] = new Dictionary<string, object?>(StringComparer.Ordinal) { ["x"] = 1.0, ["y"] = 2.0 },
        };
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [DagLayoutHintsSerializer.MetadataKey] = inner,
        };
        var parsed = DagLayoutHintsSerializer.Parse(metadata);
        var mutable = parsed.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
        mutable["new-key"] = new DagLayoutHint { X = 999, Y = 999 };
        mutable.Remove("s1");

        // The input Metadata's inner dict is untouched.
        inner.Should().ContainKey("s1");
        inner.Should().NotContainKey("new-key");

        // Re-parsing the original Metadata yields the original entries (no side effects).
        var reparsed = DagLayoutHintsSerializer.Parse(metadata);
        reparsed.Should().HaveCount(1);
        reparsed.Should().ContainKey("s1");
    }

    // ── sanity: Parse(BatchDefinition) null-arg defends ────────────────

    [Fact]
    public void Parse_NullBatchDefinition_ThrowsArgumentNullException()
    {
        var act = () => DagLayoutHintsSerializer.Parse((BatchDefinition)null!);
        act.Should().Throw<ArgumentNullException>("Parse(def) is a public API surface and must defend null args");
    }
}
