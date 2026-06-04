using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Triggering;

namespace UKBatch.AspNetCore.Tracing;

/// <summary>
/// Extension methods on <see cref="JobContext"/> for W3C trace correlation across the trigger
/// boundary.
/// </summary>
public static class JobContextActivityExtensions
{
    private static readonly IDisposable NoOp = new NoOpDisposable();

    /// <summary>
    /// Restores the captured request <see cref="Activity"/> as <see cref="Activity.Current"/> for
    /// the scope of the returned <see cref="IDisposable"/>. If no Activity was captured for this
    /// execution (e.g. the job was scheduler-triggered, not HTTP-triggered), returns a no-op disposable.
    /// </summary>
    /// <remarks>
    /// <para><b>REQUIRED for trace correlation.</b> Without this call, logs and child spans inside
    /// the job will NOT be correlated with the HTTP request that triggered them — trace propagation
    /// is opt-in by design (the runtime cannot intercept user job code without breaking
    /// <see cref="JobContext"/>'s frozen contract).</para>
    /// <para>If the user forgets this call, unconsumed Activity slots eventually expire via a 60s TTL
    /// in <see cref="IJobTraceContext"/>. A diagnostic Information-level log fires once per 100
    /// unconsumed expirations to alert the user.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// public async Task ExecuteAsync(JobContext ctx, CancellationToken ct)
    /// {
    ///     using var _ = ctx.RestoreRequestActivity();
    ///     // ctx.Logger emits with trace ids correlated to the originating request.
    ///     await DoWorkAsync(ct);
    /// }
    /// </code>
    /// </example>
    public static IDisposable RestoreRequestActivity(this JobContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var trace = context.Services.GetService<IJobTraceContext>();
        var captured = trace?.ConsumeActivity(context.ExecutionId);
        if (captured is null) return NoOp;
        // Restore by starting a child Activity whose parent is the captured trace id.
        var restored = new Activity(captured.OperationName ?? "ukbatch.job");
        if (!string.IsNullOrEmpty(captured.Id)) restored.SetParentId(captured.Id);
        restored.Start();
        return new RestoreScope(restored);
    }

    /// <summary>
    /// Lightweight scope wrapper that stops the restored <see cref="Activity"/> on disposal.
    /// </summary>
    private sealed class RestoreScope : IDisposable
    {
        private readonly Activity _activity;
        public RestoreScope(Activity activity) => _activity = activity;
        public void Dispose() => _activity.Stop();
    }

    /// <summary>No-op disposable returned when no Activity was captured for the execution.</summary>
    private sealed class NoOpDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
