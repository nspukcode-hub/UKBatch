using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using UKBatch.Runtime;

namespace UKBatch.AspNetCore.HealthChecks;

/// <summary>
/// Readiness signal for the UKBatch host. Returns <see cref="HealthStatus.Unhealthy"/> until
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> fires; <see cref="HealthStatus.Healthy"/>
/// thereafter. There is an optional backpressure dimension that returns
/// <see cref="HealthStatus.Degraded"/> when dispatcher saturation exceeds
/// <see cref="UKBatchHealthCheckOptions.BackpressureWarningRatio"/>.
/// </summary>
/// <remarks>
/// Tagged <c>"ready"</c> (NOT <c>"live"</c>). Kubernetes-style probes that consume this should map
/// it to a readiness probe — failure delays traffic but does NOT kill the pod. Users wanting a
/// liveness probe should rely on the framework's default <c>MapHealthChecks</c> without a tag filter.
/// </remarks>
internal sealed class UKBatchHealthCheck : IHealthCheck
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IDispatcherProbe? _dispatcherProbe;
    private readonly UKBatchHealthCheckOptions _options;

    /// <summary>
    /// Parameterless-probe constructor. The dispatcher backpressure dimension is optional; the
    /// probe is null when the consumer has not registered an <see cref="IDispatcherProbe"/>.
    /// The backpressure parameters are defaulted (consumed via DI's optional
    /// binding for <c>IDispatcherProbe?</c> + <c>IOptions{UKBatchHealthCheckOptions}</c>).
    /// </summary>
    public UKBatchHealthCheck(IHostApplicationLifetime lifetime)
        : this(lifetime, options: null, dispatcherProbe: null) { }

    /// <summary>DI-resolved constructor.</summary>
    public UKBatchHealthCheck(
        IHostApplicationLifetime lifetime,
        IOptions<UKBatchHealthCheckOptions>? options,
        IDispatcherProbe? dispatcherProbe)
    {
        ArgumentNullException.ThrowIfNull(lifetime);
        _lifetime = lifetime;
        _options = options?.Value ?? new UKBatchHealthCheckOptions();
        _dispatcherProbe = dispatcherProbe;
    }

    /// <inheritdoc/>
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Note: IHealthCheck.CheckHealthAsync declares CT with `= default` — that's the framework's
        // interface, not ours. Our convention of no-default CancellationToken applies only to OUR
        // public interfaces.
        if (!_lifetime.ApplicationStarted.IsCancellationRequested)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "UKBatch host not started yet",
                data: new Dictionary<string, object> { ["state"] = "starting" }));
        }

        // Optional dispatcher backpressure dimension.
        if (_dispatcherProbe is { } probe && _options.BackpressureWarningRatio is { } threshold)
        {
            var waiters = probe.BackpressureWaiterCount;
            var capacity = probe.DispatcherChannelCapacity;
            if (capacity > 0)
            {
                var ratio = (double)waiters / capacity;
                if (ratio >= threshold)
                {
                    return Task.FromResult(HealthCheckResult.Degraded(
                        $"Dispatcher backpressure {ratio:F2} >= threshold {threshold:F2}",
                        data: new Dictionary<string, object>
                        {
                            ["state"] = "degraded",
                            ["backpressureRatio"] = ratio,
                            ["backpressureWaiters"] = waiters,
                            ["dispatcherCapacity"] = capacity,
                        }));
                }
            }
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "ok",
            data: new Dictionary<string, object> { ["state"] = "running" }));
    }
}
