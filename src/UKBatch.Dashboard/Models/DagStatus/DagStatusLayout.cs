using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# LEFT-TO-RIGHT position layout for the read-only Drawflow status canvas.
/// Replaces <see cref="DagLayout"/>'s VERTICAL spine coordinates for the read-only views: the live
/// <c>.dag-st-node</c> cards render TALLER than <see cref="DagLayout"/>'s assumed 80px (a 2-line subtitle
/// such as "any user" pushes them to ~110-130px), so consecutive vertical-spine nodes overlapped. This
/// mirrors the batch <c>Editor.razor</c>'s clean LTR placement (each step occupies a horizontal column),
/// EXPANDED so a ParallelGroup's children render as separate nodes stacked vertically within one column.
/// </summary>
/// <remarks>
/// <para>Position-only. The <b>topology/edges are unchanged</b> — <see cref="DagStatusEdges.Build"/> is
/// structural (position-independent) and stays as-is. Node metadata (Title/Subtitle/Kind/TargetService)
/// still comes from <see cref="DagLayout.Compute(IReadOnlyList{BatchStep}, IReadOnlyList{BatchStep})"/>'s
/// node list (which already expands ParallelGroup
/// children); the canvas looks each node up by StepId and overrides ONLY its X/Y from this map — exactly
/// how <c>Editor.razor</c> keeps DagLayout for metadata but overrides X/Y.</para>
/// <para><b>Spacing rationale (overlap-free by construction for realistic 2-line cards ~230×130):</b>
/// <list type="bullet">
/// <item><see cref="ColStride"/> = 320 (230 card width + 90 gap, == editor's stride) ⇒ columns never
/// collide horizontally.</item>
/// <item><see cref="ChildStride"/> = 150 (≥ ~130 card height + gap) ⇒ vertically stacked parallel
/// siblings never overlap.</item>
/// <item><see cref="FailureLaneDy"/> = 300 is a FLOOR; the lane is additionally pushed below the
/// deepest fan-out node's bottom + a clear gap, so a wide ParallelGroup can never collide with the
/// compensation lane (robust by construction, not a fixed-offset gamble).</item>
/// </list></para>
/// <para><b>Forward-compat:</b> an unrecognised <see cref="BatchStepType"/> is treated as a single
/// spine node (one column) — never throws on an unknown type.</para>
/// </remarks>
public static class DagStatusLayout
{
    /// <summary>Card width (== <c>.dag-status-canvas .drawflow-node { width: 230px }</c>).</summary>
    internal const double NodeW = 230;

    /// <summary>Nominal card height used for vertical centring (2-line card ≈ 130px).</summary>
    internal const double NodeH = 130;

    /// <summary>Left edge of the first column.</summary>
    internal const double StartX = 40;

    /// <summary>Horizontal distance between successive columns (card width + generous gap).</summary>
    internal const double ColStride = 320;

    /// <summary>Vertical centre line of the main flow (room above for upper parallel siblings).</summary>
    internal const double MidY = 240;

    /// <summary>Vertical distance between stacked ParallelGroup siblings (≥ card height + gap).</summary>
    internal const double ChildStride = 150;

    /// <summary>Lower-lane floor for the reverse-unwind compensators (sits below the spine, above the chain).</summary>
    internal const double CompensationLaneDy = 150;

    /// <summary>Lower lane offset for the OnFailure compensation chain (clears the fan-out).</summary>
    internal const double FailureLaneDy = 300;

    /// <summary>
    /// Computes a <c>StepId → (X, Y)</c> top-left position map for the canvas. Every rendered node — a
    /// top-level Job/Approval/Unknown, each expanded ParallelGroup child, and each OnFailure step — gets
    /// one entry. The ParallelGroup container step itself has NO entry (it renders no node; its children
    /// do). Lookup is by <see cref="BatchStep.StepId"/>; a node absent from the map keeps its source
    /// coordinate (defensive — never happens for well-formed input).
    /// </summary>
    /// <param name="steps">Ordered top-level main-flow steps (== <c>DagStatusCanvas.Steps</c>).</param>
    /// <param name="onFailureSteps">OnFailure compensation steps (lower lane, left→right).</param>
    public static IReadOnlyDictionary<string, (double X, double Y)> Compute(
        IReadOnlyList<BatchStep> steps,
        IReadOnlyList<BatchStep> onFailureSteps)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onFailureSteps);

        var map = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);

        // The top-left Y for a card whose VERTICAL CENTRE sits at centerY.
        static double TopFor(double centerY) => centerY - NodeH / 2;

        // Track the lowest node BOTTOM in the main flow so a deep fan-out (5-6 children) still has the
        // OnFailure lane placed clear BELOW it (robust by construction — not a fixed offset gamble).
        double maxMainBottom = MidY + NodeH / 2;

        // Compensator nodes (one per top-level step with a compensator) placed on a lane below the spine.
        // Tracked here in the parent's column so the lane sits directly under the step it undoes.
        var compensators = new List<(string StepId, int Column)>();

        int column = 0;
        foreach (var step in steps.OrderBy(s => s.Order))
        {
            double x = StartX + column * ColStride;
            // Most steps occupy ONE column; a decision spans TWO (the diamond, then its branch column).
            int columnSpan = 1;

            if (step is { StepType: BatchStepType.ParallelGroup, ParallelGroup: { } pg })
            {
                var children = pg.Steps.OrderBy(c => c.Order).ToList();
                int n = children.Count;
                // Stack children vertically within THIS one column, centred around MidY:
                //   centre_i = MidY + (i - (n-1)/2) * ChildStride
                // n=1 ⇒ centred on MidY; n=2 ⇒ ±ChildStride/2; etc. Siblings are ChildStride apart.
                for (int i = 0; i < n; i++)
                {
                    double childCenter = MidY + (i - (n - 1) / 2.0) * ChildStride;
                    map[children[i].StepId] = (x, TopFor(childCenter));
                    maxMainBottom = Math.Max(maxMainBottom, childCenter + NodeH / 2);
                }
            }
            else if (step is { StepType: BatchStepType.Decision, Decision: { } decision })
            {
                // The diamond sits on the baseline in THIS column; its branch jobs stack vertically in the
                // NEXT column (like a PG's children, but one column to the right so the diamond → branch
                // edges read left-to-right). The whole decision therefore spans two columns.
                map[step.StepId] = (x, TopFor(MidY));
                var branches = decision.Branches;
                int n = branches.Count;
                double branchX = StartX + (column + 1) * ColStride;
                for (int i = 0; i < n; i++)
                {
                    double branchCenter = MidY + (i - (n - 1) / 2.0) * ChildStride;
                    map[branches[i].StepId] = (branchX, TopFor(branchCenter));
                    maxMainBottom = Math.Max(maxMainBottom, branchCenter + NodeH / 2);
                }
                columnSpan = 2;
            }
            else
            {
                // Job / ApprovalGate / Unknown — one node centred on the baseline.
                map[step.StepId] = (x, TopFor(MidY));
            }

            if (step.Compensation is not null)
            {
                compensators.Add((CompensationStepIds.For(step.StepId), column));
            }

            // The column advances by ONE for a single-node step (a PG collapses its stacked children into
            // one column), or by TWO past a decision's diamond + branch columns.
            column += columnSpan;
        }

        // Compensation lane: one node per compensator, in the PARENT's column, on a row below the spine's
        // deepest node (same floor-vs-content max the OnFailure lane uses). Placing it here EXTENDS
        // maxMainBottom, so the OnFailure lane computed below automatically shifts further down — the two
        // lower lanes never collide by construction. Skipped entirely when there are no compensators, so a
        // definition without compensators lays out byte-identically to before.
        if (compensators.Count > 0)
        {
            double compCenterY = Math.Max(MidY + CompensationLaneDy, maxMainBottom + ChildStride / 2 + NodeH / 2);
            foreach (var (compStepId, col) in compensators)
            {
                double cx = StartX + col * ColStride;
                map[compStepId] = (cx, TopFor(compCenterY));
            }
            maxMainBottom = Math.Max(maxMainBottom, compCenterY + NodeH / 2);
        }

        // OnFailure lane: a lower row, left→right. CONTINUES the column counter from where the spine ended
        // (instead of resetting to column 0) so the compensation node sits to the RIGHT of its source (the
        // spine's exit node). Drawflow connects output(right port)→input(left port); if the OnFailure node
        // were placed back at column 0 (far LEFT of its right-side source), the dashed edge would sweep
        // right then loop all the way back across the TOP of the canvas — an ugly full-width arc. Keeping it
        // to the right makes the edge flow naturally right-and-down. Lowered below the deepest fan-out so a
        // wide ParallelGroup can never collide with the lane (robust by construction).
        double failureCenterY = Math.Max(MidY + FailureLaneDy, maxMainBottom + ChildStride / 2 + NodeH / 2);
        int failureColumn = column;   // continue rightward from the spine's last column
        foreach (var step in onFailureSteps.OrderBy(s => s.Order))
        {
            double x = StartX + failureColumn * ColStride;
            map[step.StepId] = (x, TopFor(failureCenterY));
            failureColumn++;
        }

        return map;
    }
}
