using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Service for inspecting and acting on pending approval gates. Implementations enforce role-based
/// authorization (against <see cref="Batches.ApprovalGateConfig.AllowedRoles"/>) on top of the
/// dashboard's caller identity.
/// </summary>
public interface IApprovalGateService
{
    /// <summary>
    /// Lists pending approvals, optionally filtered to a role the caller holds. <paramref name="userRole"/>
    /// is <c>null</c> for an unfiltered admin view.
    /// </summary>
    Task<IReadOnlyList<PendingApproval>> ListPendingAsync(string? userRole, CancellationToken cancellationToken);

    /// <summary>
    /// Approves the gate. Throws <see cref="InvalidOperationException"/> if the gate is not pending
    /// or the caller lacks authorization.
    /// </summary>
    Task ApproveAsync(string approvalId, ApproverContext approver, string? note, CancellationToken cancellationToken);

    /// <summary>
    /// Rejects the gate; the parent batch transitions to <see cref="JobStatus.Failed"/>.
    /// <paramref name="reason"/> is required for audit.
    /// </summary>
    Task RejectAsync(string approvalId, ApproverContext approver, string reason, CancellationToken cancellationToken);
}
