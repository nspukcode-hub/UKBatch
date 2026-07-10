namespace UKBatch.Dashboard.Models.DagStatus;

/// <summary>
/// One edge in the wire payload sent C#→JS for <c>buildGraph</c>. Both endpoints are
/// ALWAYS real, rendered node StepIds — produced by <see cref="DagStatusEdges.Build"/>, which derives
/// entry/exit structurally from the <c>Steps</c> list (no <c>null</c> synthetic anchors).
/// </summary>
/// <remarks>
/// The fan-out↔fan-in discriminator (<see cref="StatusEdge.IsFanIn"/>) lives on the INTERNAL
/// <see cref="StatusEdge"/> consumed by <see cref="DagStatusClasses.EdgeClass"/>; this wire record only
/// carries the already-resolved <see cref="StatusClass"/>, so JS needs no <c>IsFanIn</c>.
/// </remarks>
public sealed record class DagStatusEdge
{
    /// <summary>Source node StepId.</summary>
    public required string FromStepId { get; init; }

    /// <summary>Destination node StepId.</summary>
    public required string ToStepId { get; init; }

    /// <summary>Visual line style: <c>"Sequential" | "Parallel" | "OnFailure" | "Compensation"</c>.</summary>
    public required string Kind { get; init; }

    /// <summary>Resolved <c>data-status</c> value (may be <c>""</c>) from <see cref="DagStatusClasses.EdgeClass"/>.</summary>
    public required string StatusClass { get; init; }
}
