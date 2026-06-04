using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage;

/// <summary>
/// Default <see cref="IBatchCatalogService"/> composing the in-process
/// <see cref="IBatchDefinitionLookup"/> (Code source) with the persistent
/// <see cref="IBatchDefinitionStore"/> (Dashboard / Api sources). v0.2.0 EF / Redis adapters
/// may replace this entirely with an end-to-end backed implementation.
/// </summary>
internal sealed class BatchCatalogService : IBatchCatalogService
{
    private readonly IBatchDefinitionLookup _codeLookup;
    private readonly IBatchDefinitionStore _store;

    /// <summary>Constructs the composite.</summary>
    public BatchCatalogService(IBatchDefinitionLookup codeLookup, IBatchDefinitionStore store)
    {
        ArgumentNullException.ThrowIfNull(codeLookup);
        ArgumentNullException.ThrowIfNull(store);
        _codeLookup = codeLookup;
        _store = store;
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition?> GetByIdAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        // Code-first.
        var fromCode = _codeLookup.TryGetById(id);
        if (fromCode is not null) return fromCode;
        return await _store.GetAsync(id, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition?> GetByNameAsync(string name, BatchSource? source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);

        if (source is null or BatchSource.Code)
        {
            var fromCode = _codeLookup.TryGetByName(name);
            if (fromCode is not null) return fromCode;
            // Rule: source=Code MUST NOT touch persistent storage.
            if (source == BatchSource.Code) return null;
        }
        if (source is null or BatchSource.Dashboard)
        {
            var fromDash = await _store.GetByNameAsync(name, BatchSource.Dashboard, cancellationToken).ConfigureAwait(false);
            if (fromDash is not null) return fromDash;
            // Rule: source=Dashboard MUST NOT consult the Code lookup.
            if (source == BatchSource.Dashboard) return null;
        }
        if (source is null or BatchSource.Api)
        {
            var fromApi = await _store.GetByNameAsync(name, BatchSource.Api, cancellationToken).ConfigureAwait(false);
            if (fromApi is not null) return fromApi;
        }
        return null;
    }

    /// <inheritdoc/>
    public async Task<BatchCatalogPage> ListAsync(BatchCatalogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var codeAll = (query.Source is null or BatchSource.Code) ? _codeLookup.All() : (IReadOnlyList<BatchDefinition>)Array.Empty<BatchDefinition>();
        var dashAll = (query.Source is null or BatchSource.Dashboard)
            ? await _store.ListAsync(BatchSource.Dashboard, 0, int.MaxValue, cancellationToken).ConfigureAwait(false)
            : Array.Empty<BatchDefinition>();
        var apiAll = (query.Source is null or BatchSource.Api)
            ? await _store.ListAsync(BatchSource.Api, 0, int.MaxValue, cancellationToken).ConfigureAwait(false)
            : Array.Empty<BatchDefinition>();

        // Code-wins-on-collision: dedupe by Name where Code overlaps any other source.
        var codeNames = new HashSet<string>(codeAll.Select(d => d.Name), StringComparer.Ordinal);
        IEnumerable<BatchDefinition> merged = codeAll
            .Concat(dashAll.Where(d => !codeNames.Contains(d.Name)))
            .Concat(apiAll.Where(d => !codeNames.Contains(d.Name)));

        if (query.NameContains is { } needle)
        {
            merged = merged.Where(d => d.Name.Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
        var ordered = merged.OrderBy(d => d.Name, StringComparer.Ordinal).ToList();
        var total = (long)ordered.Count;
        var items = ordered
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Max(0, query.Limit))
            .ToList();
        return new BatchCatalogPage
        {
            Items = items,
            TotalCount = total,
            Offset = query.Offset,
            Limit = query.Limit,
        };
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(BatchCatalogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        // Plain async/await — NOT ContinueWith + t.Result (avoids sync-over-async).
        var page = await ListAsync(query with { Offset = 0, Limit = int.MaxValue }, cancellationToken).ConfigureAwait(false);
        return page.TotalCount;
    }
}
