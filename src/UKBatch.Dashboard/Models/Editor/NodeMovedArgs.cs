namespace UKBatch.Dashboard.Models.Editor;

/// <summary>
/// A committed node-move (<c>OnNodeMoved</c>): the operator dragged a node and it settled
/// (~120ms debounce on the JS side, pointer-up granularity — NOT per-frame). The Editor records the
/// new (<see cref="X"/>, <see cref="Y"/>) as the step's layout hint.
/// </summary>
/// <param name="StepId">The moved step's <c>StepId</c>.</param>
/// <param name="X">New X in Drawflow canvas space.</param>
/// <param name="Y">New Y in Drawflow canvas space.</param>
public sealed record class NodeMovedArgs(string StepId, double X, double Y);
