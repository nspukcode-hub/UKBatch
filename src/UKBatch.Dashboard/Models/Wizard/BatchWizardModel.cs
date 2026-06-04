using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;

namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Mutable view model for the create/edit wizard, mirroring <see cref="BatchDefinition"/> but with
/// settable properties for Blazor two-way binding. Projects to <see cref="CreateBatchRequest"/> /
/// <see cref="UpdateBatchRequest"/> on submit and loads from a fetched <see cref="BatchDefinitionDto"/>
/// on edit (carrying <see cref="Id"/> + <see cref="Version"/> for optimistic concurrency).
/// </summary>
public sealed class BatchWizardModel
{
    /// <summary><c>null</c> in Create; set in Edit.</summary>
    public string? Id { get; set; }

    /// <summary><c>0</c> in Create; carried in Edit (optimistic concurrency token).</summary>
    public int Version { get; set; }

    /// <summary>Display name (required, unique within source).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Origin of the definition. Carried through edit so an <c>Api</c>-source batch is NOT silently
    /// rewritten to <c>Dashboard</c> on save (provenance preservation).
    /// Defaults to <see cref="BatchSource.Dashboard"/> for Create; <see cref="FromDefinition"/>
    /// captures the loaded source for Edit.
    /// </summary>
    public BatchSource Source { get; set; } = BatchSource.Dashboard;

    /// <summary>Optional cron expression; <c>null</c>/blank = trigger-only.</summary>
    public string? Schedule { get; set; }

    /// <summary>Failure policy.</summary>
    public BatchFailurePolicy FailurePolicy { get; set; } = BatchFailurePolicy.StopOnFailure;

    /// <summary>Ordered step drafts (the main flow).</summary>
    public List<WizardStepDraft> Steps { get; set; } = new();

    /// <summary>Job-only compensation drafts (used when <see cref="FailurePolicy"/> is Compensate).</summary>
    public List<WizardStepDraft> OnFailureSteps { get; set; } = new();

    /// <summary>
    /// Storage-adapter opaque metadata round-tripped through Wizard edit + save. Wizard UI
    /// does NOT surface this — it ONLY carries it so layout hints set in the interactive DAG view
    /// (Detail page) survive a subsequent Wizard edit (silent data-loss prevention).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; set; }

    /// <summary>
    /// True when the loaded definition contains a step type the wizard cannot edit (v0.2 data).
    /// The wizard blocks editing to avoid a lossy round-trip.
    /// </summary>
    public bool ContainsUnsupportedStep =>
        Steps.Any(IsUnsupportedRecursive) || OnFailureSteps.Any(s => s.IsUnsupported);

    private static bool IsUnsupportedRecursive(WizardStepDraft s)
        => s.IsUnsupported || s.Children.Any(c => c.IsUnsupported);

    /// <summary>
    /// Ensures <see cref="FailurePolicy"/> is <see cref="BatchFailurePolicy.Compensate"/> when a
    /// compensation (onFailure) step is added on the canvas. Returns <c>true</c> when the policy was
    /// flipped (so the caller can notify the operator). Idempotent — a no-op if already Compensate.
    /// Extracted from the Editor's drop handler so it is unit-testable.
    /// </summary>
    public bool EnsureCompensatePolicy()
    {
        if (FailurePolicy == BatchFailurePolicy.Compensate) return false;
        FailurePolicy = BatchFailurePolicy.Compensate;
        return true;
    }

    /// <summary>
    /// Reports whether removing the LAST compensation step has left a <see cref="BatchFailurePolicy.Compensate"/>
    /// policy with no compensation steps — the server degrades Compensate to StopOnFailure on save, so the
    /// canvas WARNS but does NOT auto-revert the operator's explicit policy. Returns <c>true</c> ⇒
    /// the caller should warn. Does NOT mutate the model (warn-don't-revert).
    /// </summary>
    public bool ShouldWarnEmptyCompensate()
        => OnFailureSteps.Count == 0 && FailurePolicy == BatchFailurePolicy.Compensate;

    /// <summary>Builds the create request (no id — the server assigns it).</summary>
    public CreateBatchRequest ToCreateRequest(string? createdBy) => new()
    {
        Name = Name.Trim(),
        Source = Source,
        Schedule = string.IsNullOrWhiteSpace(Schedule) ? null : Schedule.Trim(),
        Steps = Steps.Select((s, i) => s.ToBatchStep(i)).ToList(),
        FailurePolicy = FailurePolicy,
        OnFailureSteps = OnFailureSteps.Select((s, i) => s.ToBatchStep(i)).ToList(),
        CreatedBy = createdBy,
        // Carry Metadata so the FIRST save of a visual-editor batch persists its layout hints:
        // the Editor's SaveAsync sets _model.Metadata = Serialize(hints) before projecting,
        // and create-mode would otherwise POST null and lose every dragged position. Safe for the
        // Wizard, which never sets Metadata on create (stays null).
        Metadata = Metadata,
    };

    /// <summary>Builds the update request (carries id + version for optimistic concurrency).</summary>
    public UpdateBatchRequest ToUpdateRequest() => new()
    {
        Id = Id!,
        Name = Name.Trim(),
        Source = Source,
        Schedule = string.IsNullOrWhiteSpace(Schedule) ? null : Schedule.Trim(),
        Steps = Steps.Select((s, i) => s.ToBatchStep(i)).ToList(),
        FailurePolicy = FailurePolicy,
        OnFailureSteps = OnFailureSteps.Select((s, i) => s.ToBatchStep(i)).ToList(),
        Version = Version,
        // Round-trip Metadata so a Wizard edit doesn't clobber operator-set layout hints.
        Metadata = Metadata,
    };

    /// <summary>Projects the main step drafts into <see cref="BatchStep"/>s (for the Review-step DAG preview).</summary>
    public IReadOnlyList<BatchStep> StepsAsBatchSteps()
        => Steps.Select((s, i) => s.ToBatchStep(i)).ToList();

    /// <summary>Projects the OnFailure drafts into <see cref="BatchStep"/>s (for the Review-step DAG preview).</summary>
    public IReadOnlyList<BatchStep> OnFailureAsBatchSteps()
        => OnFailureSteps.Select((s, i) => s.ToBatchStep(i)).ToList();

    /// <summary>Edit-mode load: projects a fetched definition into the mutable VM (carries Id + Version).</summary>
    public static BatchWizardModel FromDefinition(BatchDefinitionDto def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new BatchWizardModel
        {
            Id = def.Id,
            Version = def.Version,
            Name = def.Name,
            Source = def.Source,   // preserve the loaded source (a hardcoded Dashboard would flip Api-source batches).
            Schedule = def.Schedule,
            FailurePolicy = def.FailurePolicy,
            Steps = def.Steps.OrderBy(s => s.Order).Select(WizardStepDraft.FromBatchStep).ToList(),
            OnFailureSteps = def.OnFailureSteps.OrderBy(s => s.Order).Select(WizardStepDraft.FromBatchStep).ToList(),
            // Round-trip Metadata so a Wizard edit doesn't clobber operator-set layout hints.
            Metadata = def.Metadata,
        };
    }
}
