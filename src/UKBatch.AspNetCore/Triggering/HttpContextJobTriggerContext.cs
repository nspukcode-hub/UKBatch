using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace UKBatch.AspNetCore.Triggering;

/// <summary>
/// HTTP-aware implementation of both <see cref="IJobTriggerContext"/> (identity resolver) and
/// <see cref="IJobTraceContext"/> (per-execution <see cref="Activity"/> slot manager). Registered as
/// a singleton; two service descriptors share the same instance (the identity/trace ISP split).
/// </summary>
/// <remarks>
/// Activity slots expire after <see cref="TtlSeconds"/> seconds via a cleanup callback driven by the
/// configured <see cref="TimeProvider"/>. The cleanup increments an unconsumed-eviction counter and
/// emits a single <c>Information</c>-level log on every <see cref="DiagnosticLogEveryN"/> evictions
/// to alert the user that <c>JobContext.RestoreRequestActivity()</c> may be missing in their
/// <c>IJob</c> implementation.
/// </remarks>
internal sealed class HttpContextJobTriggerContext : IJobTriggerContext, IJobTraceContext, IDisposable
{
    private const int TtlSeconds = 60;                                 // unconsumed Activity slots expire after this
    private const int DiagnosticLogEveryN = 100;                       // emit one diagnostic per N expirations

    private readonly IHttpContextAccessor _accessor;
    private readonly ILogger<HttpContextJobTriggerContext> _logger;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, Slot> _slots = new(StringComparer.Ordinal);
    private readonly ITimer _cleanupTimer;
    private long _unconsumedEvictions;                                 // diagnostic counter

    private readonly record struct Slot(Activity? Activity, long CapturedAtTicks);

    /// <summary>
    /// Constructs the context. <paramref name="timeProvider"/> is optional and defaults to
    /// <see cref="TimeProvider.System"/>; tests inject a fake time provider to drive the cleanup
    /// timer deterministically.
    /// </summary>
    public HttpContextJobTriggerContext(
        IHttpContextAccessor accessor,
        ILogger<HttpContextJobTriggerContext> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(accessor);
        ArgumentNullException.ThrowIfNull(logger);
        _accessor = accessor;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
        _cleanupTimer = _time.CreateTimer(
            CleanupCallback,
            state: null,
            dueTime: TimeSpan.FromSeconds(TtlSeconds),
            period: TimeSpan.FromSeconds(TtlSeconds));
    }

    /// <inheritdoc/>
    public string? GetTriggeredByOrNull()
    {
        var http = _accessor.HttpContext;
        if (http is null) return null;
        // Identity.Name takes precedence (works for cookie auth, basic auth, dev auth).
        var name = http.User.Identity?.Name;
        if (!string.IsNullOrEmpty(name)) return name;
        // Fall back to the standard 'sub' claim used by OIDC / JWT.
        return http.User.FindFirst("sub")?.Value;
    }

    /// <inheritdoc/>
    public void CaptureActivity(string executionId, Activity? activity)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        _slots[executionId] = new Slot(activity, _time.GetTimestamp());
    }

    /// <inheritdoc/>
    public Activity? ConsumeActivity(string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        return _slots.TryRemove(executionId, out var slot) ? slot.Activity : null;
    }

    /// <summary>
    /// Periodic cleanup callback driven by <see cref="ITimer"/>. Removes every slot older than
    /// <see cref="TtlSeconds"/> and emits a single diagnostic log when the cumulative eviction
    /// count crosses each <see cref="DiagnosticLogEveryN"/> boundary.
    /// </summary>
    private void CleanupCallback(object? state)
    {
        var cutoffTicks = _time.GetTimestamp() - (long)(TtlSeconds * _time.TimestampFrequency);
        var evicted = 0;
        foreach (var kvp in _slots)
        {
            if (kvp.Value.CapturedAtTicks < cutoffTicks &&
                _slots.TryRemove(kvp.Key, out _))
            {
                evicted++;
            }
        }
        if (evicted > 0)
        {
            var total = Interlocked.Add(ref _unconsumedEvictions, evicted);
            // Only log once per N evictions (crosses a multiple-of-N boundary).
            if (total / DiagnosticLogEveryN > (total - evicted) / DiagnosticLogEveryN)
            {
                _logger.LogInformation(
                    "UKBatch.AspNetCore: {Count} captured request activities were never consumed by RestoreRequestActivity; " +
                    "trace correlation may be incomplete. Did you forget 'using var _ = ctx.RestoreRequestActivity();' in your IJob?",
                    total);
            }
        }
    }

    /// <summary>Disposes the cleanup timer.</summary>
    public void Dispose() => _cleanupTimer.Dispose();
}
