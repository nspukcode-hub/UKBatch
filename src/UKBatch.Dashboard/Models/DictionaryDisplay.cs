using System.Globalization;
using System.Text.Json;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// Formats the <c>object?</c> values of a persisted dictionary (job outputs, run forwarded state) for
/// read-only display. The single source of truth for turning a value that may have been read back from a
/// JSON column into a human-readable string. Shared by the read-only key/value panels so the rule lives
/// in one place.
/// </summary>
/// <remarks>
/// A value round-tripped through a JSON column deserializes as a <see cref="JsonElement"/>, not its
/// original CLR type, so a plain <c>value.ToString()</c> would print the element kind for objects/arrays
/// rather than the data. This formatter renders a scalar element as its text and a structured element
/// (object/array) as compact JSON; non-JSON CLR values fall back to invariant-culture formatting.
/// </remarks>
public static class DictionaryDisplay
{
    /// <summary>
    /// Returns a display string for <paramref name="value"/>. Scalars render as their text; JSON
    /// objects/arrays render as compact JSON; <c>null</c> renders as the empty string.
    /// </summary>
    public static string Format(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        JsonElement e => FormatJsonElement(e),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string FormatJsonElement(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString() ?? string.Empty,
        JsonValueKind.Null => string.Empty,
        // GetRawText() keeps numbers/booleans verbatim and renders objects/arrays as compact JSON,
        // which is the readable form for a structured value read back from a JSON column.
        _ => e.GetRawText(),
    };
}
