namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>InMemoryBatchDefinitionStore.UpdateAsync</c> when the caller's
/// <c>BatchDefinition.Version</c> does not match the store's current version (optimistic
/// concurrency violation). Consumed by REST endpoints for the 409
/// <c>ukbatch:concurrency-conflict</c> mapping on <c>PUT /batches/by-id/{id}</c>.
/// </summary>
/// <remarks>
/// <para>Distinct from <see cref="BatchDefinitionDuplicateNameException"/>.
/// Both map to HTTP 409 but with different ProblemDetails type URIs so dashboard create/edit
/// forms can render the right user message.</para>
/// <para>Inherits <see cref="InvalidOperationException"/> for zero-test-churn.</para>
/// </remarks>
public sealed class BatchConcurrencyConflictException : InvalidOperationException
{
    /// <summary>The definition id whose version mismatched; <c>null</c> if the caller did not supply context.</summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>The version held by the store; <c>null</c> if the caller did not supply context.</summary>
    public int? StoreVersion { get; init; }

    /// <summary>The version submitted by the caller; <c>null</c> if the caller did not supply context.</summary>
    public int? CallerVersion { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public BatchConcurrencyConflictException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public BatchConcurrencyConflictException(string message, Exception innerException) : base(message, innerException) { }
}
