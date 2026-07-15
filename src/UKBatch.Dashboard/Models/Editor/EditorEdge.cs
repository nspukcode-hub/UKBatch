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

    /// <summary>Edge kind: <c>"Sequential"</c> (main flow) | <c>"Decision"</c> (amber branch) | <c>"OnFailure"</c> (red-dashed compensation branch).</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// Optional short label for a decision <c>diamond→branch</c> edge — the branch's condition text (e.g.
    /// <c>"amount &gt; 1000"</c> or <c>"else"</c>). <c>null</c> on every other edge.
    /// </summary>
    public string? Label { get; init; }
}
