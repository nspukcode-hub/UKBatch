using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Serializable, durable snapshot of an approval gate's lifecycle. Persisted by
/// <see cref="IApprovalGateStore"/>; assembled from the runtime's internal
/// <c>ApprovalGateRegistration</c> on create and updated on decision.
/// </summary>
/// <remarks>
/// <b>Durability boundary (durable RECORD, not durable RESUME):</b> persisting this record preserves
/// the gate's history after a restart, but it does NOT resume the paused batch — the
/// in-memory awaiter (<c>ApprovalGateService.AwaitApprovalAsync</c>'s <c>TaskCompletionSource</c>) is
/// gone after a process exit. Mid-flight resume of a paused workflow is a v0.2 durable-scheduler
/// concern (tracked with <see cref="Models.JobStatus.Scheduled"/>).
/// </remarks>
public sealed record class PersistedApprovalGate
{
    /// <summary>Unique gate identifier (matches <c>PendingApproval.ApprovalId</c>).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Parent batch RUN id.</summary>
    public required string BatchId { get; init; }

    /// <summary>Parent batch step id.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Definition id of the parent batch, when known; <c>null</c> for Code-only batches that lack a stored definition.</summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>Gate configuration snapshot (title, allowed roles, timeout). Stored as JSON.</summary>
    public required ApprovalGateConfig Config { get; init; }

    /// <summary>Current record status.</summary>
    public required ApprovalRecordStatus Status { get; init; }

    /// <summary>UTC time the gate became pending.</summary>
    public required DateTimeOffset PendingSinceUtc { get; init; }

    /// <summary>UTC deadline computed from <see cref="ApprovalGateConfig.TimeoutAfter"/>; <c>null</c> if indefinite.</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>Decision outcome once terminal; <c>null</c> while pending.</summary>
    public ApprovalRecordOutcome? Outcome { get; init; }

    /// <summary>Identity that decided; <c>null</c> while pending.</summary>
    public string? DecidedBy { get; init; }

    /// <summary>UTC decision time; <c>null</c> while pending.</summary>
    public DateTimeOffset? DecidedAtUtc { get; init; }

    /// <summary>Free-text note (approve) or reason (reject); <c>null</c> while pending or when omitted.</summary>
    public string? Note { get; init; }
}

/// <summary>Lifecycle phase of a <see cref="PersistedApprovalGate"/>.</summary>
public enum ApprovalRecordStatus
{
    /// <summary>Awaiting a decision (or already orphaned by a restart but still decidable for audit).</summary>
    Pending = 0,

    /// <summary>A terminal decision was recorded — see <see cref="PersistedApprovalGate.Outcome"/>.</summary>
    Decided = 1,
}

/// <summary>Terminal decision recorded against an approval gate.</summary>
public enum ApprovalRecordOutcome
{
    /// <summary>Approved by an operator.</summary>
    Approved = 0,

    /// <summary>Auto-approved on timeout (<see cref="ApprovalTimeoutAction.AutoApprove"/>).</summary>
    AutoApproved = 1,

    /// <summary>Rejected by an operator.</summary>
    Rejected = 2,

    /// <summary>Timed out with <see cref="ApprovalTimeoutAction.Fail"/>.</summary>
    TimedOutFail = 3,

    /// <summary>
    /// Cancelled while pending — the parent batch was torn down (host shutdown or explicit
    /// batch cancellation) before any human decision. A terminal AUDIT fact: "this gate never
    /// received a decision because its batch ended." Distinct from <see cref="Rejected"/>
    /// (a deliberate human no) and from <see cref="Interrupted"/> (a crash-orphan reaped at startup).
    /// </summary>
    Cancelled = 4,

    /// <summary>
    /// Reaped at startup: the owning batch is dead/orphaned (no in-memory awaiter survived the
    /// restart) and the gate was still <c>Pending</c> past the orphan grace window. Written by the
    /// <c>OrphanedExecutionReaper</c>'s gate sweep, NOT by a human or the resolution path.
    /// </summary>
    Interrupted = 5,

    /// <summary>
    /// Reserved/legacy value. Nothing produces it anymore: the operator dismiss action was removed
    /// (redundant with <see cref="Rejected"/> — both terminate the run at the gate). Kept as a
    /// persisted enum value so existing stored records that carry it still read back; a status
    /// renderer treats it as a terminal failure, the same as <see cref="Rejected"/>.
    /// </summary>
    Dismissed = 6,
}
