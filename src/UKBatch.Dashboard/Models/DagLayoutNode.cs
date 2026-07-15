namespace UKBatch.Dashboard.Models;

/// <summary>Kind of a laid-out DAG node — drives the rendered shape + icon in <c>DagView</c>.</summary>
public enum DagNodeKind
{
    /// <summary>Rectangular job node (200×80).</summary>
    Job,
    /// <summary>Rectangular approval-gate node (200×80) — purple left-border accent + <c>rule</c> icon.</summary>
    Approval,
    /// <summary>
    /// Rectangular decision node (200×80) — amber left-border accent + <c>call_split</c> icon. Rendered as
    /// a rectangle (not an SVG diamond polygon): a non-rectangle foreignObject mis-places under the canvas
    /// <c>transform: scale</c> in Chromium, the same reason the approval gate is a rectangle. The routing
    /// "diamond" reads from the accent + icon, and its branch job nodes fan out below it.
    /// </summary>
    Decision,
    /// <summary>Neutral placeholder for an unrecognised future step type (forward-compat).</summary>
    Unknown,
}

/// <summary>
/// One positioned node in a <see cref="DagLayout"/>. Pure data — no Blazor. Coordinates are in the
/// SVG viewBox space; <c>DagView</c> renders each node as a <c>&lt;foreignObject&gt;</c> at
/// (<see cref="X"/>, <see cref="Y"/>).
/// </summary>
public sealed record class DagLayoutNode
{
    /// <summary>Source <c>BatchStep.StepId</c> — the live-status join key (== <c>JobExecution.BatchStepId</c>).</summary>
    public required string StepId { get; init; }

    /// <summary>Rendered shape.</summary>
    public required DagNodeKind Kind { get; init; }

    /// <summary>Left edge in viewBox coordinates.</summary>
    public required double X { get; init; }

    /// <summary>Top edge in viewBox coordinates.</summary>
    public required double Y { get; init; }

    /// <summary>Node width.</summary>
    public required double Width { get; init; }

    /// <summary>Node height.</summary>
    public required double Height { get; init; }

    /// <summary>Primary label — job name / approval title / step id for unknown types.</summary>
    public required string Title { get; init; }

    /// <summary>Secondary label — e.g. join policy, allowed-role summary, or <c>null</c>.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Cross-service target (Job nodes only); <c>null</c> ⇒ local execution (cloud badge omitted).</summary>
    public string? TargetService { get; init; }

    /// <summary>Parent <c>ParallelGroup</c> step id when this node is a parallel child, else <c>null</c>.</summary>
    public string? GroupId { get; init; }

    /// <summary>True ⇒ node lives on the dashed OnFailure side branch.</summary>
    public bool IsFailureBranch { get; init; }
}
