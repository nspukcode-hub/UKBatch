using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Typed, read-only accessor over a job's parameter dictionary. Values are user-supplied and
/// MUST be JSON-serializable for cross-service transport.
/// </summary>
/// <remarks>
/// Reads are JSON-aware. A value produced locally is read back as its original CLR type (the fast
/// path, unchanged behavior). A value that crossed a service boundary, or was rehydrated from a
/// durable store on resume, arrives as a <see cref="JsonElement"/>; the typed readers then deserialize
/// it into the requested type. So <c>TryGet&lt;int&gt;</c> and <c>TryGet&lt;MyDto&gt;</c> work identically
/// for local and cross-service values.
/// </remarks>
public sealed class JobParameters
{
    private static readonly Dictionary<string, object?> _emptyDict = new(0);

    private static readonly JsonSerializerOptions _jsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Every writer that carries these values (HTTP/RabbitMQ wire options, the EF JSON columns)
        // serializes enums as their string names. Without the matching converter a typed enum read
        // works locally (boxed CLR fast path) but throws on the cross-service / resumed JsonElement
        // shape — the exact local-vs-remote fork this class promises not to have. The converter also
        // still accepts numeric enum tokens, so both wire forms read fine.
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Empty parameter set.</summary>
    public static JobParameters Empty { get; } = new(_emptyDict, noCopy: true);

    /// <summary>
    /// Creates a parameter set backed by the given dictionary; a defensive copy is taken so the
    /// caller's mutations do not affect this instance. Safe to use from untrusted call sites.
    /// </summary>
    public JobParameters(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values.Count == 0
            ? _emptyDict
            : new Dictionary<string, object?>(values, StringComparer.Ordinal);
    }

    private JobParameters(IReadOnlyDictionary<string, object?> values, bool noCopy)
    {
        Values = values;
    }

    /// <summary>
    /// Wraps an existing dictionary WITHOUT a defensive copy. The caller MUST NOT mutate the
    /// dictionary after handing it over; intended for trusted call sites such as deserialization
    /// pipelines and the dispatcher hot path where the dict is freshly allocated and immutable
    /// by convention.
    /// </summary>
    public static JobParameters WrapWithoutCopy(IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return values.Count == 0 ? Empty : new JobParameters(values, noCopy: true);
    }

    /// <summary>Raw underlying values (read-only view).</summary>
    public IReadOnlyDictionary<string, object?> Values { get; }

    /// <summary>True if the key exists (value may still be <c>null</c>).</summary>
    public bool Contains(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Values.ContainsKey(key);
    }

    /// <summary>
    /// Returns the value as <typeparamref name="T"/>, or <paramref name="defaultValue"/> if the key is
    /// missing or the value is <c>null</c>. A <see cref="JsonElement"/> value (cross-service / resumed)
    /// is deserialized into <typeparamref name="T"/>; a genuinely incompatible CLR value still throws
    /// <see cref="InvalidCastException"/> as before.
    /// </summary>
    public T? GetOrDefault<T>(string key, T? defaultValue = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Values.TryGetValue(key, out var raw) && raw is not null)
        {
            if (raw is T typed)
            {
                return typed;
            }
            if (raw is JsonElement element)
            {
                // A JSON null is the cross-service / resumed shape of a null value. Deserialize<T> on a null
                // token throws JsonException for a value-type T, so short-circuit to honor the documented
                // "null → defaultValue" contract (a reference-type T would already null-coalesce below).
                if (element.ValueKind == JsonValueKind.Null)
                {
                    return defaultValue;
                }
                return element.Deserialize<T>(_jsonReadOptions) ?? defaultValue;
            }
            return (T)raw;
        }
        return defaultValue;
    }

    /// <summary>
    /// Returns the value as <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No entry for <paramref name="key"/>.</exception>
    /// <exception cref="InvalidCastException">The value is null, or cannot be converted to <typeparamref name="T"/>.</exception>
    public T GetRequired<T>(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!Values.TryGetValue(key, out var raw))
        {
            throw new KeyNotFoundException($"Required job parameter '{key}' was not provided.");
        }
        if (raw is null)
        {
            throw new InvalidCastException($"Required job parameter '{key}' is null but '{typeof(T).Name}' was requested.");
        }
        if (raw is T typed)
        {
            return typed;
        }
        if (raw is JsonElement element)
        {
            // A JSON null is the cross-service / resumed shape of a null value — surface the same
            // InvalidCastException as the CLR-null branch above (not a JsonException), so a null output
            // fails identically whether it arrived local or across a service boundary.
            if (element.ValueKind == JsonValueKind.Null)
            {
                throw new InvalidCastException($"Required job parameter '{key}' is null but '{typeof(T).Name}' was requested.");
            }
            return element.Deserialize<T>(_jsonReadOptions)
                ?? throw new InvalidCastException($"Required job parameter '{key}' deserialized to null for '{typeof(T).Name}'.");
        }
        return (T)raw;
    }

    /// <summary>
    /// Attempts a non-throwing typed read. Returns <c>true</c> if the key exists and the value is
    /// non-null and either assignable to <typeparamref name="T"/> or a <see cref="JsonElement"/> that
    /// deserializes into <typeparamref name="T"/>; otherwise <c>false</c> with <paramref name="value"/>
    /// set to <see langword="default"/>.
    /// </summary>
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Values.TryGetValue(key, out var raw))
        {
            if (raw is T typed)
            {
                value = typed;
                return true;
            }
            if (raw is JsonElement element && TryDeserialize(element, out value))
            {
                return true;
            }
        }
        value = default;
        return false;
    }

    private static bool TryDeserialize<T>(JsonElement element, [MaybeNullWhen(false)] out T value)
    {
        try
        {
            var deserialized = element.Deserialize<T>(_jsonReadOptions);
            if (deserialized is not null)
            {
                value = deserialized;
                return true;
            }
        }
        catch (JsonException)
        {
            // Value is a JsonElement but not convertible to T — treat as a missing typed read.
        }
        value = default;
        return false;
    }
}
