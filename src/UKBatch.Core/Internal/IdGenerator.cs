namespace UKBatch.Internal;

/// <summary>
/// Generates k-sortable, time-ordered identifiers using <see cref="Guid.CreateVersion7()"/>
/// (UUIDv7). Identifiers are formatted as 32-character hex strings without
/// separators (<c>"N"</c> format) for compact storage and URL safety.
/// </summary>
internal static class IdGenerator
{
    /// <summary>Generates a new execution id.</summary>
    public static string NewExecutionId() => Guid.CreateVersion7().ToString("N");

    /// <summary>Generates a new batch id.</summary>
    public static string NewBatchId() => Guid.CreateVersion7().ToString("N");

    /// <summary>Generates a new batch-step id.</summary>
    public static string NewStepId() => Guid.CreateVersion7().ToString("N");

    /// <summary>Generates a new approval-gate id.</summary>
    public static string NewApprovalId() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// Generates a new wire-format <c>JobMessage.MessageId</c>. UUIDv7 chosen for
    /// time-ordered + k-sortable + clock-skew-insensitive properties (same as
    /// <see cref="NewExecutionId"/>). Receivers de-duplicate on this id.
    /// </summary>
    public static string NewMessageId() => Guid.CreateVersion7().ToString("N");
}
