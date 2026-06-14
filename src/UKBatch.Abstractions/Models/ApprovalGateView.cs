using UKBatch.Abstractions.Storage;

namespace UKBatch.Abstractions.Models;

/// <summary>
/// Focused, read-only view of an approval gate's identity + decided outcome for ONE batch run.
/// Returned by <see cref="IApprovalGateService.ListForBatchAsync"/> so a dashboard can colour a gate
/// node from the gate's OWN recorded decision (pending / approved / rejected / dismissed / timed-out /
/// cancelled / interrupted) instead of inferring it from the batch's job-row aggregate. A gate has no
/// <c>JobExecution</c> row, so its outcome is invisible to row-based status roll-ups.
/// </summary>
/// <remarks>
/// Deliberately a SUBSET of <see cref="PersistedApprovalGate"/>: only the fields a status renderer
/// needs. The full record (config, deadline, decided-by, note) is not carried here — the actionable
/// pending feed already exposes those via <see cref="PendingApproval"/>.
/// </remarks>
public sealed record class ApprovalGateView
{
    /// <summary>Approval gate id (UUIDv7).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Batch RUN id this gate belongs to.</summary>
    public required string BatchId { get; init; }

    /// <summary>Step id within the batch that the gate guards.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Lifecycle status of the gate record (pending vs decided).</summary>
    public required ApprovalRecordStatus Status { get; init; }

    /// <summary>Terminal decision once <see cref="Status"/> is decided; <c>null</c> while pending.</summary>
    public ApprovalRecordOutcome? Outcome { get; init; }
}
