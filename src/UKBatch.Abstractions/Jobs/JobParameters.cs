using System.Diagnostics.CodeAnalysis;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Typed, read-only accessor over a job's parameter dictionary. Values are user-supplied and
/// MUST be JSON-serializable for cross-service transport.
/// </summary>
public sealed class JobParameters
{
    private static readonly Dictionary<string, object?> _emptyDict = new(0);

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
    /// Returns the value cast to <typeparamref name="T"/>, or <paramref name="defaultValue"/>
    /// if the key is missing or the value is <c>null</c>.
    /// </summary>
    public T? GetOrDefault<T>(string key, T? defaultValue = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Values.TryGetValue(key, out var raw) && raw is not null)
        {
            return (T)raw;
        }
        return defaultValue;
    }

    /// <summary>
    /// Returns the value cast to <typeparamref name="T"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No entry for <paramref name="key"/>.</exception>
    /// <exception cref="InvalidCastException">The value cannot be converted to <typeparamref name="T"/>.</exception>
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
        return (T)raw;
    }

    /// <summary>
    /// Attempts a non-throwing typed read. Returns <c>true</c> if the key exists and the value is
    /// non-null and assignable to <typeparamref name="T"/>; otherwise <c>false</c> with
    /// <paramref name="value"/> set to <see langword="default"/>.
    /// </summary>
    public bool TryGet<T>(string key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (Values.TryGetValue(key, out var raw) && raw is T typed)
        {
            value = typed;
            return true;
        }
        value = default;
        return false;
    }
}
