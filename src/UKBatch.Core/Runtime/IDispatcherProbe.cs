namespace UKBatch.Runtime;

/// <summary>
/// JobDispatcher trigger-side backpressure probe. v0.1 surface: waiter count + capacity.
/// Concrete impl wires into the dispatcher Channel. Consumed by <c>UKBatchHealthCheck</c>
/// for the BackpressureWarningRatio dimension. Adapter packages may substitute (e.g. expose
/// trigger latency histograms in v0.2.0).
/// </summary>
public interface IDispatcherProbe
{
    /// <summary>Number of trigger callers currently awaiting capacity in the dispatcher channel.</summary>
    long BackpressureWaiterCount { get; }

    /// <summary>Configured channel capacity (== <c>UKBatchOptions.MaxDispatcherQueueDepth</c>).</summary>
    int DispatcherChannelCapacity { get; }
}
