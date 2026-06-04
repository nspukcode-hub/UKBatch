namespace UKBatch.Runtime;

/// <summary>
/// WatchAsync feed backpressure probe. v0.1 surface: cumulative drop counter.
/// </summary>
/// <remarks>
/// <b>No concrete impl in v0.1</b> — the in-memory adapter does NOT
/// maintain per-subscription drop counters; v0.2.0 EF / Redis / RabbitMQ adapters introduce
/// the metric. The interface is declared in v0.1 so v0.2 adapters can implement against a
/// stable contract.
/// </remarks>
public interface IWatchBackpressureProbe
{
    /// <summary>Cumulative WatchAsync drops since process start. v0.1: always 0 (no concrete impl).</summary>
    long CumulativeDroppedCount { get; }
}
