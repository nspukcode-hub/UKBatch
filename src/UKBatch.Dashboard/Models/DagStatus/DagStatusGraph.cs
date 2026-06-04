namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// The complete graph payload sent C#→JS for <c>buildGraph</c>: positioned nodes +
/// structural edges. Sent once per topology change (relayout is rare); live status afterwards
/// flows via <c>setStatuses</c> without a rebuild.
/// </summary>
public sealed record class DagStatusGraph
{
    /// <summary>Positioned nodes (spine + parallel children + failure branch).</summary>
    public required IReadOnlyList<DagStatusNode> Nodes { get; init; }

    /// <summary>Structural edges (both endpoints real StepIds).</summary>
    public required IReadOnlyList<DagStatusEdge> Edges { get; init; }
}
