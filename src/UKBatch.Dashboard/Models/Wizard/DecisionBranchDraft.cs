using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Mutable per-branch draft for a decision step (Blazor two-way binding needs settable properties; the
/// Abstractions <see cref="DecisionBranch"/> record is <c>init</c>-only). Each branch runs one job when its
/// condition holds; a branch with a <c>null</c> <see cref="When"/> is the else/default (at most one, last).
/// Projected to <see cref="DecisionBranch"/> on submit and rehydrated on edit-load.
/// </summary>
public sealed class DecisionBranchDraft
{
    /// <summary>Stable id of this branch's job node — participates in the definition's step-id uniqueness space.</summary>
    public string StepId { get; set; } = WizardStepDraft.NewStepId();

    /// <summary>Optional short edge label; falls back to the condition text when blank.</summary>
    public string? Label { get; set; }

    /// <summary>Run this branch when the condition holds; <c>null</c> = the else/default branch.</summary>
    public ConditionDraft? When { get; set; }

    /// <summary>Logical job name this branch dispatches (required).</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Cross-service target; <c>null</c>/empty = local execution.</summary>
    public string? TargetService { get; set; }

    /// <summary>Static parameters as string key/values (same shape as a job step's parameters).</summary>
    public List<KeyValuePair<string, string>> Parameters { get; set; } = new();

    /// <summary>Per-branch retry budget; <c>null</c> = inherit.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Wall-clock timeout in seconds; <c>0</c> = no timeout, <c>null</c> = inherit.</summary>
    public int? TimeoutSeconds { get; set; }

    /// <summary>
    /// A compact display of this branch's routing condition — the explicit <see cref="Label"/> when set,
    /// otherwise <c>"else"</c> for the default branch or a <c>"key symbol value"</c> summary.
    /// </summary>
    public string SummaryLabel()
    {
        if (Label is { Length: > 0 } label)
        {
            return label;
        }
        if (When is not { } c || string.IsNullOrWhiteSpace(c.ParameterKey))
        {
            return "else";
        }
        return c.Operator switch
        {
            ConditionOperator.Exists => $"{c.ParameterKey} exists",
            ConditionOperator.NotExists => $"{c.ParameterKey} not set",
            ConditionOperator.IsTrue => $"{c.ParameterKey} is true",
            ConditionOperator.IsFalse => $"{c.ParameterKey} is false",
            _ => $"{c.ParameterKey} {Symbol(c.Operator)} {c.Value}",
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
