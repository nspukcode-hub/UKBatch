using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# structural topology derivation for the read-only status canvas.
/// Walks the ordered <see cref="BatchStep"/> list — tracking each step's entry/exit nodes — and emits
/// real <c>from→to</c> (StepId) edges: fan-out, fan-in collapse, consecutive-PG cross-product, and the
/// OnFailure origin. Both endpoints of every emitted edge are ALWAYS real rendered node StepIds.
/// </summary>
/// <remarks>
/// <para>Replaces an earlier "scan the Sequential <c>DagLayoutEdge</c> whose
/// <c>FromStepId==groupStepId</c>" approach, which silently broke on (i) consecutive
/// ParallelGroups (PG1's successor edges are PG2's <i>fan-out</i> — the Sequential scan found nothing,
/// children dangled) and (ii) onFailure-after-trailing-ParallelGroup (the group's own non-rendered step
/// id became a dangling edge origin).</para>
/// <para>Drawflow connections require a real source AND target node id, so every edge here resolves to
/// a <c>stepId→dfId</c> pair at <c>buildGraph</c> time. No phantom Drawflow nodes.</para>
/// </remarks>
public static class DagStatusEdges
{
    /// <summary>
    /// Derives the structural edge set for <paramref name="steps"/> + <paramref name="onFailureSteps"/>.
    /// </summary>
    /// <param name="steps">Ordered top-level main-flow steps (== <c>DagStatusCanvas.Steps</c>).</param>
    /// <param name="onFailureSteps">OnFailure compensation steps (dashed side-branch).</param>
    /// <param name="layout">
    /// The computed layout — consulted ONLY to confirm which ParallelGroup children actually rendered
    /// (forward-compat with unknown child types); the topology itself comes from <paramref name="steps"/>.
    /// </param>
    public static IReadOnlyList<StatusEdge> Build(
        IReadOnlyList<BatchStep> steps,
        IReadOnlyList<BatchStep> onFailureSteps,
        DagLayout layout)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onFailureSteps);
        ArgumentNullException.ThrowIfNull(layout);

        // A node "rendered" iff DagLayout placed it (forward-compat: a future PG child kind the layout
        // chose to skip must not produce a dangling edge to a non-existent node).
        var renderedStepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var n in layout.Nodes) renderedStepIds.Add(n.StepId);

        var edges = new List<StatusEdge>();

        // prevExitStepIds = the node(s) the NEXT step connects FROM. Empty at start ⇒ a leading
        // ParallelGroup's children have no inbound (case c).
        IReadOnlyList<string> prevExitStepIds = [];
        bool prevWasParallelGroup = false;

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            var (entryNodes, exitNodes, isParallelGroup) = Resolve(step, renderedStepIds);

            // Emit prev → entry for every prev × entry. A fan-out (this step is a PG) keys status off the
            // destination/child; a fan-in (the PREVIOUS step was a PG) keys off the source/child.
            bool isFanOut = isParallelGroup;
            bool isFanIn = prevWasParallelGroup;
            string kind = (isFanOut || isFanIn) ? "Parallel" : "Sequential";
            foreach (var prev in prevExitStepIds)
            {
                foreach (var entry in entryNodes)
                {
                    edges.Add(new StatusEdge
                    {
                        FromStepId = prev,
                        ToStepId = entry,
                        Kind = kind,
                        IsFanIn = isFanIn,
                    });
                }
            }

            prevExitStepIds = exitNodes;
            prevWasParallelGroup = isParallelGroup;
        }

        // ── OnFailure chain: originates from the spine's TRUE exit nodes (children if the spine ends in
        //    a ParallelGroup — case e), then chains node→node down the compensation branch. ──
        var ordered = onFailureSteps.OrderBy(s => s.Order).ToList();
        string? prevFailureStepId = null;
        for (int i = 0; i < ordered.Count; i++)
        {
            var f = ordered[i];
            if (!renderedStepIds.Contains(f.StepId)) continue;
            if (i == 0 || prevFailureStepId is null)
            {
                foreach (var spineExit in prevExitStepIds)
                {
                    edges.Add(new StatusEdge
                    {
                        FromStepId = spineExit,
                        ToStepId = f.StepId,
                        Kind = "OnFailure",
                        IsFanIn = false,
                    });
                }
            }
            else
            {
                edges.Add(new StatusEdge
                {
                    FromStepId = prevFailureStepId,
                    ToStepId = f.StepId,
                    Kind = "OnFailure",
                    IsFanIn = false,
                });
            }
            prevFailureStepId = f.StepId;
        }

        return edges;
    }

    // Entry = node(s) an inbound edge terminates at; Exit = node(s) the next step departs from.
    // Job / Approval / Unknown → [stepId] for both. ParallelGroup → [renderedChild1…N] for both.
    private static (IReadOnlyList<string> Entry, IReadOnlyList<string> Exit, bool IsParallelGroup) Resolve(
        BatchStep step,
        HashSet<string> renderedStepIds)
    {
        if (step is { StepType: BatchStepType.ParallelGroup, ParallelGroup: { } pg })
        {
            var children = pg.Steps
                .OrderBy(c => c.Order)
                .Select(c => c.StepId)
                .Where(renderedStepIds.Contains)
                .ToList();
            return (children, children, true);
        }

        // Job / ApprovalGate / Unknown (and a malformed PG with null payload) — single spine node.
        var one = renderedStepIds.Contains(step.StepId) ? (IReadOnlyList<string>)[step.StepId] : [];
        return (one, one, false);
    }
}
