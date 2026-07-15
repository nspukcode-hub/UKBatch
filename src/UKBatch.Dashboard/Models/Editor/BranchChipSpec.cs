namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// One decision-branch chip rendered INSIDE the diamond card, carrying the branch's full condition text
/// plus the colour that pairs it with the edge leaving the diamond and the branch card it lands on.
/// Serialized to JS as <c>{ label, accent }</c>.
/// </summary>
/// <remarks>
/// Display-only: the editable model is <c>WizardStepDraft.DecisionBranches</c> (edited in the step
/// dialog); this is a projection. The condition text lives HERE rather than on the edge because an edge
/// label truncates a long condition into noise.
/// </remarks>
public sealed record class BranchChipSpec
{
    /// <summary>The branch's routing condition in full (e.g. <c>"amount &gt; 1000"</c>, or <c>"else"</c>).</summary>
    public required string Label { get; init; }

    /// <summary>Colour key — a palette slot, or <see cref="BranchAccents.Else"/> for the default branch.</summary>
    public required string Accent { get; init; }
}
