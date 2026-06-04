namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// The full set of nodes serialized to <c>dag-editor.js</c>'s <c>importGraph(graph)</c> on first
/// render (and the shape the Editor's <c>BuildGraph()</c> produces). Drawflow connection edges are
/// NEVER persisted — execution order is the C# <c>BatchWizardModel.Steps</c> list, not node geometry.
/// </summary>
public sealed record class DagGraphSpec
{
    /// <summary>Nodes to place, in execution order (main flow first, then the onFailure lane). Empty ⇒ an empty canvas.</summary>
    public required IReadOnlyList<DagNodeSpec> Nodes { get; init; }

    /// <summary>
    /// Typed visual edges (<see cref="EditorEdge"/>) — main flow <c>Sequential</c> + the red-dashed
    /// <c>OnFailure</c> compensation branch. Derived from the model (<see cref="EditorEdges.Build"/>),
    /// never from node geometry; the operator cannot draw or delete them.
    /// </summary>
    public IReadOnlyList<EditorEdge> Edges { get; init; } = [];
}
