namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>JobRunner.RetryBatchAsync</c> when a run exists but cannot be retried from its failed
/// step: it is not <c>Failed</c>, it was compensated (its completed steps were already undone, so a
/// forward continuation would replay work on top of a rolled-back state), its store never recorded a
/// resume cursor although steps completed (the retry point cannot be proven, and guessing "from the
/// beginning" would re-run completed work), or the definition's topology changed since the run started.
/// </summary>
/// <remarks>
/// Inherits <see cref="InvalidOperationException"/> for zero-test-churn, matching the rest of the
/// typed-exception family. The REST layer maps this to a conflict response; the message names the
/// specific precondition that failed so an operator can act on it.
/// </remarks>
public sealed class BatchRunNotRetryableException : InvalidOperationException
{
    /// <summary>The run id that was refused; <c>null</c> if the caller did not supply context.</summary>
    public string? BatchId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public BatchRunNotRetryableException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public BatchRunNotRetryableException(string message, Exception innerException) : base(message, innerException) { }
}
