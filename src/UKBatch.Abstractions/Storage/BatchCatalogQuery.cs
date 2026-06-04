using UKBatch.Abstractions.Batches;

namespace UKBatch.Abstractions.Storage;

/// <summary>Filter + pagination criteria for <see cref="IBatchCatalogService.ListAsync"/>.</summary>
public sealed record class BatchCatalogQuery
{
    /// <summary>Filter to one source; <c>null</c> means all sources.</summary>
    public BatchSource? Source { get; init; }

    /// <summary>Case-insensitive substring filter on <see cref="BatchDefinition.Name"/>; <c>null</c> = no filter.</summary>
    public string? NameContains { get; init; }

    /// <summary>0-based page offset. Default 0.</summary>
    public int Offset { get; init; }

    /// <summary>Page size. Default 50; max enforced by REST layer (not by the catalog itself).</summary>
    public int Limit { get; init; } = 50;
}
