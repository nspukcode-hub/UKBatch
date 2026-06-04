namespace UKBatch.Api.Approvals;

/// <summary>
/// Body for <c>POST /approvals/{id}/reject</c>. Approver identity + roles are derived from
/// <c>HttpContext.User</c> by the endpoint — NEVER from the request body.
/// </summary>
public sealed record class ApprovalReasonRequest
{
    /// <summary>Mandatory non-empty reason; audited and surfaced in UI. Endpoint validates non-empty BEFORE Core.</summary>
    public required string Reason { get; init; }
}
