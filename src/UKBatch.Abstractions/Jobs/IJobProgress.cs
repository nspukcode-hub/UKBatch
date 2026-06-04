namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Progress reporter for a single job execution.
/// <para>
/// Implementations MUST be thread-safe; concurrent readers MUST observe monotonically non-decreasing
/// values for <see cref="Processed"/> and <see cref="Failed"/>. Implementations should use
/// <see cref="System.Threading.Interlocked"/> or equivalent. <see cref="Total"/> may transition from
/// <c>null</c> to non-null exactly once; subsequent <see cref="SetTotal"/> calls are ignored.
/// </para>
/// </summary>
public interface IJobProgress
{
    /// <summary>Total expected items, or <c>null</c> when unknown (e.g. streaming source).</summary>
    long? Total { get; }

    /// <summary>Items processed successfully so far.</summary>
    long Processed { get; }

    /// <summary>Items that failed permanently after retry exhaustion (for partitioned jobs).</summary>
    long Failed { get; }

    /// <summary>
    /// Sets the total expected count; idempotent — implementations MUST honour only the first
    /// non-null call and ignore subsequent values.
    /// </summary>
    void SetTotal(long total);

    /// <summary>Atomically increments <see cref="Processed"/> by 1.</summary>
    void Increment();

    /// <summary>Atomically increments <see cref="Processed"/> by <paramref name="count"/>.</summary>
    void Increment(long count);

    /// <summary>Atomically increments <see cref="Failed"/> by 1.</summary>
    void ReportFailure();

    /// <summary>
    /// Atomically increments <see cref="Failed"/> by <paramref name="count"/>. Preferred over calling
    /// <see cref="ReportFailure()"/> in a loop when a single batch of items fails together.
    /// </summary>
    void ReportFailure(long count);

    /// <summary>Reports a free-form status message; surfaced to the dashboard via SignalR.</summary>
    void ReportStatus(string message);
}
