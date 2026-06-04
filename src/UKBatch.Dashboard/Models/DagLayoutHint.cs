namespace UKBatch.Dashboard.Models;

/// <summary>
/// Layout hint for a single DAG node — overrides the deterministic auto-layout X/Y for that step.
/// Persisted in <c>BatchDefinition.Metadata["dashboard.layoutHints"]</c> as a nested object.
/// </summary>
/// <remarks>
/// Operator-set positions for the interactive DAG view. <see cref="DagLayoutHintBounds"/>
/// clamps the operator-set range; <see cref="DagLayoutHintsSerializer"/> additionally rejects NaN /
/// Infinity on Serialize and Parse for forward-compat safety.
/// </remarks>
public sealed record class DagLayoutHint
{
    /// <summary>X coordinate in viewBox space (<see cref="DagLayout"/> coordinate system).</summary>
    public required double X { get; init; }

    /// <summary>Y coordinate in viewBox space.</summary>
    public required double Y { get; init; }
}
