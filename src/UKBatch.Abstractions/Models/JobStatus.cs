namespace UKBatch.Abstractions.Models;

/// <summary>
/// Lifecycle state of a <see cref="JobExecution"/>.
/// <para>
/// Numeric values are stable across versions; new statuses will be appended. Consumers switching on
/// this enum MUST include a <c>default:</c> arm so unknown future values are handled gracefully.
/// </para>
/// </summary>
/// <remarks>
/// State machine (terminal states marked T):
/// <code>
/// Scheduled ──► Pending ──► Running ──► Completed (T)
///                  │           │
///                  │           ├──► Failed (T)         (no retry left)
///                  │           ├──► Retrying ──► Running   (next attempt)
///                  │           ├──► AwaitingApproval ──► Running    (approved)
///                  │           │                       ├► Failed (T)  (rejected / timeout-fail)
///                  │           │                       └► Cancelling ──► Cancelled (T) (manual cancel / host shutdown)
///                  │           └──► Cancelling ──► Cancelled (T)
///                  │
///                  ├──► Cancelling ──► Cancelled (T)    (cancel before dispatch)
///                  └──► Failed (T)                       (validation failure short-circuit)
/// </code>
/// <para>
/// All cancellations flow through <see cref="Cancelling"/>. There is no direct edge to
/// <see cref="Cancelled"/> from any state other than <see cref="Cancelling"/>.
/// </para>
/// </remarks>
public enum JobStatus
{
    /// <summary>Scheduled by cron but not yet eligible to be enqueued (waiting for next fire time).</summary>
    Scheduled = 0,

    /// <summary>Enqueued, not yet dispatched to a worker.</summary>
    Pending = 1,

    /// <summary>Worker is actively executing.</summary>
    Running = 2,

    /// <summary>Execution failed; awaiting retry orchestrator to re-enqueue.</summary>
    Retrying = 3,

    /// <summary>Inside an approval gate, waiting for human action.</summary>
    AwaitingApproval = 4,

    /// <summary>Cancellation requested; worker has not yet exited.</summary>
    Cancelling = 5,

    /// <summary>Terminal: completed successfully.</summary>
    Completed = 6,

    /// <summary>Terminal: failed permanently.</summary>
    Failed = 7,

    /// <summary>Terminal: cancelled before completion.</summary>
    Cancelled = 8,

    /// <summary>
    /// Terminal: the step's run-if condition was not met, so it was skipped without dispatch. Written
    /// directly at insert time (there is no incoming transition — a skipped step is never enqueued or run).
    /// </summary>
    Skipped = 9,
}
