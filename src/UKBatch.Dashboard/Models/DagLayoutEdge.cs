namespace UKBatch.Dashboard.Models;

/// <summary>Visual classification of a DAG edge — drives stroke style in <c>DagView</c>.</summary>
public enum DagEdgeKind
{
    /// <summary>Solid connector between successive main-spine steps.</summary>
    Sequential,
    /// <summary>Fan-out / fan-in connector for a parallel group.</summary>
    Parallel,
    /// <summary>Dashed red connector into / within the OnFailure branch.</summary>
    OnFailure,
}

/// <summary>
/// One connector in a <see cref="DagLayout"/>, from (<see cref="X1"/>, <see cref="Y1"/>) to
/// (<see cref="X2"/>, <see cref="Y2"/>) in viewBox coordinates. Pure data — <c>DagView</c> turns it
/// into an SVG <c>&lt;path&gt;</c> with a vertical-bezier <c>d</c>.
/// </summary>
public sealed record class DagLayoutEdge
{
    /// <summary>Start x.</summary>
    public required double X1 { get; init; }

    /// <summary>Start y.</summary>
    public required double Y1 { get; init; }

    /// <summary>End x.</summary>
    public required double X2 { get; init; }

    /// <summary>End y.</summary>
    public required double Y2 { get; init; }

    /// <summary>Edge classification.</summary>
    public required DagEdgeKind Kind { get; init; }

    /// <summary>
    /// StepId of the node this edge departs FROM, or <c>null</c> for a synthetic anchor (e.g. the
    /// fan-in join point, which is a point on the spine, not a node). Lets
    /// <c>DagView.EdgeStatusClass</c> color a live edge by its endpoints' statuses.
    /// </summary>
    public string? FromStepId { get; init; }

    /// <summary>
    /// StepId of the node this edge arrives AT, or <c>null</c> for a synthetic anchor (fan-in join).
    /// A sequential edge colors by this DESTINATION status only — a grey edge
    /// into a not-started grey node is the honest "not fired yet" signal; <c>null</c> destination
    /// (synthetic fan-in) falls back to <see cref="FromStepId"/> (the child).
    /// </summary>
    public string? ToStepId { get; init; }
}
