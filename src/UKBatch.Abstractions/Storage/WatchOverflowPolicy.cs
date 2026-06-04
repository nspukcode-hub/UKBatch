namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Overflow policy for <see cref="IJobExecutionReader.WatchAsync"/> when the consumer lags behind
/// the publisher rate.
/// </summary>
/// <remarks>
/// Each value declares the caller's INTENT. Storage adapters are free to honour the intent or
/// to substitute a best-effort approximation when full implementation would degrade hot-path
/// throughput. Behaviour per adapter is documented on the adapter itself; defaults below describe
/// the in-memory adapter shipped with <c>UKBatch.Core</c>.
/// </remarks>
public enum WatchOverflowPolicy
{
    /// <summary>
    /// CALLER INTENT: apply backpressure to the publisher when the per-subscriber buffer fills,
    /// preserving every event in arrival order. Recommended for SignalR push consumers that
    /// must not drop events for dashboard correctness.
    /// </summary>
    /// <remarks>
    /// <b>In-memory adapter behaviour (<c>UKBatch.Core</c>, v0.1.0-alpha):</b> implemented as
    /// <see cref="DropNewest"/> semantics. The per-subscriber channel is built with
    /// <c>BoundedChannelFullMode.DropNewest</c> and the publisher path
    /// (<c>InMemoryJobStore.UpdateStatusAsync</c>) is non-blocking via
    /// <c>Channel.Writer.TryWrite</c>; when the per-subscriber buffer is full the newest event
    /// is silently dropped. The enum value is preserved so future durable adapters
    /// (e.g. <c>UKBatch.Transport.RabbitMQ</c>, EF Core) can implement true awaiting
    /// backpressure without a source-breaking enum rename. Callers requiring guaranteed
    /// delivery in v0.1 must oversize <see cref="WatchOptions.BufferCapacity"/> for their
    /// consumer's worst-case lag.
    /// </remarks>
    Backpressure = 0,

    /// <summary>
    /// Drop the OLDEST queued events when the buffer overflows. Use when the consumer is a metric
    /// aggregator that tolerates loss but wants the most recent state.
    /// </summary>
    DropOldest = 1,

    /// <summary>
    /// Drop NEW events when the buffer overflows. Use when older events are more important than
    /// the newest (rare).
    /// </summary>
    DropNewest = 2,
}
