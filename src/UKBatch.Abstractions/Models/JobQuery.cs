namespace UKBatch.Abstractions.Models;

/// <summary>Filter and pagination criteria for <see cref="Storage.IJobExecutionReader.QueryAsync"/>.</summary>
public sealed record class JobQuery
{
    /// <summary>Filter to specific statuses; <c>null</c> or empty means any status.</summary>
    public IReadOnlyList<JobStatus>? Statuses { get; init; }

    /// <summary>Filter to a specific job name; <c>null</c> means any.</summary>
    public string? JobName { get; init; }

    /// <summary>Filter to a specific batch run; <c>null</c> means any.</summary>
    public string? BatchId { get; init; }

    /// <summary>
    /// Filter to executions whose <see cref="JobExecution.BatchDefinitionId"/> matches; <c>null</c>
    /// means any definition (or standalone jobs). Empty string is treated as "no filter applied"
    /// at the adapter layer (consistent with other string filter fields).
    /// </summary>
    /// <remarks>
    /// Standalone jobs (triggered via <c>IJobRunner.TriggerAsync</c>, not
    /// <c>TriggerBatchAsync</c>) have <see cref="JobExecution.BatchDefinitionId"/> null and will
    /// NEVER match a non-null filter.
    /// </remarks>
    public string? BatchDefinitionId { get; init; }

    /// <summary>Inclusive lower bound on <see cref="JobExecution.EnqueuedAtUtc"/>; <c>null</c> means no lower bound.</summary>
    public DateTimeOffset? FromUtc { get; init; }

    /// <summary>Exclusive upper bound on <see cref="JobExecution.EnqueuedAtUtc"/>; <c>null</c> means no upper bound.</summary>
    public DateTimeOffset? ToUtc { get; init; }

    /// <summary>Filter to a specific worker (worker mode); <c>null</c> means any.</summary>
    public string? WorkerName { get; init; }

    /// <summary>
    /// Free-text search applied to <see cref="JobExecution.LastError"/> and the job name.
    /// Case-insensitive substring match per adapter; adapters MAY optimise via full-text indices.
    /// </summary>
    public string? SearchText { get; init; }

    /// <summary>Page offset (0-based). Default 0.</summary>
    public int Offset { get; init; }

    /// <summary>Page size; adapter-defined max (typically 1000). Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Sort direction on <see cref="JobExecution.EnqueuedAtUtc"/>. Default descending (newest first).</summary>
    public bool DescendingByEnqueuedAt { get; init; } = true;
}
