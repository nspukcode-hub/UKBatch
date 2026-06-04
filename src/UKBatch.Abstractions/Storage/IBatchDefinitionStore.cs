using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Pluggable store for <see cref="BatchDefinition"/> instances. Only <see cref="BatchSource.Dashboard"/>
/// and <see cref="BatchSource.Api"/> definitions are persisted; <see cref="BatchSource.Code"/>
/// definitions are held in-memory by the runtime registry and are not the responsibility of this store.
/// </summary>
public interface IBatchDefinitionStore
{
    /// <summary>Creates a new definition; <see cref="BatchDefinition.Version"/> is set to <c>1</c>.</summary>
    Task<BatchDefinition> CreateAsync(BatchDefinition definition, CancellationToken cancellationToken);

    /// <summary>
    /// Updates an existing definition with optimistic concurrency. Throws
    /// <see cref="InvalidOperationException"/> on version mismatch.
    /// </summary>
    Task<BatchDefinition> UpdateAsync(BatchDefinition definition, CancellationToken cancellationToken);

    /// <summary>Deletes the definition. Idempotent — succeeds silently if already absent.</summary>
    Task DeleteAsync(string batchDefinitionId, CancellationToken cancellationToken);

    /// <summary>Returns the definition, or <c>null</c> if absent.</summary>
    Task<BatchDefinition?> GetAsync(string batchDefinitionId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the definition whose <see cref="BatchDefinition.Name"/> equals <paramref name="name"/>
    /// AND whose <see cref="BatchDefinition.Source"/> equals <paramref name="source"/>, or <c>null</c>
    /// if absent. Throws <see cref="ArgumentException"/> if <paramref name="name"/> is null or empty.
    /// </summary>
    /// <remarks>
    /// <para>Source-scoped because <see cref="BatchDefinition.Name"/> is unique-within-source by the
    /// data contract (enforced for <see cref="BatchSource.Dashboard"/> /
    /// <see cref="BatchSource.Api"/> by store implementations).</para>
    /// <para>Whitespace handling mirrors <c>IBatchDefinitionLookup.TryGetByName</c>: rejected at the
    /// REGISTRATION boundary (<see cref="CreateAsync"/> via <c>ThrowIfNullOrWhiteSpace</c> on the
    /// implementation), permitted at the LOOKUP boundary (this method); returns <c>null</c> on
    /// whitespace-only input.</para>
    /// </remarks>
    Task<BatchDefinition?> GetByNameAsync(string name, BatchSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Lists definitions filtered by source with pagination. Order is implementation-defined but
    /// MUST be stable across pages for the same query parameters.
    /// </summary>
    Task<IReadOnlyList<BatchDefinition>> ListAsync(BatchSource source, int offset, int limit, CancellationToken cancellationToken);

    /// <summary>Returns the total count of definitions for a source (used for paging UIs).</summary>
    Task<long> CountAsync(BatchSource source, CancellationToken cancellationToken);
}
