using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Read-only union of every <see cref="BatchDefinition"/> regardless of source.
/// Composes <see cref="Batches.IBatchDefinitionLookup"/> (Code-source, in-process) and
/// <see cref="IBatchDefinitionStore"/> (Dashboard / Api sources, possibly persistent). The default
/// composite ships in <c>UKBatch.Core</c>; storage adapters may substitute an end-to-end backed
/// implementation in v0.2.0+.
/// </summary>
/// <remarks>
/// <para><b>Code-wins-on-collision:</b> when a Code-source batch and a Store batch share the same
/// Name (and Source filter does not disambiguate the resolution call), the Code-source batch wins.
/// This matches the <see cref="Batches.IBatchDefinitionLookup"/> contract and avoids unstable
/// route resolution under hot-reload.</para>
/// <para><b>Pagination:</b> all listing calls take a <see cref="BatchCatalogQuery"/> envelope with
/// offset/limit semantics. <see cref="ListAsync"/> returns a <see cref="BatchCatalogPage"/> with
/// the total count (across all source filters that match) so the API can render paged UIs.</para>
/// </remarks>
public interface IBatchCatalogService
{
    /// <summary>Returns the definition by id across every source, or <c>null</c>.</summary>
    Task<BatchDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken);

    /// <summary>
    /// Returns the definition by name. Three-rule source contract (consumed by v0.2+ adapter authors):
    /// <list type="number">
    ///   <item>When <paramref name="source"/> is <c>null</c>, the implementation MUST query
    ///         <see cref="Batches.IBatchDefinitionLookup"/> (Code) FIRST,
    ///         then any persisted source(s) in implementation-defined order. The Code-wins-on-collision
    ///         rule applies — if both Code and Store contain a definition with the same Name, the
    ///         Code-source instance is returned.</item>
    ///   <item>When <paramref name="source"/> is <see cref="BatchSource.Code"/>, the implementation
    ///         MUST NOT touch persistent storage. Only the in-process
    ///         <see cref="Batches.IBatchDefinitionLookup"/> is consulted.</item>
    ///   <item>When <paramref name="source"/> is <see cref="BatchSource.Dashboard"/> or
    ///         <see cref="BatchSource.Api"/>, the implementation MUST NOT consult the Code lookup —
    ///         only the persistent <see cref="IBatchDefinitionStore"/> for the matching source.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// v0.2.0 adapters that implement <see cref="IBatchCatalogService"/> end-to-end MUST preserve
    /// the rule set.
    /// </remarks>
    Task<BatchDefinition?> GetByNameAsync(string name, BatchSource? source, CancellationToken cancellationToken);

    /// <summary>
    /// Paged listing. Filters by source (null = all), free-text on Name (null = no filter),
    /// pagination via Offset/Limit. Ordering: by Name ascending (stable, deterministic for paging).
    /// </summary>
    Task<BatchCatalogPage> ListAsync(BatchCatalogQuery query, CancellationToken cancellationToken);

    /// <summary>Total count under the same filter; useful when paginating clients want TotalCount.</summary>
    Task<long> CountAsync(BatchCatalogQuery query, CancellationToken cancellationToken);
}
