using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// Shared projections for decision steps: the edge/branch label text and the runnable-step view of a
/// branch. A decision routes to one of several branch jobs; each branch renders as its own node keyed by
/// its <see cref="DecisionBranch.StepId"/> (== the <c>JobExecution.BatchStepId</c> the winner produces),
/// and the diamond→branch edge is labelled with the branch's condition.
/// </summary>
public static class DecisionNodes
{
    /// <summary>
    /// The label for a branch edge: the branch's explicit <see cref="DecisionBranch.Label"/> when set,
    /// otherwise the condition text (e.g. <c>"amount &gt; 1000"</c>, or <c>"else"</c> for the default branch).
    /// </summary>
    public static string BranchLabel(DecisionBranch branch)
    {
        ArgumentNullException.ThrowIfNull(branch);
        return BranchLabel(branch.Label, branch.When);
    }

    /// <summary>
    /// The label for a branch from its raw parts — the shared rule behind both the saved-branch label and
    /// the in-progress draft's summary, so an authoring chip and the label the branch renders with once
    /// saved cannot drift. A blank/whitespace label counts as "no label" and a blank condition key as "no
    /// condition", matching the draft→branch projection (which drops both) — otherwise a whitespace-only
    /// label would read as an empty chip in one view and as the condition text in the other.
    /// </summary>
    public static string BranchLabel(string? label, StepCondition? when)
        => string.IsNullOrWhiteSpace(label) ? Describe(when) : label.Trim();

    /// <summary>
    /// Human-readable form of a branch condition: <c>"else"</c> when there is no condition (the default
    /// branch), the presence/boolean phrasing for those operators, or <c>"key symbol value"</c> otherwise.
    /// </summary>
    public static string Describe(StepCondition? when)
        => when is null ? "else" : Describe(when.ParameterKey, when.Operator, when.Value);

    /// <summary>
    /// Human-readable form of a condition from its raw parts, for callers holding a mutable draft rather
    /// than a projected <see cref="StepCondition"/>. A blank/whitespace <paramref name="parameterKey"/>
    /// yields <c>"else"</c> — the projection treats it as "no condition", so describing it any other way
    /// would show the operator a condition that will not be saved.
    /// </summary>
    public static string Describe(string? parameterKey, ConditionOperator op, string? value)
    {
        if (string.IsNullOrWhiteSpace(parameterKey))
        {
            return "else";
        }
        var key = parameterKey.Trim();
        return op switch
        {
            ConditionOperator.Exists => $"{key} exists",
            ConditionOperator.NotExists => $"{key} not set",
            ConditionOperator.IsTrue => $"{key} is true",
            ConditionOperator.IsFalse => $"{key} is false",
            _ => $"{key} {Symbol(op)} {value}",
        };
    }

    /// <summary>
    /// Projects a branch to the runnable Job step it dispatches — keyed by the branch id and carrying its
    /// condition — so a click on a branch node opens the inspector on the branch's job (matching the runtime
    /// synthesis in the executor).
    /// </summary>
    public static BatchStep BranchAsStep(BatchStep decision, DecisionBranch branch)
    {
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(branch);
        return new BatchStep
        {
            StepId = branch.StepId,
            Order = decision.Order,
            StepType = BatchStepType.Job,
            Job = branch.Job,
            Condition = branch.When,
        };
    }

    private static string Symbol(ConditionOperator op) => op switch
    {
        ConditionOperator.Equals => "=",
        ConditionOperator.NotEquals => "≠",
        ConditionOperator.GreaterThan => ">",
        ConditionOperator.GreaterThanOrEqual => "≥",
        ConditionOperator.LessThan => "<",
        ConditionOperator.LessThanOrEqual => "≤",
        ConditionOperator.Contains => "contains",
        _ => op.ToString(),
    };
}
