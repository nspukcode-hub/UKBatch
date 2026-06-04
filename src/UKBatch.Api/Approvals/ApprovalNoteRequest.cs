namespace UKBatch.Api.Approvals;

/// <summary>
/// Body for <c>POST /approvals/{id}/approve</c>. Approver identity + roles are derived from
/// <c>HttpContext.User</c> by the endpoint — NEVER from the request body.
/// </summary>
public sealed record class ApprovalNoteRequest
{
    /// <summary>Optional free-text audit note attached to the approval log entry.</summary>
    public string? Note { get; init; }
}
