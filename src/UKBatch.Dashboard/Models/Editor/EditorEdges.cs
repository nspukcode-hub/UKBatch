using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models.Wizard;

namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// Pure-C# topology helper for the visual batch editor: derives the typed visual edge set
/// (<see cref="EditorEdge"/>) from a model's main-flow steps + onFailure (compensation) steps.
/// Unit-testable, no Blazor.
/// </summary>
/// <remarks>
/// <para>CROSS-REFERENCE — mirrors <c>UKBatch.Dashboard.Models.DagStatus.DagStatusEdges.Build</c> (the
/// read-only live-status canvas's edge derivation) but with ONE intentional divergence: the editor
/// renders a <c>ParallelGroup</c> AND a <c>Decision</c> as a SINGLE node (their children / branches live
/// INSIDE the card, edited in the modal), so there is no child/branch fan-out/fan-in cross-product. The
/// spine is a simple node chain and the onFailure branch anchors on the single TRAILING top-level node id
/// — never a child/branch set. When/if the editor ever expands those children/branches on-canvas, this
/// divergence must be revisited (and <c>DagStatusEdges</c>'s fan-out logic ported in). The read-only
/// <c>DagStatusEdges</c> carries the reciprocal pointer.</para>
/// </remarks>
public static class EditorEdges
{
    /// <summary>
    /// Builds the typed visual edges: consecutive <c>Sequential</c> edges along the main flow, then the
    /// <c>OnFailure</c> branch (spine-exit → first compensation step, then a compensation chain). Returns
    /// no onFailure edge when the spine is empty (orphan compensation has no anchor).
    /// </summary>
    public static IReadOnlyList<EditorEdge> Build(
        IReadOnlyList<WizardStepDraft> steps,
        IReadOnlyList<WizardStepDraft> onFailureSteps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onFailureSteps);

        var edges = new List<EditorEdge>();
        for (int i = 0; i + 1 < steps.Count; i++)
        {
            edges.Add(new EditorEdge
            {
                FromStepId = steps[i].StepId,
                ToStepId = steps[i + 1].StepId,
                Kind = "Sequential",
            });
        }

        // Per-step compensator edges: each compensator renders as its own display node (derived id =
        // parent id + fixed suffix) hanging off the step it undoes — visually distinct from the
        // batch-level failure chain, which anchors on the spine EXIT below. Reuses the OnFailure edge
        // kind so the existing dashed styling applies without a new style hook.
        foreach (var s in steps)
        {
            // Match the display-node projection: a compensator only round-trips on a Job / ParallelGroup /
            // Decision step, so only those get an edge — never a dangling edge to a node that is not drawn.
            if (s is { Compensation: not null, StepType: BatchStepType.Job or BatchStepType.ParallelGroup or BatchStepType.Decision })
            {
                edges.Add(new EditorEdge
                {
                    FromStepId = s.StepId,
                    ToStepId = CompensationStepIds.For(s.StepId),
                    Kind = "OnFailure",
                });
            }
        }

        // Spine exit = the LAST top-level step's node id. The editor renders a ParallelGroup as ONE node
        // (its children live inside the card), so — UNLIKE the read-only DagStatusEdges (which fans a
        // trailing PG out to its children) — the exit is always a single node id. This is the one
        // intentional divergence (see the type remarks + the DagStatusEdges cross-reference).
        string? prev = steps.Count > 0 ? steps[^1].StepId : null;
        foreach (var f in onFailureSteps)
        {
            if (prev is null) break;   // no spine ⇒ no anchor; orphan onFailure not drawn
            edges.Add(new EditorEdge { FromStepId = prev, ToStepId = f.StepId, Kind = "OnFailure" });
            prev = f.StepId;
        }
        return edges;
    }
}
