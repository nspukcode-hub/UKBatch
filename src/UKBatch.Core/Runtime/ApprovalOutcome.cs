namespace UKBatch.Runtime;

/// <summary>Internal enum used by <c>ApprovalGateService</c> to communicate the gate's resolution.</summary>
internal enum ApprovalOutcome
{
    /// <summary>Explicitly approved by an operator.</summary>
    Approved,

    /// <summary>Auto-approved on timeout (<c>ApprovalTimeoutAction.AutoApprove</c>).</summary>
    AutoApproved,

    /// <summary>Explicitly rejected by an operator.</summary>
    Rejected,

    /// <summary>Timed out with <c>OnTimeout = Fail</c>.</summary>
    TimedOutFail,

    /// <summary>Cancelled by the caller (host shutdown or parent batch cancellation).</summary>
    Cancelled,
}
