namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// One typed visual edge sent from C# to <c>dag-editor.js</c>'s <c>syncConnections(edges)</c>. Mirrors
/// the read-only canvas's <c>buildGraph</c> edge shape (<c>{ fromStepId, toStepId, kind }</c>) so the
/// editor and the read-only status canvas share ONE typed-edge mental model. Edges are presentation-only
/// (the C# <c>BatchWizardModel.Steps</c>/<c>OnFailureSteps</c> lists are the source of truth) — the
/// operator can neither draw nor delete them; they are derived from the model on every structural change.
/// </summary>
public sealed record class EditorEdge
{
    /// <summary>Source node's <c>WizardStepDraft.StepId</c>.</summary>
    public required string FromStepId { get; init; }

    /// <summary>Target node's <c>WizardStepDraft.StepId</c>.</summary>
    public required string ToStepId { get; init; }

    /// <summary>Edge kind: <c>"Sequential"</c> (main flow) | <c>"OnFailure"</c> (red-dashed compensation branch).</summary>
    public required string Kind { get; init; }
}
