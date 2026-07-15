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
/// read-only live-status canvas's edge derivation), including the decision fan-out: a diamond → one
/// labelled edge per branch → each branch re-converging on the next step. TWO intentional divergences
/// remain:</para>
/// <list type="number">
/// <item>A <c>ParallelGroup</c> stays ONE node here (its children live INSIDE the card, edited in the
/// modal), so it never fans out and its entry/exit is always its own step id — where the read-only view
/// fans a trailing group out to its children.</item>
/// <item>A decision-level compensator hangs off the DIAMOND with a single edge, where the read-only view
/// fans it in from every branch. That view has no diamond container to anchor on; this one draws the
/// diamond, and the branch cards sit a column to its right — anchoring on them would sweep the edge back
/// leftwards across the canvas.</item>
/// </list>
/// <para>Both divergences resolve to node ids <c>BuildGraph</c> actually renders, which is the invariant
/// that matters: every endpoint emitted here must be a real card on the canvas.</para>
/// </remarks>
public static class EditorEdges
{
    /// <summary>
    /// Builds the typed visual edges: the main flow chained through each step's entry/exit nodes (a
    /// decision exits through its branches, so the next step re-converges from all of them), each
    /// decision's labelled diamond → branch fan-out, the per-step compensator links, and then the
    /// batch-level <c>OnFailure</c> chain. Returns no onFailure edge when the spine is empty (orphan
    /// compensation has no anchor).
    /// </summary>
    public static IReadOnlyList<EditorEdge> Build(
        IReadOnlyList<WizardStepDraft> steps,
        IReadOnlyList<WizardStepDraft> onFailureSteps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onFailureSteps);

        var edges = new List<EditorEdge>();

        // The node(s) the NEXT step connects FROM. Empty until the first step resolves.
        IReadOnlyList<string> prevExits = [];
        foreach (var step in steps)
        {
            var (entry, exit, isDecision) = Resolve(step);

            // prev → entry for every prev × entry. After a decision that is a fan-IN (every branch
            // re-converges on the next step). The kind stays Sequential either way: the editor's
            // stylesheet paints only Sequential / Decision / OnFailure, and a re-convergence is still
            // main-flow — the only edges that earn their own colour are the branch fan-out below.
            foreach (var prev in prevExits)
            {
                foreach (var e in entry)
                {
                    edges.Add(new EditorEdge { FromStepId = prev, ToStepId = e, Kind = "Sequential" });
                }
            }

            // A decision is a VISIBLE fan-out origin: its diamond → branch edges are not a prev→entry
            // cross-product, so emit them explicitly. Each carries the branch's accent so the edge, the
            // chip inside the diamond and the branch card all read as one colour.
            if (isDecision)
            {
                for (int i = 0; i < step.DecisionBranches.Count; i++)
                {
                    var branch = step.DecisionBranches[i];
                    edges.Add(new EditorEdge
                    {
                        FromStepId = step.StepId,
                        ToStepId = branch.StepId,
                        Kind = "Decision",
                        Label = branch.SummaryLabel(),
                        BranchAccent = BranchAccents.For(branch, i),
                    });
                }
            }

            prevExits = exit;
        }

        // Per-step compensator edges: each compensator renders as its own display node (derived id =
        // parent id + fixed suffix) hanging off the step it undoes — visually distinct from the
        // batch-level failure chain, which anchors on the spine EXIT below. Reuses the OnFailure edge
        // kind so the existing dashed styling applies without a new style hook.
        foreach (var s in steps)
        {
            // Match the display-node projection: a compensator only round-trips on a Job / ParallelGroup /
            // Decision step, so only those get an edge — never a dangling edge to a node that is not drawn.
            // A decision compensates as ONE unit, so the edge leaves the diamond (see the type remarks).
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

        // Batch-level OnFailure chain: anchors on the spine's TRUE exit set (a trailing decision's branch
        // cards, otherwise the trailing step's own node), then chains node→node. The exit set collapses to
        // a single id after the first compensation step, so only the anchor can fan in.
        var anchors = prevExits;
        foreach (var f in onFailureSteps)
        {
            if (anchors.Count == 0) break;   // no spine ⇒ no anchor; orphan onFailure not drawn
            foreach (var src in anchors)
            {
                edges.Add(new EditorEdge { FromStepId = src, ToStepId = f.StepId, Kind = "OnFailure" });
            }
            anchors = [f.StepId];
        }

        return edges;
    }

    /// <summary>
    /// True when <paramref name="step"/> renders as a diamond fanning out to its own branch cards, rather
    /// than as a single spine node. Shared with the canvas placement so the cards that get PLACED are
    /// exactly the ones that get WIRED here — a disagreement would strand a card with no edges or emit an
    /// edge to a card nobody drew.
    /// </summary>
    /// <remarks>
    /// A branch-LESS decision does NOT fan out: it is reachable mid-edit (the dialog lets the operator
    /// remove the last branch; only save-time validation rejects it), and it has nothing to fan out to.
    /// </remarks>
    public static bool FansOut(WizardStepDraft step)
    {
        ArgumentNullException.ThrowIfNull(step);
        return step is { StepType: BatchStepType.Decision, DecisionBranches.Count: > 0 };
    }

    // Entry = node(s) an inbound edge terminates at; Exit = node(s) the next step departs from.
    // A decision with branches enters at its diamond and exits through every branch card. Everything else
    // — Job / ApprovalGate / ParallelGroup (ONE node; its children live inside the card) / an unknown
    // future type — is a single spine node for both.
    //
    // A branch-LESS decision threads as a single spine node rather than exiting through an empty set — an
    // empty exit set would silently strand the next step's edge, the onFailure anchor and the rest of the
    // chain.
    private static (IReadOnlyList<string> Entry, IReadOnlyList<string> Exit, bool IsDecision) Resolve(
        WizardStepDraft step)
    {
        if (FansOut(step))
        {
            return ([step.StepId], step.DecisionBranches.Select(b => b.StepId).ToList(), true);
        }
        return ([step.StepId], [step.StepId], false);
    }
}
