namespace UKBatch.Abstractions.Batches;

/// <summary>
/// An optional run-if guard on a <see cref="BatchStep"/>: the step runs only when the condition holds at
/// dispatch time; otherwise it is skipped and the batch proceeds to the next step. The condition tests the
/// value at <see cref="ParameterKey"/> against the data a step would receive when dispatched — the batch's
/// initial/trigger parameters merged with the outputs forwarded by earlier steps (later wins). So a
/// condition can branch on an earlier step's output, e.g. "ship only when the invoice amount exceeds 1000".
/// </summary>
/// <remarks>
/// <para>Honored on a top-level <see cref="BatchStepType.Job"/>, <see cref="BatchStepType.ParallelGroup"/>
/// (the whole group is skipped as one unit), or <see cref="BatchStepType.ApprovalGate"/> step. Forbidden on
/// parallel-group CHILDREN and OnFailure (compensation-chain) steps — the validator rejects those. The
/// code-first fluent builder exposes <c>RunIf</c> on Job and ParallelGroup steps; a condition on an
/// ApprovalGate is set via REST or the dashboard.</para>
/// <para>A skipped step is recorded as <see cref="Models.JobStatus.Skipped"/> so it is visible in history,
/// advances the resume cursor, produces no forwarded output, and is never compensated during a saga
/// unwind.</para>
/// <para>Modelled as its own record so multi-condition (AND/OR) support can be added later as an additive,
/// non-breaking change.</para>
/// </remarks>
public sealed record class StepCondition
{
    /// <summary>The parameter or forwarded-output key whose value is tested.</summary>
    public required string ParameterKey { get; init; }

    /// <summary>How <see cref="ParameterKey"/>'s value is compared to <see cref="Value"/>.</summary>
    public required ConditionOperator Operator { get; init; }

    /// <summary>
    /// The comparand, as a culture-invariant string (JSON- and form-friendly, so the same representation is
    /// compared whether the condition was authored in code, over REST, or in the dashboard). Required for the
    /// comparison operators (<see cref="ConditionOperator.Equals"/>, <see cref="ConditionOperator.NotEquals"/>,
    /// the ordering operators, and <see cref="ConditionOperator.Contains"/>); ignored by the presence and
    /// boolean operators (<see cref="ConditionOperator.Exists"/>, <see cref="ConditionOperator.NotExists"/>,
    /// <see cref="ConditionOperator.IsTrue"/>, <see cref="ConditionOperator.IsFalse"/>).
    /// </summary>
    public string? Value { get; init; }
}
