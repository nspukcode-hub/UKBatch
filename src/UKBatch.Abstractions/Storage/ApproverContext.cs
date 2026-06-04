namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Structured caller identity passed to <see cref="IApprovalGateService.ApproveAsync"/> and
/// <see cref="IApprovalGateService.RejectAsync"/>. The audit trail records both the identity and
/// the role(s) under which the decision was authorized.
/// </summary>
public sealed record class ApproverContext
{
    /// <summary>Stable principal identifier (email, sub claim, or user id).</summary>
    public required string Identity { get; init; }

    /// <summary>Roles the caller holds at the time of the decision; used for audit and the per-role pending feed.</summary>
    public required IReadOnlyList<string> Roles { get; init; }
}
