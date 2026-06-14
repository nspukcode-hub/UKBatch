using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Api.Approvals;

/// <summary>
/// Wire DTO for <see cref="ApprovalGateView"/>: a gate's identity plus its decided outcome for one
/// batch run. Lets a dashboard colour a gate node from its own recorded decision (the gate has no
/// <c>JobExecution</c> row, so its outcome is invisible to row-based status roll-ups). The
/// <see cref="Status"/> and <see cref="Outcome"/> enums reuse the Abstractions types directly and
/// string-serialize via the API's configured <c>JsonStringEnumConverter</c>.
/// </summary>
public sealed record class ApprovalGateViewDto
{
    /// <summary>Approval gate id (UUIDv7).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Batch run id this gate belongs to.</summary>
    public required string BatchId { get; init; }

    /// <summary>Step id within the batch that the gate guards.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Lifecycle status of the gate record (pending vs decided).</summary>
    public required ApprovalRecordStatus Status { get; init; }

    /// <summary>Terminal decision once <see cref="Status"/> is decided; <c>null</c> while pending.</summary>
    public ApprovalRecordOutcome? Outcome { get; init; }

    /// <summary>Maps from an <see cref="ApprovalGateView"/> to the wire DTO.</summary>
    public static ApprovalGateViewDto FromModel(ApprovalGateView g)
    {
        ArgumentNullException.ThrowIfNull(g);
        return new ApprovalGateViewDto
        {
            ApprovalId = g.ApprovalId,
            BatchId = g.BatchId,
            BatchStepId = g.BatchStepId,
            Status = g.Status,
            Outcome = g.Outcome,
        };
    }
}
