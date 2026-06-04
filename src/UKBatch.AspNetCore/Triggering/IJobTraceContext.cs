using System.Diagnostics;

namespace UKBatch.AspNetCore.Triggering;

/// <summary>
/// Manages per-execution <see cref="Activity"/> slots for W3C trace propagation across the
/// trigger boundary. Consumed by <c>JobContext.RestoreRequestActivity()</c> at execution time.
/// </summary>
/// <remarks>
/// Producers (the <c>TriggerWithRequestContextAsync</c> extension methods) snapshot
/// <see cref="Activity.Current"/> BEFORE the awaited <c>TriggerAsync</c> call and pass it
/// explicitly to <see cref="CaptureActivity"/>. This interface NEVER reads
/// <see cref="Activity.Current"/> on its own — it would be unreliable after an await boundary.
/// </remarks>
public interface IJobTraceContext
{
    /// <summary>
    /// Stores the given Activity for the execution id. Accepts <c>null</c> as a valid value
    /// (no Activity was ambient at trigger time).
    /// </summary>
    void CaptureActivity(string executionId, Activity? activity);

    /// <summary>
    /// Reads and removes (one-shot consume) the captured Activity for the execution id.
    /// Returns <c>null</c> if no slot exists.
    /// </summary>
    Activity? ConsumeActivity(string executionId);
}
