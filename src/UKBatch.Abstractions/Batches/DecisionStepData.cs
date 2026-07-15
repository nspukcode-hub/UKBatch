namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Payload for a <see cref="BatchStepType.Decision"/> step: routes to exactly one of several branch jobs by
/// condition. The branches are evaluated in order; the first whose <see cref="DecisionBranch.When"/> holds
/// runs, and every other branch is recorded <see cref="Models.JobStatus.Skipped"/>. A branch with a null
/// condition is the fallback (else) — at most one, and it must be last. When no branch matches and there is
/// no else, the decision passes through: every branch is skipped and the batch proceeds to the next step.
/// The decision is one execution unit — if it carries <see cref="BatchStep.Compensation"/> it compensates as
/// a whole, and its losing branches (which never ran) are never compensated.
/// </summary>
public sealed record class DecisionStepData
{
    /// <summary>
    /// Ordered branches; the first whose <see cref="DecisionBranch.When"/> holds runs, the rest are skipped.
    /// A branch with a null condition is the fallback (else) — at most one, and it must be last. Enforced by
    /// the validator (matching how ordering and comparand rules for <see cref="StepCondition"/> live there).
    /// </summary>
    public required IReadOnlyList<DecisionBranch> Branches { get; init; }
}

/// <summary>
/// One branch of a <see cref="DecisionStepData"/>: the single job to run when its condition holds. In v1 a
/// branch targets one job (local or cross-service); parallel groups and multi-step blocks are not involved.
/// </summary>
public sealed record class DecisionBranch
{
    /// <summary>
    /// Stable id of this branch's job node — the <see cref="Transport.JobMessage.BatchStepId"/> stamped on the
    /// execution row it produces (the winner runs under this id; a losing branch is recorded skipped under it).
    /// Participates in the definition's step-id uniqueness space and must not end with the reserved compensator
    /// suffix (<see cref="CompensationStepIds.Suffix"/>).
    /// </summary>
    public required string StepId { get; init; }

    /// <summary>Optional short display label for the branch edge; falls back to the condition text.</summary>
    public string? Label { get; init; }

    /// <summary>Run this branch when the condition holds; <c>null</c> = the else/default branch.</summary>
    public StepCondition? When { get; init; }

    /// <summary>The single job this branch runs (v1: job-only; local or cross-service).</summary>
    public required JobStepData Job { get; init; }
}
