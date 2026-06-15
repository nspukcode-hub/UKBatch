namespace UKBatch.Abstractions.Models;

/// <summary>Filter and pagination criteria for <see cref="Storage.IBatchRunStore.QueryAsync"/>.</summary>
public sealed record class BatchRunQuery
{
    /// <summary>
    /// Filter to runs of one batch DEFINITION; <c>null</c> or empty means any definition. Empty string is
    /// treated as "no filter applied" at the adapter layer (consistent with the string filter fields on
    /// <see cref="JobQuery"/>).
    /// </summary>
    public string? BatchDefinitionId { get; init; }

    /// <summary>
    /// Filter to specific terminal statuses; <c>null</c> or empty means any.
    /// <para>A NULL run status (in-progress) is matched ONLY when <see cref="IncludeRunning"/> is set — a
    /// status filter naturally excludes running runs because their status is null and cannot be in any
    /// status set. To surface in-progress runs through this filter, set <see cref="IncludeRunning"/> to
    /// <c>true</c>.</para>
    /// </summary>
    public IReadOnlyList<JobStatus>? Statuses { get; init; }

    /// <summary>
    /// When <c>true</c>, in-progress runs (Status == null) are INCLUDED in the result regardless of
    /// <see cref="Statuses"/>; when <c>false</c>, only runs whose terminal status passes the
    /// <see cref="Statuses"/> filter are returned, so a null-status run is excluded.
    /// </summary>
    /// <remarks>
    /// Chosen over a dedicated <c>RunningOnly</c> flag because a null status cannot live inside a
    /// <see cref="JobStatus"/> set: the only way to surface in-progress runs through a status-typed filter
    /// is an explicit boolean. With <see cref="Statuses"/> null/empty AND <see cref="IncludeRunning"/>
    /// <c>true</c> (the default), EVERY run — running and terminal — is returned.
    /// </remarks>
    public bool IncludeRunning { get; init; } = true;

    /// <summary>Page offset (0-based). Default 0.</summary>
    public int Offset { get; init; }

    /// <summary>Page size; adapter-defined max (the REST layer caps via <c>UKBatchOptions.MaxPageLimit</c>). Default 50.</summary>
    public int Limit { get; init; } = 50;

    /// <summary>Sort direction on <see cref="BatchRun.StartedAtUtc"/>. Default descending (newest first).</summary>
    public bool DescendingByStartedAt { get; init; } = true;
}
