using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Storage;

/// <summary>Paged result envelope for <see cref="IBatchCatalogService.ListAsync"/>.</summary>
public sealed record class BatchCatalogPage
{
    /// <summary>Definitions in the page; ordered by Name ascending.</summary>
    public required IReadOnlyList<BatchDefinition> Items { get; init; }

    /// <summary>Total count across all pages for the same filter; -1 if the impl declines to count.</summary>
    public required long TotalCount { get; init; }

    /// <summary>Echo of the request's <see cref="BatchCatalogQuery.Offset"/>.</summary>
    public required int Offset { get; init; }

    /// <summary>Echo of the request's <see cref="BatchCatalogQuery.Limit"/>.</summary>
    public required int Limit { get; init; }
}
