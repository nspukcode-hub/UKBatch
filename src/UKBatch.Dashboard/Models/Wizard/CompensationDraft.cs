namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Mutable per-step compensator draft for the wizard/editor (Blazor two-way binding needs settable
/// properties; the Abstractions <see cref="UKBatch.Abstractions.Batches.CompensationStepData"/> record is
/// <c>init</c>-only). Attached to a top-level Job or ParallelGroup <see cref="WizardStepDraft"/> and
/// projected to <see cref="UKBatch.Abstractions.Batches.CompensationStepData"/> on submit, rehydrated on
/// edit-load. A compensator runs to undo its step when a later step fails and the batch policy is Compensate.
/// </summary>
public sealed class CompensationDraft
{
    /// <summary>Logical job name to dispatch as the compensator (required).</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Cross-service target for the compensator; <c>null</c>/empty = local execution.</summary>
    public string? TargetService { get; set; }

    /// <summary>Static parameters as string key/values (same shape as the step's own parameters).</summary>
    public List<KeyValuePair<string, string>> Parameters { get; set; } = new();

    /// <summary>Per-compensator retry budget; <c>null</c> = inherit the job/runtime default.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Wall-clock timeout in seconds; <c>0</c> = no timeout, <c>null</c> = inherit.</summary>
    public int? TimeoutSeconds { get; set; }
}
