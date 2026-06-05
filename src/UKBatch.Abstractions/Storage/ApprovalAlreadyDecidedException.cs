namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Thrown by <see cref="IApprovalGateStore.RecordOutcomeAsync"/> when a SECOND decision is attempted on a
/// gate that is already <see cref="ApprovalRecordStatus.Decided"/>. A gate's terminal outcome is an
/// immutable audit fact — a late duplicate approve, or an operator decision racing the startup reaper's
/// Interrupt, must NOT silently overwrite the recorded outcome.
/// </summary>
/// <remarks>Inherits <see cref="InvalidOperationException"/> so existing 4xx mapping and test setups are
/// unaffected.</remarks>
public sealed class ApprovalAlreadyDecidedException : InvalidOperationException
{
    /// <summary>The gate id that was already decided.</summary>
    public string? ApprovalId { get; init; }

    /// <summary>The outcome already recorded (the one that must NOT be overwritten).</summary>
    public ApprovalRecordOutcome? ExistingOutcome { get; init; }

    /// <summary>Constructs the exception.</summary>
    public ApprovalAlreadyDecidedException(string message) : base(message) { }
}
