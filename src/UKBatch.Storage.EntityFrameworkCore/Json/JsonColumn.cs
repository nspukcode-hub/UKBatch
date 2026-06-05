using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using UKBatch.Abstractions.Batches;

namespace UKBatch.Storage.EntityFrameworkCore.Json;

/// <summary>
/// Shared <see cref="ValueConverter"/> + <see cref="ValueComparer"/> factory for the JSON columns
/// (System.Text.Json). A converter ALONE is not enough: EF's change-tracker compares the reference of
/// a converted property by default, so a mutated dictionary/list with the same reference is seen as
/// unchanged and the UPDATE is silently skipped (data loss). Every JSON column MUST also supply a
/// value-comparing comparer with a deep-copy snapshot.
/// </summary>
/// <remarks>
/// <para><b>Hot-path fast-path:</b> the equality comparer short-circuits on
/// <see cref="object.ReferenceEquals"/> BEFORE serializing. The source dictionaries/lists are
/// <c>init</c>-only on immutable records, so reference-equality is a sound proxy: a real change to an
/// <c>init</c>-only member requires a NEW reference (which fails <c>ReferenceEquals</c> → falls through
/// to serialize-compare). This means a status-only <c>UpdateStatusAsync</c> (which never touches
/// <c>Parameters</c>/<c>Steps</c>) pays no serialize on every status write. The deep-copy SNAPSHOT is
/// retained for correctness.</para>
/// <para><b>Enum-as-name (forward-compat):</b> <see cref="JsonStringEnumConverter"/> serializes
/// nested enums (<c>BatchStepType</c>, <c>ParallelJoinPolicy</c>, <c>ApprovalTimeoutAction</c>) as
/// NAMES so a v0.2 reader of a v0.1 blob round-trips. The <c>BatchStep.Metadata</c> verbatim invariant
/// depends on the whole step tree being one JSON blob.</para>
/// <para><b><c>object?</c> round-trip caveat:</b> dictionary <c>object?</c> values deserialize as
/// <see cref="JsonElement"/>, not the original CLR type. This matches the existing "JSON-serializable /
/// raw dict" contract; equality is by serialized form, not CLR-type identity.</para>
/// </remarks>
internal static class JsonColumn
{
    internal static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.General)
    {
        Converters = { new JsonStringEnumConverter() },
        // Null-valued dictionary entries (e.g. {"customerId": null}) MUST survive the round-trip: an
        // explicit null is meaningful data, and dropping the key here would silently lose it on persist,
        // diverging from the in-memory store which keeps it. With the default (Never) such a key
        // serializes as "k":null and deserializes back into a null value for the object?-typed entry.
    };

    /// <summary>Converter+comparer pair for an <see cref="IReadOnlyDictionary{TKey,TValue}"/> (string,object?) JSON column.</summary>
    public static (ValueConverter Converter, ValueComparer Comparer) ForDictionary()
    {
        var converter = new ValueConverter<IReadOnlyDictionary<string, object?>, string>(
            v => JsonSerializer.Serialize(v, Opts),
            v => JsonSerializer.Deserialize<Dictionary<string, object?>>(v, Opts)
                 ?? new Dictionary<string, object?>());

        var comparer = new ValueComparer<IReadOnlyDictionary<string, object?>>(
            // equals: reference-equal ⇒ identical (init-only immutable source). Serialize-compare only on the cold path.
            (a, b) => ReferenceEquals(a, b)
                      || JsonSerializer.Serialize(a, Opts) == JsonSerializer.Serialize(b, Opts),
            // hashcode: over the serialized form.
            v => v == null ? 0 : JsonSerializer.Serialize(v, Opts).GetHashCode(StringComparison.Ordinal),
            // snapshot: deep copy so the tracker holds an independent baseline (round-trip clone).
            v => JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(v, Opts), Opts)!);

        return (converter, comparer);
    }

    /// <summary>Converter+comparer pair for an <see cref="IReadOnlyList{T}"/> of <see cref="BatchStep"/> JSON column.</summary>
    public static (ValueConverter Converter, ValueComparer Comparer) ForStepList()
    {
        var converter = new ValueConverter<IReadOnlyList<BatchStep>, string>(
            v => JsonSerializer.Serialize(v, Opts),
            v => JsonSerializer.Deserialize<List<BatchStep>>(v, Opts)
                 ?? new List<BatchStep>());

        var comparer = new ValueComparer<IReadOnlyList<BatchStep>>(
            (a, b) => ReferenceEquals(a, b)
                      || JsonSerializer.Serialize(a, Opts) == JsonSerializer.Serialize(b, Opts),
            v => v == null ? 0 : JsonSerializer.Serialize(v, Opts).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<List<BatchStep>>(JsonSerializer.Serialize(v, Opts), Opts)!);

        return (converter, comparer);
    }

    /// <summary>Converter+comparer pair for a single <see cref="ApprovalGateConfig"/> JSON column.</summary>
    public static (ValueConverter Converter, ValueComparer Comparer) ForApprovalConfig()
    {
        var converter = new ValueConverter<ApprovalGateConfig, string>(
            v => JsonSerializer.Serialize(v, Opts),
            v => JsonSerializer.Deserialize<ApprovalGateConfig>(v, Opts)!);

        var comparer = new ValueComparer<ApprovalGateConfig>(
            (a, b) => ReferenceEquals(a, b)
                      || JsonSerializer.Serialize(a, Opts) == JsonSerializer.Serialize(b, Opts),
            v => v == null ? 0 : JsonSerializer.Serialize(v, Opts).GetHashCode(StringComparison.Ordinal),
            v => JsonSerializer.Deserialize<ApprovalGateConfig>(JsonSerializer.Serialize(v, Opts), Opts)!);

        return (converter, comparer);
    }
}
