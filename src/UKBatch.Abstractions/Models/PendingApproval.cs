using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Models;

/// <summary>
/// Snapshot of an approval gate awaiting action. Returned by
/// <see cref="Storage.IApprovalGateService.ListPendingAsync"/>.
/// </summary>
public sealed record class PendingApproval
{
    /// <summary>Unique approval id (action endpoint key).</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Parent batch run id.</summary>
    public required string BatchId { get; init; }

    /// <summary>Parent batch step id.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Logical batch name for dashboard display.</summary>
    public required string BatchName { get; init; }

    /// <summary>Configuration of the gate as registered in the definition.</summary>
    public required ApprovalGateConfig Config { get; init; }

    /// <summary>UTC time the batch entered <see cref="JobStatus.AwaitingApproval"/>.</summary>
    public required DateTimeOffset PendingSinceUtc { get; init; }

    /// <summary>
    /// UTC deadline computed from <see cref="ApprovalGateConfig.TimeoutAfter"/> at the time the gate
    /// entered <see cref="JobStatus.AwaitingApproval"/>; <c>null</c> if no timeout.
    /// </summary>
    public DateTimeOffset? DeadlineUtc { get; init; }
}
