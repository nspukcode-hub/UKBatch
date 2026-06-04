using UKBatch.Api.Approvals;

namespace UKBatch.Dashboard.Models;

/// <summary>View model adapter from <see cref="PendingApprovalDto"/> for the Approvals queue row.</summary>
public sealed record class ApprovalRowViewModel
{
    /// <summary>Approval gate id.</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Parent batch run id.</summary>
    public required string BatchId { get; init; }

    /// <summary>Parent batch step id.</summary>
    public required string BatchStepId { get; init; }

    /// <summary>Display name of the batch.</summary>
    public required string BatchName { get; init; }

    /// <summary>Gate title from configuration.</summary>
    public required string Title { get; init; }

    /// <summary>Roles allowed to approve; empty list means nobody can approve (fail-safe).</summary>
    public required IReadOnlyList<string> AllowedRoles { get; init; }

    /// <summary>UTC instant the gate became pending.</summary>
    public required DateTimeOffset PendingSinceUtc { get; init; }

    /// <summary>Deadline UTC; <c>null</c> when the gate has no timeout.</summary>
    public DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>Time remaining until <see cref="DeadlineUtc"/>; <c>null</c> if no deadline or already overdue.</summary>
    public TimeSpan? RemainingTime(DateTimeOffset nowUtc)
        => DeadlineUtc is { } dl && dl > nowUtc ? dl - nowUtc : null;

    /// <summary><c>true</c> when the deadline has passed.</summary>
    public bool IsOverdue(DateTimeOffset nowUtc)
        => DeadlineUtc is { } dl && dl <= nowUtc;

    /// <summary>Maps from a wire <see cref="PendingApprovalDto"/> to the view model.</summary>
    public static ApprovalRowViewModel FromDto(PendingApprovalDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new ApprovalRowViewModel
        {
            ApprovalId = dto.ApprovalId,
            BatchId = dto.BatchId,
            BatchStepId = dto.BatchStepId,
            BatchName = dto.BatchName,
            Title = dto.Config.Title,
            AllowedRoles = dto.Config.AllowedRoles,
            PendingSinceUtc = dto.PendingSinceUtc,
            DeadlineUtc = dto.DeadlineUtc,
        };
    }
}
