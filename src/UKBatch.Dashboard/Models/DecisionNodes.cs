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
        return branch.Label is { Length: > 0 } label ? label : Describe(branch.When);
    }

    /// <summary>
    /// Human-readable form of a branch condition: <c>"else"</c> when there is no condition (the default
    /// branch), the presence/boolean phrasing for those operators, or <c>"key symbol value"</c> otherwise.
    /// </summary>
    public static string Describe(StepCondition? when)
    {
        if (when is null)
        {
            return "else";
        }
        return when.Operator switch
        {
            ConditionOperator.Exists => $"{when.ParameterKey} exists",
            ConditionOperator.NotExists => $"{when.ParameterKey} not set",
            ConditionOperator.IsTrue => $"{when.ParameterKey} is true",
            ConditionOperator.IsFalse => $"{when.ParameterKey} is false",
            _ => $"{when.ParameterKey} {Symbol(when.Operator)} {when.Value}",
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
