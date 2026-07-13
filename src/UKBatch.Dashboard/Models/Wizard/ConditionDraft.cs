using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Mutable mirror of <see cref="StepCondition"/> for the wizard and visual editor (Blazor two-way binding
/// needs settable properties; the Abstractions record is <c>init</c>-only). Projected to
/// <see cref="StepCondition"/> on submit and rehydrated from a fetched step on edit-load.
/// </summary>
public sealed class ConditionDraft
{
    /// <summary>The parameter or forwarded-output key the condition tests.</summary>
    public string ParameterKey { get; set; } = string.Empty;

    /// <summary>How the key's value is compared to <see cref="Value"/>.</summary>
    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;

    /// <summary>The comparand (blank for the presence/boolean operators).</summary>
    public string Value { get; set; } = string.Empty;
}
