namespace UKBatch.Api.Common;

/// <summary>Generic paged envelope for REST list endpoints.</summary>
public sealed record class PageEnvelope<T>
{
    /// <summary>Items in the page (order is endpoint-defined).</summary>
    public required IReadOnlyList<T> Items { get; init; }

    /// <summary>Total count across all pages for the same query.</summary>
    public required long TotalCount { get; init; }

    /// <summary>Echo of the request's offset.</summary>
    public required int Offset { get; init; }

    /// <summary>Echo of the request's limit.</summary>
    public required int Limit { get; init; }
}
