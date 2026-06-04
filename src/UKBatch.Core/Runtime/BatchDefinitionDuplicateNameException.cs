using UKBatch.Abstractions.Batches;

namespace UKBatch.Runtime;

/// <summary>
/// Thrown by <c>InMemoryBatchDefinitionStore.CreateAsync</c> when a definition with the same
/// <see cref="BatchDefinition.Name"/> already exists in the same
/// <see cref="BatchSource"/>. Also thrown by <c>UpdateAsync</c> on the rename-to-existing-name case.
/// Consumed by REST endpoints for the 409 <c>ukbatch:batch-definition-duplicate-name</c>
/// mapping.
/// </summary>
/// <remarks>
/// <para>This exception is DISTINCT from <see cref="BatchConcurrencyConflictException"/> (which is
/// for version-mismatch on optimistic updates). They are separate so the dashboard's create/edit
/// forms get accurate ProblemDetails reporting.</para>
/// <para>Inherits <see cref="InvalidOperationException"/> for zero-test-churn.</para>
/// </remarks>
public sealed class BatchDefinitionDuplicateNameException : InvalidOperationException
{
    /// <summary>The conflicting definition name; <c>null</c> if the caller did not supply context.</summary>
    public string? Name { get; init; }

    /// <summary>The source whose name slot is occupied; <c>null</c> if the caller did not supply context.</summary>
    /// <remarks>Property name avoids collision with <see cref="Exception.Source"/>.</remarks>
    public BatchSource? BatchSource { get; init; }

    /// <summary>Constructs an exception with the given message.</summary>
    public BatchDefinitionDuplicateNameException(string message) : base(message) { }

    /// <summary>Constructs an exception with the given message and inner exception.</summary>
    public BatchDefinitionDuplicateNameException(string message, Exception innerException) : base(message, innerException) { }
}
