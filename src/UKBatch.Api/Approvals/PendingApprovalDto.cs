using UKBatch.Abstractions.Models;

namespace UKBatch.Api.Approvals;

/// <summary>
/// Wire DTO for <see cref="PendingApproval"/>. <see cref="Config"/> reuses the Abstractions
/// <c>ApprovalGateConfig</c> directly (no DTO mirror).
/// </summary>
public sealed record class PendingApprovalDto
{
    /// <summary>Approval gate id (UUIDv7).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Batch run id this gate belongs to.</summary>
    public required string BatchId { get; init; }

    /// <summary>Step id within the batch that the gate guards.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Display name of the batch (resolved at registration time).</summary>
    public required string BatchName { get; init; }

    /// <summary>Gate config — title, allowed roles, timeout, onTimeout (Abstractions type used directly).</summary>
    public required UKBatch.Abstractions.Batches.ApprovalGateConfig Config { get; init; }

    /// <summary>UTC instant the gate became pending.</summary>
    public required DateTimeOffset PendingSinceUtc { get; init; }

    /// <summary>Deadline UTC; <c>null</c> when the gate has no timeout.</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>Maps from a <see cref="PendingApproval"/> to the wire DTO.</summary>
    public static PendingApprovalDto FromModel(PendingApproval p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new PendingApprovalDto
        {
            ApprovalId = p.ApprovalId,
            BatchId = p.BatchId,
            BatchStepId = p.BatchStepId,
            BatchName = p.BatchName,
            Config = p.Config,
            PendingSinceUtc = p.PendingSinceUtc,
            DeadlineUtc = p.DeadlineUtc,
        };
    }
}
