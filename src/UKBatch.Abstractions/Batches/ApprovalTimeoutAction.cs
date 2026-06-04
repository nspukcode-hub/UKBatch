namespace UKBatch.Abstractions.Batches;

/// <summary>Behaviour when an approval gate times out.</summary>
public enum ApprovalTimeoutAction
{
    /// <summary>Fail the batch with <see cref="Models.JobStatus.Failed"/>.</summary>
    Fail = 0,

    /// <summary>Continue execution as if approved.</summary>
    AutoApprove = 1,

    /// <summary>
    /// Keep the gate open and the batch in <see cref="Models.JobStatus.AwaitingApproval"/> state
    /// past the timeout (essentially no-op on timeout; an operator must intervene).
    /// </summary>
    Hold = 2,
}
