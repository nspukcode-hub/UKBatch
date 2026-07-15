using System.Globalization;

namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// One node sent from C# to <c>dag-editor.js</c> (placed via <c>addNode</c> / <c>importGraph</c>).
/// Serialized to the JS object
/// <c>{ stepId, kind, title, subtitle, orderBadge, targetService, children, branches, isOnFailure,
/// isDeleteProtected, branchAccent, x, y }</c>.
/// </summary>
/// <remarks>
/// <para><see cref="X"/>/<see cref="Y"/> cross the boundary as <see cref="double"/> via JSON, which is
/// culture-invariant — there is no C#-side coordinate-to-string emission here (Drawflow owns the SVG
/// DOM). <see cref="OrderBadge"/> is the 1-based execution position as a string, formatted with
/// <see cref="CultureInfo.InvariantCulture"/> at the producer.</para>
/// </remarks>
public sealed record class DagNodeSpec
{
    /// <summary>Source <c>WizardStepDraft.StepId</c> — the C#↔JS identity (also the live-status join key in read-only views).</summary>
    public required string StepId { get; init; }

    /// <summary>
    /// <c>BatchStepType</c> name: <c>"Job"</c> | <c>"ParallelGroup"</c> | <c>"ApprovalGate"</c> |
    /// <c>"Decision"</c>. Display nodes that are not steps of their own (a compensator, a decision branch)
    /// carry <c>"Job"</c> — the card they project IS a job — and mark themselves via
    /// <see cref="IsOnFailure"/> / <see cref="IsDeleteProtected"/> / <see cref="BranchAccent"/>.
    /// </summary>
    public required string Kind { get; init; }

    /// <summary>Primary node label (job name / approval title).</summary>
    public required string Title { get; init; }

    /// <summary>Secondary label (join policy / role summary), or <c>null</c>.</summary>
    public string? Subtitle { get; init; }

    /// <summary>1-based execution position rendered as a badge (e.g. <c>"1"</c>). InvariantCulture at the producer.</summary>
    public required string OrderBadge { get; init; }

    /// <summary>Cross-service target (Job nodes only); <c>null</c> ⇒ local execution (cloud badge omitted).</summary>
    public string? TargetService { get; init; }

    /// <summary>
    /// Child-job DISPLAY labels for a <c>ParallelGroup</c> node, rendered as parallel branch chips
    /// INSIDE the node (so the operator sees the contents without opening the modal). <c>null</c> for
    /// non-group nodes. Display-only: the editable model is <c>WizardStepDraft.Children</c>; this is a
    /// projection. Serialized to JS as <c>children</c> (an array of strings).
    /// </summary>
    public IReadOnlyList<string>? Children { get; init; }

    /// <summary>
    /// Branch chips for a <c>Decision</c> node, rendered INSIDE the diamond: each carries its condition in
    /// full plus the colour of the edge leaving to its branch card. <c>null</c> for every other kind.
    /// Display-only (the editable model is <c>WizardStepDraft.DecisionBranches</c>). Serialized to JS as
    /// <c>branches</c>.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="Children"/> (a <c>ParallelGroup</c>'s plain job-name strings) because a
    /// branch chip needs a colour to pair with its edge — a group's children have no such pairing.
    /// </remarks>
    public IReadOnlyList<BranchChipSpec>? Branches { get; init; }

    /// <summary>
    /// True ⇒ this node is a compensation (onFailure) node. <c>Kind</c> stays <c>"Job"</c> (so the modal
    /// renders the Job-only editor) while <c>dag-editor.js</c> appends the <c>dag-ed-node--failure</c>
    /// modifier (red/dashed accent). Serialized to JS as <c>isOnFailure</c>.
    /// </summary>
    public bool IsOnFailure { get; init; }

    /// <summary>
    /// True ⇒ the canvas must NOT delete this node; it is removed by editing its parent. Set on decision
    /// BRANCH nodes: a branch has no entry in <c>Steps</c> to remove, so a canvas delete would leave the
    /// model and the canvas disagreeing — branches are added/removed in the decision's dialog, which also
    /// keeps the else-branch rules intact. <c>dag-editor.js</c> drops such a node's Delete affordances and
    /// refuses a delete for it. Serialized to JS as <c>isDeleteProtected</c>.
    /// </summary>
    /// <remarks>
    /// A compensator node is NOT delete-protected even though it also projects a parent's field: deleting
    /// it has one unambiguous meaning (clear the parent's <c>Compensation</c>), which the canvas does. The
    /// flag is about a node's DELETE contract, not about being a projection.
    /// </remarks>
    public bool IsDeleteProtected { get; init; }

    /// <summary>
    /// Colour key for a decision BRANCH node's accent, pairing the card with its chip inside the diamond
    /// and the edge between them — a palette slot, or <see cref="BranchAccents.Else"/> for the default
    /// branch. <c>null</c> on every other node. Serialized to JS as <c>branchAccent</c>.
    /// </summary>
    public string? BranchAccent { get; init; }

    /// <summary>Node left coordinate in Drawflow canvas space.</summary>
    public required double X { get; init; }

    /// <summary>Node top coordinate in Drawflow canvas space.</summary>
    public required double Y { get; init; }
}
