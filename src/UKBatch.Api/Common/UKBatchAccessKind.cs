namespace UKBatch.Api;

/// <summary>
/// Classifies a UKBatch API endpoint for role-gating. Applied via <c>WithUKBatchAccess</c> and read
/// by <c>RequireUKBatchRoleAuthorization</c>. Inert unless a host opts in to role gating — an
/// unconfigured host sees no behavior change.
/// </summary>
public enum UKBatchAccessKind
{
    /// <summary>A safe query. Gated to the read (viewer) policy.</summary>
    Read,

    /// <summary>A mutation (trigger, create, update, delete, cancel, retry, pause, resume). Gated to the write (operator) policy.</summary>
    Write,

    /// <summary>
    /// An approval-gate decision (approve or reject). Gated only to the read (viewer) policy at the
    /// endpoint layer; the gate's own allowed-roles check is the real authority, so an approver who
    /// holds a gate role but not the operator role can still act.
    /// </summary>
    GateDecision,

    /// <summary>A machine ingest (worker heartbeat). Never gated — reached over a trusted network or gateway.</summary>
    Ingest,
}
