namespace UKBatch.AspNetCore.HealthChecks;

/// <summary>
/// Configuration for <see cref="UKBatchHealthCheck"/>.
/// </summary>
public sealed class UKBatchHealthCheckOptions
{
    /// <summary>
    /// Optional dispatcher backpressure threshold (0.0-1.0). If set, the health check reads
    /// <c>IDispatcherProbe.BackpressureWaiterCount / DispatcherChannelCapacity</c> and returns
    /// <see cref="Microsoft.Extensions.Diagnostics.HealthChecks.HealthStatus.Degraded"/> when the
    /// ratio exceeds this threshold. Default <c>null</c> = backpressure dimension disabled.
    /// </summary>
    /// <remarks>
    /// This probe observes the DISPATCHER (trigger backpressure), NOT the WatchAsync feed
    /// (which is deferred via <c>IWatchBackpressureProbe</c>).
    /// </remarks>
    public double? BackpressureWarningRatio { get; set; }
}
