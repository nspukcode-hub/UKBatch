using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models;

/// <summary>
/// Deterministic top-down DAG layout for a batch's steps. Pure C# (no Blazor, no measurement) so it
/// is unit-testable and culture-independent: <c>DagView</c> renders the result and applies
/// <see cref="System.Globalization.CultureInfo.InvariantCulture"/> when stringifying coordinates.
/// </summary>
/// <remarks>
/// <para>The flow is a single vertical spine centred on <see cref="CenterX"/>. Job and ApprovalGate
/// steps sit on the spine; a ParallelGroup fans its (Job-only) children out at
/// <see cref="ParallelPitch"/> horizontal pitch, then fans back into a synthetic join point on the
/// spine. <see cref="BatchDefinition.OnFailureSteps"/> render as a dashed side-chain to the right.</para>
/// <para><b>Forward-compat (mirrors <c>BatchStep</c> deserialization contract):</b> an unrecognised
/// <see cref="BatchStepType"/> renders as a neutral <see cref="DagNodeKind.Unknown"/> node on the
/// spine — the layout NEVER throws on an unknown type.</para>
/// </remarks>
public sealed record class DagLayout
{
    /// <summary>Positioned nodes (spine + parallel children + failure branch).</summary>
    public required IReadOnlyList<DagLayoutNode> Nodes { get; init; }

    /// <summary>Connectors between nodes.</summary>
    public required IReadOnlyList<DagLayoutEdge> Edges { get; init; }

    /// <summary>viewBox width.</summary>
    public required double Width { get; init; }

    /// <summary>viewBox height.</summary>
    public required double Height { get; init; }

    internal const double NodeW = 200, NodeH = 80;
    private const double RowGap = 60;                // vertical gap between successive rows
    private const double RowPitch = NodeH + RowGap;  // 140 main-flow row stride
    private const double ParallelPitch = 220;        // horizontal pitch between parallel children
    private const double CenterX = 500;              // main spine x-center
    private const double PadTop = 40, PadBottom = 40;
    private const double FailureBranchDx = 320;      // OnFailure branch offset right of the spine

    /// <summary>Computes the layout for the main steps + optional OnFailure branch (pure auto-layout, no hints).</summary>
    public static DagLayout Compute(
        IReadOnlyList<BatchStep> steps,
        IReadOnlyList<BatchStep> onFailureSteps)
        => Compute(steps, onFailureSteps, hints: null);

    /// <summary>
    /// Computes the layout honoring per-step XY hints. A node whose
    /// <see cref="BatchStep.StepId"/> appears in <paramref name="hints"/> is positioned at the hinted
    /// (X, Y) instead of the auto-layout position. ParallelGroup hints apply to the group container
    /// only; children are deterministically positioned within the group bounds.
    /// Edges remain auto-computed (operator does NOT draw edges).
    /// </summary>
    /// <param name="steps">Top-level main flow steps.</param>
    /// <param name="onFailureSteps">OnFailure branch steps (dashed side-branch).</param>
    /// <param name="hints">Per-stepId XY overrides; <c>null</c> ⇒ pure auto-layout.</param>
    public static DagLayout Compute(
        IReadOnlyList<BatchStep> steps,
        IReadOnlyList<BatchStep> onFailureSteps,
        IReadOnlyDictionary<string, DagLayoutHint>? hints)
    {
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(onFailureSteps);

        var nodes = new List<DagLayoutNode>();
        var edges = new List<DagLayoutEdge>();
        double y = PadTop;
        string? prevAnchorStepId = null;          // step whose bottom-center the next edge starts from
        double prevAnchorX = CenterX, prevAnchorBottomY = PadTop;
        double maxX = CenterX + NodeW / 2;
        double minX = CenterX - NodeW / 2;
        // Bottom-edge tracker so hinted Y values that exceed the auto-y cursor still grow viewBox height.
        double maxNodeBottomY = 0;

        foreach (var step in steps.OrderBy(s => s.Order))
        {
            switch (step.StepType)
            {
                case BatchStepType.Job:
                {
                    var hasHint = TryGetHint(hints, step.StepId, out var jobHint);
                    var jobX = hasHint ? jobHint.X : CenterX - NodeW / 2;
                    var jobY = hasHint ? jobHint.Y : y;
                    nodes.Add(JobNode(step, x: jobX, y: jobY));
                    if (prevAnchorStepId is not null)
                        edges.Add(SeqEdge(prevAnchorX, prevAnchorBottomY, jobX + NodeW / 2, jobY,
                            fromStepId: prevAnchorStepId, toStepId: step.StepId));
                    prevAnchorStepId = step.StepId;
                    prevAnchorX = jobX + NodeW / 2;
                    prevAnchorBottomY = jobY + NodeH;
                    // track bottom edge
                    maxNodeBottomY = Math.Max(maxNodeBottomY, jobY + NodeH);
                    minX = Math.Min(minX, jobX);
                    maxX = Math.Max(maxX, jobX + NodeW);
                    // Advance y-cursor ONLY for non-hinted nodes — a hint frees the spine y-stack.
                    if (!hasHint) y += RowPitch;
                    break;
                }
                case BatchStepType.ApprovalGate:
                {
                    // Chrome DAG-render fix (2026-06): the approval node is now a RECTANGLE identical in
                    // size to a job node (NodeW×NodeH), not a hexagon. The narrow 100px hex foreignObject
                    // mis-placed its centered content far LEFT under the canvas CSS transform in Chromium;
                    // converging onto the job-node rectangle (which provably renders correctly) eliminates
                    // the displacement. Layout math mirrors the Job case exactly.
                    var hasHint = TryGetHint(hints, step.StepId, out var apHint);
                    var hx = hasHint ? apHint.X : CenterX - NodeW / 2;
                    var hy = hasHint ? apHint.Y : y;
                    nodes.Add(ApprovalNode(step, hx, hy));
                    if (prevAnchorStepId is not null)
                        edges.Add(SeqEdge(prevAnchorX, prevAnchorBottomY, hx + NodeW / 2, hy,
                            fromStepId: prevAnchorStepId, toStepId: step.StepId));
                    prevAnchorStepId = step.StepId;
                    prevAnchorX = hx + NodeW / 2;
                    prevAnchorBottomY = hy + NodeH;
                    // track bottom edge
                    maxNodeBottomY = Math.Max(maxNodeBottomY, hy + NodeH);
                    minX = Math.Min(minX, hx);
                    maxX = Math.Max(maxX, hx + NodeW);
                    if (!hasHint) y += RowPitch;
                    break;
                }
                case BatchStepType.ParallelGroup:
                {
                    var children = step.ParallelGroup?.Steps.OrderBy(c => c.Order).ToList() ?? [];
                    int n = Math.Max(children.Count, 1);
                    // Center the fan: total span = (n-1)*pitch; leftmost child center x.
                    double span = (n - 1) * ParallelPitch;
                    // ParallelGroup hint = group container CENTER (operator-friendly). Children are
                    // deterministic from the group XY (one mental unit). Child hints IGNORED.
                    var hasHint = TryGetHint(hints, step.StepId, out var groupHint);
                    var groupCenterX = hasHint ? groupHint.X : CenterX;
                    var groupY = hasHint ? groupHint.Y : y;
                    double firstCx = groupCenterX - span / 2;
                    for (int i = 0; i < children.Count; i++)
                    {
                        double cx = firstCx + i * ParallelPitch;
                        // Forward-compat for v0.2+ definitions whose parallel children
                        // may be ApprovalGate or unknown kinds. The wizard still emits Job-only;
                        // this branch matters for read-only Code/Api-source DAG rendering.
                        var child = children[i];
                        DagLayoutNode cnode = child.StepType switch
                        {
                            BatchStepType.ApprovalGate => ApprovalNode(child, cx - NodeW / 2, groupY),
                            BatchStepType.Job          => JobNode(child, x: cx - NodeW / 2, groupY, groupId: step.StepId),
                            _                          => UnknownNode(child, cx - NodeW / 2, groupY),
                        };
                        nodes.Add(cnode);
                        if (prevAnchorStepId is not null)
                            edges.Add(ParallelEdge(prevAnchorX, prevAnchorBottomY, cx, groupY,
                                fromStepId: prevAnchorStepId, toStepId: child.StepId));
                        // Per-child bottom-edge tracking — approval children are now rectangles (NodeH), same as jobs.
                        maxNodeBottomY = Math.Max(maxNodeBottomY, groupY + NodeH);
                        minX = Math.Min(minX, cx - NodeW / 2);
                        maxX = Math.Max(maxX, cx + NodeW / 2);
                    }
                    // Fan-in: synthetic join point one row below; edges from each child bottom to join.
                    // The join is a synthetic anchor (a point on the spine, no node) → toStepId
                    // is null and DagView source-fallbacks to color this edge by the child's status.
                    double joinY = groupY + NodeH + RowGap / 2;
                    for (int i = 0; i < children.Count; i++)
                    {
                        double cx = firstCx + i * ParallelPitch;
                        edges.Add(ParallelEdge(cx, groupY + NodeH, groupCenterX, joinY,
                            fromStepId: children[i].StepId, toStepId: null));
                    }
                    // The group's "exit anchor" is the join point on the spine (no node there).
                    prevAnchorStepId = step.StepId;
                    prevAnchorX = groupCenterX;
                    prevAnchorBottomY = joinY;
                    // Synthetic join point — no node added, but the tracker covers the branch-height edge case.
                    maxNodeBottomY = Math.Max(maxNodeBottomY, joinY);
                    if (!hasHint) y = joinY + RowGap; // next row starts below the join
                    break;
                }
                default:
                {
                    // Unknown future step type — neutral placeholder on the spine (NEVER throw).
                    var hasHint = TryGetHint(hints, step.StepId, out var unknownHint);
                    var ux = hasHint ? unknownHint.X : CenterX - NodeW / 2;
                    var uy = hasHint ? unknownHint.Y : y;
                    nodes.Add(UnknownNode(step, ux, uy));
                    if (prevAnchorStepId is not null)
                        edges.Add(SeqEdge(prevAnchorX, prevAnchorBottomY, ux + NodeW / 2, uy,
                            fromStepId: prevAnchorStepId, toStepId: step.StepId));
                    prevAnchorStepId = step.StepId;
                    prevAnchorX = ux + NodeW / 2;
                    prevAnchorBottomY = uy + NodeH;
                    // Unknown default — same bottom-edge tracking as the other node kinds.
                    maxNodeBottomY = Math.Max(maxNodeBottomY, uy + NodeH);
                    minX = Math.Min(minX, ux);
                    maxX = Math.Max(maxX, ux + NodeW);
                    if (!hasHint) y += RowPitch;
                    break;
                }
            }
        }

        // OnFailure branch: a dashed side-chain to the right, anchored from the LAST spine node.
        // Track branchBottomY so the viewBox height accounts for a branch taller than the spine
        // (otherwise a spine of 1 step + 3 compensation steps would clip the bottom).
        double branchBottomY = PadTop;
        if (onFailureSteps.Count > 0)
        {
            double fx = CenterX + FailureBranchDx;
            double fy = PadTop;
            string? fPrev = prevAnchorStepId;       // dashed edge from spine tail into the branch head
            double fPrevX = prevAnchorX, fPrevBottomY = prevAnchorBottomY;
            bool firstFailure = true;
            // Render non-Job OnFailure steps as Unknown (forward-compat) instead of
            // silently filtering them out — the operator should see SOMETHING they didn't expect.
            foreach (var step in onFailureSteps.OrderBy(s => s.Order))
            {
                var hasHint = TryGetHint(hints, step.StepId, out var failureHint);
                var sx = hasHint ? failureHint.X : fx - NodeW / 2;
                var sy = hasHint ? failureHint.Y : fy;
                DagLayoutNode fnode = step.StepType == BatchStepType.Job
                    ? JobNode(step, x: sx, y: sy, isFailureBranch: true)
                    : UnknownNode(step, sx, sy);
                nodes.Add(fnode);
                if (firstFailure && fPrev is not null)
                    edges.Add(FailureEdge(fPrevX, fPrevBottomY, sx + NodeW / 2, sy)); // spine → branch (dashed red)
                else if (!firstFailure)
                    edges.Add(FailureEdge(sx + NodeW / 2, sy - RowGap, sx + NodeW / 2, sy));
                minX = Math.Min(minX, sx);
                maxX = Math.Max(maxX, sx + NodeW);
                branchBottomY = sy + NodeH;          // last placed node's bottom edge
                // track bottom edge
                maxNodeBottomY = Math.Max(maxNodeBottomY, sy + NodeH);
                if (!hasHint) fy += RowPitch;
                firstFailure = false;
            }
        }

        double width = (maxX - minX) + 80;          // +pad (40 left + 40 right after the shift below)
        // When no hints are present, maxNodeBottomY <= y by construction, so this reduces to the
        // simple Math.Max(y, branchBottomY) spine/branch height.
        double height = Math.Max(maxNodeBottomY, Math.Max(y, branchBottomY)) + PadBottom;
        // Normalize: shift all x so minX maps to 40 (left pad) — keeps the viewBox at x >= 0.
        double shift = 40 - minX;
        var shiftedNodes = nodes.Select(nd => nd with { X = nd.X + shift }).ToList();
        var shiftedEdges = edges.Select(e => e with { X1 = e.X1 + shift, X2 = e.X2 + shift }).ToList();
        return new DagLayout { Nodes = shiftedNodes, Edges = shiftedEdges, Width = width, Height = height };
    }

    // Local helper — null-safe hint lookup that satisfies nullable flow analysis (CS8602/CA1062
    // would fire on a bare `hints![step.StepId]` even after a guarded `hints is not null` check
    // because the boolean is not tracked across the indexer call).
    private static bool TryGetHint(IReadOnlyDictionary<string, DagLayoutHint>? hints, string stepId, out DagLayoutHint hint)
    {
        if (hints is not null && hints.TryGetValue(stepId, out var h))
        {
            hint = h;
            return true;
        }
        hint = default!;
        return false;
    }

    // ── node factories ──────────────────────────────────────────────────────────────

    private static DagLayoutNode JobNode(BatchStep step, double x, double y, string? groupId = null, bool isFailureBranch = false) => new()
    {
        StepId = step.StepId,
        Kind = DagNodeKind.Job,
        X = x, Y = y, Width = NodeW, Height = NodeH,
        Title = step.Job?.JobName ?? "(unknown job)",
        Subtitle = isFailureBranch ? "compensation" : null,
        TargetService = step.Job?.TargetService,
        GroupId = groupId,
        IsFailureBranch = isFailureBranch,
    };

    private static DagLayoutNode ApprovalNode(BatchStep step, double x, double y) => new()
    {
        StepId = step.StepId,
        Kind = DagNodeKind.Approval,
        // Chrome DAG-render fix (2026-06): rectangle, same dims as a job node (was hex 100×100). The
        // narrow hex foreignObject mis-placed its content under the canvas transform; the job-node
        // rectangle does not. DagView renders this as a `dag-node--approval` rect (purple left-border).
        X = x, Y = y, Width = NodeW, Height = NodeH,
        Title = step.Approval?.Title ?? "(approval gate)",
        Subtitle = ApprovalSubtitle(step.Approval),
    };

    private static DagLayoutNode UnknownNode(BatchStep step, double x, double y) => new()
    {
        StepId = step.StepId,
        Kind = DagNodeKind.Unknown,
        X = x, Y = y, Width = NodeW, Height = NodeH,
        Title = step.StepId,
        Subtitle = $"unsupported ({step.StepType})",
    };

    private static string? ApprovalSubtitle(ApprovalGateConfig? cfg)
    {
        if (cfg is null) return null;
        if (cfg.AllowedRoles.Count == 0) return "no roles";
        if (cfg.AllowedRoles.Contains(ApprovalGateConfig.AnyAuthenticatedUser)) return "any user";
        return string.Join(", ", cfg.AllowedRoles);
    }

    // ── edge factories ──────────────────────────────────────────────────────────────

    // The trailing fromStepId/toStepId carry the edge's endpoint StepIds so
    // DagView can color a live edge by its DESTINATION node's status. Synthetic anchors
    // (the fan-in join point; OnFailure connectors) pass null and stay status-neutral.
    private static DagLayoutEdge SeqEdge(double x1, double y1, double x2, double y2,
        string? fromStepId = null, string? toStepId = null)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Kind = DagEdgeKind.Sequential, FromStepId = fromStepId, ToStepId = toStepId };

    private static DagLayoutEdge ParallelEdge(double x1, double y1, double x2, double y2,
        string? fromStepId = null, string? toStepId = null)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Kind = DagEdgeKind.Parallel, FromStepId = fromStepId, ToStepId = toStepId };

    private static DagLayoutEdge FailureEdge(double x1, double y1, double x2, double y2,
        string? fromStepId = null, string? toStepId = null)
        => new() { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Kind = DagEdgeKind.OnFailure, FromStepId = fromStepId, ToStepId = toStepId };
}
