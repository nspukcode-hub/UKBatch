namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// An explicit execution-order move from the order-rail. The operator clicked a chevron
/// to shift the step at <see cref="Index"/> by <see cref="Delta"/> (−1 = earlier, +1 = later). Order
/// changes ONLY via the rail — never derived from node Y-position (the hard invariant that keeps a
/// purely aesthetic drag from silently reordering the persisted workflow).
/// </summary>
/// <param name="Index">The 0-based list index of the step being moved.</param>
/// <param name="Delta">The signed shift (−1 earlier, +1 later).</param>
public sealed record class MoveStepArgs(int Index, int Delta);
