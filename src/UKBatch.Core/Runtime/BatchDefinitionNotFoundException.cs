namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>JobRunner.TriggerBatchAsync</c> (definition lookup miss) and
/// <c>InMemoryBatchDefinitionStore.UpdateAsync</c> (id miss on optimistic update) when the
/// requested batch DEFINITION id is not in any source (Code / Dashboard / Api). Consumed by
/// REST endpoints for the 404 <c>ukbatch:batch-definition-not-found</c> mapping.
/// </summary>
/// <remarks>
/// <para>Inherits <see cref="InvalidOperationException"/> for zero-test-churn.</para>
/// <para><b>Query path stays nullable</b> — <c>IBatchDefinitionStore.GetAsync</c> and
/// <c>IBatchCatalogService.GetByIdAsync</c> continue to return <c>null</c> on miss (Maybe
/// semantics for query). Only mutate paths (<c>UpdateAsync</c>) and runtime resolution
/// (<c>JobRunner.TriggerBatchAsync</c>) throw — caller is asserting existence in these paths.</para>
/// </remarks>
public sealed class BatchDefinitionNotFoundException : InvalidOperationException
{
    /// <summary>The missing definition id; <c>null</c> if the caller did not supply context.</summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public BatchDefinitionNotFoundException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public BatchDefinitionNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}
