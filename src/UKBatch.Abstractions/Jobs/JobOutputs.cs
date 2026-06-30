using System.Collections.Concurrent;

namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Thread-safe sink for the output values a job produces. Values written here are forwarded into the
/// parameters of later steps in the same batch, and (for a cross-service step) returned to the
/// orchestrator. A single instance is shared across the N partition workers of an
/// <see cref="IPartitionedJob{TItem}"/>, so writes go through <see cref="Set"/> — the only
/// synchronized door; a settable property/indexer would race across those workers.
/// </summary>
/// <remarks>
/// Values MUST be JSON-serializable: they cross service boundaries as JSON and are persisted to the
/// execution/run store. A plain scalar (e.g. an <see cref="int"/>) or a JSON-serializable object both
/// work; the consuming step reads them back via the typed <see cref="JobParameters"/> readers.
/// </remarks>
public sealed class JobOutputs
{
    private readonly ConcurrentDictionary<string, object?> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Records an output value under <paramref name="key"/>. Last write wins for a repeated key.
    /// Safe to call concurrently from partition workers.
    /// </summary>
    public void Set(string key, object? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _values[key] = value;
    }

    /// <summary>True when no output has been recorded — the common case, and the zero-overhead path.</summary>
    public bool IsEmpty => _values.IsEmpty;

    /// <summary>Returns an independent snapshot of the recorded outputs.</summary>
    public IReadOnlyDictionary<string, object?> Snapshot()
        => new Dictionary<string, object?>(_values, StringComparer.Ordinal);
}
