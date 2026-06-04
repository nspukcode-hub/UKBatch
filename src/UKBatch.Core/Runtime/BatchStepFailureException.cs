namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>BatchExecutor</c> step handlers when a step terminated as Failed / Cancelled or an
/// approval gate resolved as Rejected / TimedOutFail. Caught in the batch executor's per-step
/// try/catch and routed per <see cref="Abstractions.Batches.BatchFailurePolicy"/>.
/// </summary>
internal sealed class BatchStepFailureException : InvalidOperationException
{
    /// <summary>Constructs the exception with a message.</summary>
    public BatchStepFailureException(string message) : base(message) { }

    /// <summary>Constructs the exception with a message and inner exception.</summary>
    public BatchStepFailureException(string message, Exception inner) : base(message, inner) { }
}
