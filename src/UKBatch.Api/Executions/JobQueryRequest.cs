using UKBatch.Abstractions.Models;

namespace UKBatch.Api.Executions;

/// <summary>
/// Body for <c>POST /executions/query</c>. Maps to <see cref="JobQuery"/> on the Core side.
/// POST is used because the query is rich (status array + UTC bounds + search text) and GET
/// would require encoding all in the query string.
/// </summary>
public sealed record class JobQueryRequest
{
    /// <summary>Status filter; <c>null</c> = all statuses.</summary>
    public IReadOnlyList<JobStatus>? Statuses { get; init; }

    /// <summary>Job-name filter; <c>null</c> = all jobs.</summary>
    public string? JobName { get; init; }

    /// <summary>Batch-id filter; <c>null</c> = all batches.</summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// Batch-definition-id filter (NOT batch run id); <c>null</c> = all definitions. Enables the
    /// dashboard "last N runs of this definition" navigation.
    /// </summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>From UTC bound (inclusive); <c>null</c> = no lower bound.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>To UTC bound (exclusive); <c>null</c> = no upper bound.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Worker-name filter; <c>null</c> = all workers.</summary>
    public string? WorkerName { get; init; }

    /// <summary>Free-text search (impl-defined; typically prefix on execution id or job name).</summary>
    public string? SearchText { get; init; }

    /// <summary>0-based page offset.</summary>
    public int Offset { get; init; }

    /// <summary>Page size; default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Descending by EnqueuedAt (most-recent first) when <c>true</c>.</summary>
    public bool DescendingByEnqueuedAt { get; init; } = true;

    /// <summary>Maps to the Abstractions <see cref="JobQuery"/>.</summary>
    public JobQuery ToQuery() => new()
    {
        Statuses = Statuses ?? Array.Empty<JobStatus>(),
        JobName = JobName,
        BatchId = BatchId,
        BatchDefinitionId = BatchDefinitionId,
        FromUtc = FromUtc,
        ToUtc = ToUtc,
        WorkerName = WorkerName,
        SearchText = SearchText,
        Offset = Offset,
        Limit = Limit,
        DescendingByEnqueuedAt = DescendingByEnqueuedAt,
    };
}
