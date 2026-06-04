using System.Globalization;
using System.Text.Json;
using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// Parse / serialize layout hints from <see cref="BatchDefinition.Metadata"/> opaque dict.
/// All-or-nothing tolerant: malformed entries are skipped, missing key returns empty dict, NaN /
/// Infinity rejected. Forward-compat: extra v0.2+ keys (e.g. <c>"v"</c> version stamp, group bounds)
/// inside our reserved <see cref="MetadataKey"/> entry are tolerated — parser only consumes nested
/// <c>{x, y}</c>; siblings of <see cref="MetadataKey"/> in the Metadata dict are preserved verbatim
/// on Serialize (forward-compat merge).
/// </summary>
public static class DagLayoutHintsSerializer
{
    /// <summary>Reserved key inside <see cref="BatchDefinition.Metadata"/> for dashboard layout hints.</summary>
    public const string MetadataKey = "dashboard.layoutHints";

    /// <summary>Maximum per-stepId key length accepted by the parser (defensive — operator-set step ids never exceed 64).</summary>
    public const int MaxKeyLength = 128;

    /// <summary>
    /// Parses layout hints from a definition's <see cref="BatchDefinition.Metadata"/>; returns an
    /// empty dict when the key is missing or any branch is malformed. Never throws on input.
    /// </summary>
    public static IReadOnlyDictionary<string, DagLayoutHint> Parse(BatchDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return Parse(def.Metadata);
    }

    /// <summary>Parses from a raw Metadata dict (testing seam).</summary>
    public static IReadOnlyDictionary<string, DagLayoutHint> Parse(IReadOnlyDictionary<string, object?>? metadata)
    {
        var empty = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal);
        if (metadata is null || !metadata.TryGetValue(MetadataKey, out var raw) || raw is null) return empty;

        // EF Core JSON columns round-trip as JsonElement; the InMemory store keeps the raw dict.
        // Both shapes parse — anything else is silently ignored (forward-compat tolerance).
        if (raw is JsonElement je)
        {
            if (je.ValueKind != JsonValueKind.Object) return empty;
            var result = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal);
            foreach (var prop in je.EnumerateObject())
            {
                if (prop.Name.Length == 0 || prop.Name.Length > MaxKeyLength) continue;
                if (prop.Value.ValueKind != JsonValueKind.Object) continue;
                if (!prop.Value.TryGetProperty("x", out var xEl) || !TryReadDouble(xEl, out var x)) continue;
                if (!prop.Value.TryGetProperty("y", out var yEl) || !TryReadDouble(yEl, out var y)) continue;
                result[prop.Name] = new DagLayoutHint { X = x, Y = y };
            }
            return result;
        }

        if (raw is IDictionary<string, object?> dict)
        {
            var result = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal);
            foreach (var (k, v) in dict)
            {
                if (k.Length == 0 || k.Length > MaxKeyLength) continue;
                if (TryReadHint(v, out var hint)) result[k] = hint;
            }
            return result;
        }

        return empty;
    }

    /// <summary>
    /// Serializes hints into a new Metadata dict, merging with existing keys (other v0.2+ entries
    /// preserved). Returns <c>null</c> when <paramref name="hints"/> is empty AND no foreign keys
    /// remain in <paramref name="existingMetadata"/> — callers SHOULD pass <c>null</c> to
    /// <c>UpdateBatchRequest.Metadata</c> on reset for wire-byte savings (the key is removed).
    /// </summary>
    public static IReadOnlyDictionary<string, object?>? Serialize(
        IReadOnlyDictionary<string, DagLayoutHint> hints,
        IReadOnlyDictionary<string, object?>? existingMetadata)
    {
        ArgumentNullException.ThrowIfNull(hints);

        // Carry foreign keys (v0.2+ entries alien to this serializer) but DROP our own MetadataKey;
        // we rebuild it below when hints is non-empty, or skip it entirely when empty.
        var merged = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (existingMetadata is not null)
        {
            foreach (var (k, v) in existingMetadata)
            {
                if (!string.Equals(k, MetadataKey, StringComparison.Ordinal)) merged[k] = v;
            }
        }

        if (hints.Count == 0)
        {
            // Reset = key removal. If no foreign keys remain, return null so the caller can
            // send Metadata=null on UpdateBatchRequest (saves wire bytes + signals "no metadata").
            return merged.Count == 0 ? null : merged;
        }

        var ourEntry = new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal);
        foreach (var (stepId, h) in hints)
        {
            if (double.IsNaN(h.X) || double.IsInfinity(h.X)) continue;
            if (double.IsNaN(h.Y) || double.IsInfinity(h.Y)) continue;
            if (stepId.Length == 0 || stepId.Length > MaxKeyLength) continue;
            ourEntry[stepId] = new Dictionary<string, double>(StringComparer.Ordinal) { ["x"] = h.X, ["y"] = h.Y };
        }

        merged[MetadataKey] = ourEntry;
        return merged;
    }

    private static bool TryReadDouble(JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out var d) && !double.IsNaN(d) && !double.IsInfinity(d))
        {
            value = d;
            return true;
        }
        return false;
    }

    private static bool TryReadHint(object? raw, out DagLayoutHint hint)
    {
        hint = default!;
        if (raw is not IDictionary<string, object?> dict) return false;
        if (!dict.TryGetValue("x", out var rx) || !TryConvertDouble(rx, out var x)) return false;
        if (!dict.TryGetValue("y", out var ry) || !TryConvertDouble(ry, out var y)) return false;
        hint = new DagLayoutHint { X = x, Y = y };
        return true;
    }

    private static bool TryConvertDouble(object? raw, out double value)
    {
        value = 0;
        switch (raw)
        {
            case double d when !double.IsNaN(d) && !double.IsInfinity(d): value = d; return true;
            case int i: value = i; return true;
            case long l: value = l; return true;
            case decimal dec: value = (double)dec; return true;
            case string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
                              && !double.IsNaN(parsed) && !double.IsInfinity(parsed):
                value = parsed; return true;
            default: return false;
        }
    }
}
