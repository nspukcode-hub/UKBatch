namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Join behaviour when reuniting branches of a <see cref="BatchStepType.ParallelGroup"/>.
/// </summary>
public enum ParallelJoinPolicy
{
    /// <summary>
    /// Wait for every child to complete (default fan-in). If any child reaches
    /// <see cref="Models.JobStatus.Failed"/>, the group's behaviour follows
    /// <see cref="BatchDefinition.FailurePolicy"/>.
    /// </summary>
    WaitAll = 0,

    /// <summary>
    /// Continue when the FIRST child reaches <see cref="Models.JobStatus.Completed"/>; remaining
    /// children are cancelled cooperatively. A failed child does NOT satisfy this policy — the
    /// group waits until either a child succeeds or every child has failed (then the group fails).
    /// </summary>
    WaitAny = 1,

    /// <summary>
    /// Continue when at least ⌈N/2⌉+1 children reach <see cref="Models.JobStatus.Completed"/>
    /// (strict majority; failures excluded from quorum). Remaining children are cancelled cooperatively.
    /// If enough children fail that majority becomes unreachable, the group fails.
    /// </summary>
    WaitMajority = 2,
}
