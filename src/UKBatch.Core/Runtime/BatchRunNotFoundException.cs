namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>JobRunner.ResumeBatchAsync</c> when the requested batch RUN id has no record in the
/// run store — the run was never created, or its record was pruned. Distinct from
/// <see cref="BatchDefinitionNotFoundException"/>, which is a missing DEFINITION.
/// </summary>
/// <remarks>
/// <para>Inherits <see cref="InvalidOperationException"/> for zero-test-churn, matching the rest of
/// the typed-exception family.</para>
/// <para><b>Query path stays nullable</b> — <c>IBatchRunStore.GetAsync</c> continues to return
/// <c>null</c> on miss (Maybe semantics for query). Only the resume entry point throws, because the
/// caller is asserting the run exists.</para>
/// </remarks>
public sealed class BatchRunNotFoundException : InvalidOperationException
{
    /// <summary>The missing run id; <c>null</c> if the caller did not supply context.</summary>
    public string? BatchId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public BatchRunNotFoundException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public BatchRunNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
