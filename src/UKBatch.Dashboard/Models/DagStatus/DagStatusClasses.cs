using UKBatch.Abstractions.Models;

namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// Pure-C# JobStatus → status-token mapping for the read-only Drawflow status canvas.
/// Ported verbatim from <c>DagView.StatusClass</c> / <c>DagView.EdgeStatusClass</c> so the live-status
/// behaviour is unchanged — the only difference is that the canvas keys CSS off a single
/// <c>data-status</c> attribute (enum-name string) rather than a <c>dag-node--*</c> CSS class.
/// </summary>
/// <remarks>
/// Shared by both <c>DagStatusCanvas</c> (which serialises the result into the node/edge spec) and its
/// graceful-degradation fallback list — DRY, single source of truth, unit-testable without Blazor.
/// </remarks>
public static class DagStatusClasses
{
    /// <summary>
    /// The <c>data-status</c> value for a node. <c>"muted"</c> when the node has no map entry (not started
    /// yet); the lower-cased status family otherwise. Returns <c>""</c> in static mode (<paramref name="statusByStepId"/>
    /// is <c>null</c>) — no status styling, mirroring <c>DagView.StatusClass</c>.
    /// </summary>
    public static string NodeClass(
        DagLayoutNode node,
        IReadOnlyDictionary<string, JobStatus>? statusByStepId)
    {
        ArgumentNullException.ThrowIfNull(node);
        return NodeClassForStepId(node.StepId, statusByStepId);
    }

    /// <summary>
    /// The <c>data-status</c> value for a node addressed by its <see cref="DagLayoutNode.StepId"/> directly.
    /// Used for synthesized nodes that are NOT <see cref="DagLayoutNode"/>s (the compensator lane, keyed by a
    /// derived <c>{parent}:comp</c> id). Same semantics as <see cref="NodeClass"/>: <c>""</c> in static mode,
    /// <c>"muted"</c> when not started, the lower-cased status family otherwise.
    /// </summary>
    public static string NodeClassForStepId(
        string stepId,
        IReadOnlyDictionary<string, JobStatus>? statusByStepId)
    {
        ArgumentException.ThrowIfNullOrEmpty(stepId);
        if (statusByStepId is null) return string.Empty;                 // static mode — no status styling
        if (!statusByStepId.TryGetValue(stepId, out var s)) return "muted"; // not started yet
        return StatusToken(s);
    }

    /// <summary>
    /// The <c>data-status</c> value for an edge. Keyed off the edge's <b>destination</b> for Sequential
    /// and fan-out edges, off the <b>source/child</b> for a fan-in edge (preserve the honest
    /// "this branch finished" signal). Returns <c>""</c> in static mode or when the key node hasn't started
    /// (kind class wins — OnFailure stays dashed-red). Ports <c>DagView.EdgeStatusClass</c>
    /// (<c>e.ToStepId ?? e.FromStepId</c>), with the synthetic-anchor fallback now expressed structurally
    /// via <see cref="StatusEdge.IsFanIn"/>.
    /// </summary>
    public static string EdgeClass(
        StatusEdge edge,
        IReadOnlyDictionary<string, JobStatus>? statusByStepId)
    {
        ArgumentNullException.ThrowIfNull(edge);
        if (statusByStepId is null) return string.Empty;                 // static topology mode
        // Fan-in keys off the source (child) — "this branch finished". Everything else keys off the
        // destination (a grey edge into a not-started grey node = the honest "not fired yet" signal).
        var keyStepId = edge.IsFanIn ? edge.FromStepId : edge.ToStepId;
        if (!statusByStepId.TryGetValue(keyStepId, out var s)) return string.Empty; // endpoint not started
        return StatusToken(s);
    }

    // Single status-family → token table, shared by node + edge mapping. Returns "" for terminal-but-untinted
    // families (Pending/Scheduled/Enqueued) so the default neutral style wins — matches DagView's `_ => ""`.
    private static string StatusToken(JobStatus s) => s switch
    {
        JobStatus.Running or JobStatus.Retrying or JobStatus.AwaitingApproval => "running",
        JobStatus.Completed => "completed",
        JobStatus.Failed => "failed",
        JobStatus.Cancelled or JobStatus.Cancelling => "cancelled",
        JobStatus.Skipped => "skipped",
        _ => string.Empty,
    };
}
