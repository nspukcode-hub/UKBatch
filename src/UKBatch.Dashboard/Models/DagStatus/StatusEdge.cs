namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// A purely-structural DAG edge derived by <see cref="DagStatusEdges.Build"/> from the ordered
/// <c>Steps</c> list. BOTH endpoints are ALWAYS real, rendered node StepIds — there are no <c>null</c>
/// synthetic anchors (unlike <c>DagLayoutEdge</c>, whose fan-in join is a point on the spine).
/// </summary>
/// <remarks>
/// Status-free by design so the topology derivation is unit-testable without a status map.
/// <see cref="DagStatusClasses.EdgeClass"/> resolves the status token from this + the live map;
/// <c>DagStatusCanvas</c> then projects it into the wire-level <see cref="DagStatusEdge"/>.
/// <para><see cref="IsFanIn"/> carries the fan-out↔fan-in discriminator the walk already
/// computes (<c>prevWasParallelGroup</c>). Both fan-out (<c>prev→child</c>) and fan-in
/// (<c>child→successor</c>) edges are <c>Kind=Parallel</c>, so <see cref="Kind"/> alone cannot tell
/// <see cref="DagStatusClasses.EdgeClass"/> which endpoint to key status off. The visual
/// <see cref="Kind"/> taxonomy (line style) is unchanged; <see cref="IsFanIn"/> drives ONLY status-key
/// node selection.</para>
/// </remarks>
public sealed record class StatusEdge
{
    /// <summary>Source node StepId (always real).</summary>
    public required string FromStepId { get; init; }

    /// <summary>Destination node StepId (always real).</summary>
    public required string ToStepId { get; init; }

    /// <summary>Visual line style: <c>"Sequential" | "Parallel" | "OnFailure"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>
    /// <c>true</c> ⇒ this is a fan-in edge (<c>child→successor</c>) whose status keys off the
    /// <b>source/child</b>; <c>false</c> ⇒ status keys off the <b>destination</b> (Sequential / fan-out).
    /// </summary>
    public required bool IsFanIn { get; init; }
}
