namespace UKBatch.Dashboard.Models;

/// <summary>
/// Clamp bounds for drag-set <see cref="DagLayoutHint"/> coordinates. Generous values prevent
/// extreme drag-off-canvas placements without restricting normal operator workflow. Values are
/// also defended by <see cref="DagLayoutHintsSerializer"/> (NaN / Infinity skip).
/// </summary>
/// <remarks>
/// Exposed publicly so tests reference the constants rather than encoding magic
/// numbers. Typical batch viewBox width is ~1200 px at default zoom; 10000 leaves room
/// for far-right pan workflow without truncating legitimate operator placements.
/// </remarks>
public static class DagLayoutHintBounds
{
    /// <summary>Minimum X for an operator-set layout hint (allows slight negative for left-pan workflow).</summary>
    public const double MinX = -1000;

    /// <summary>Maximum X for an operator-set layout hint (10× typical viewBox width — far-right edge cases).</summary>
    public const double MaxX = 10_000;

    /// <summary>Minimum Y for an operator-set layout hint.</summary>
    public const double MinY = -1000;

    /// <summary>Maximum Y for an operator-set layout hint.</summary>
    public const double MaxY = 10_000;
}
