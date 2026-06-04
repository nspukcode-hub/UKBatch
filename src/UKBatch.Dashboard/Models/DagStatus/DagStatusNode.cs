namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// One node in the wire payload sent C#→JS for <c>buildGraph</c>. Kept separate from
/// <c>Models.Editor</c> node specs so the read-only contract can't accidentally inherit editor mutation
/// fields (<c>OrderBadge</c>, <c>Children</c>, drop semantics). Coordinates cross as JSON doubles
/// (culture-invariant), never as strings.
/// </summary>
public sealed record class DagStatusNode
{
    /// <summary>Source <c>BatchStep.StepId</c> — the selection + status join key.</summary>
    public required string StepId { get; init; }

    /// <summary><c>"Job" | "Approval" | "Unknown"</c> — drives the node card variant.</summary>
    public required string Kind { get; init; }

    /// <summary>Primary label (job name / approval title / step id).</summary>
    public required string Title { get; init; }

    /// <summary>Secondary label, or <c>null</c>.</summary>
    public string? Subtitle { get; init; }

    /// <summary>Cross-service target (Job nodes only); <c>null</c> ⇒ local.</summary>
    public string? TargetService { get; init; }

    /// <summary><see cref="DagLayoutNode.X"/> (absolute, post-shift) — placed verbatim by Drawflow.</summary>
    public required double X { get; init; }

    /// <summary><see cref="DagLayoutNode.Y"/> — placed verbatim by Drawflow.</summary>
    public required double Y { get; init; }

    /// <summary><c>true</c> ⇒ node lives on the dashed OnFailure side branch.</summary>
    public required bool IsFailureBranch { get; init; }

    /// <summary>Initial <c>data-status</c> value (may be <c>""</c>) from <see cref="DagStatusClasses.NodeClass"/>.</summary>
    public required string StatusClass { get; init; }
}
