namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Declarative metadata for an <see cref="IJob"/> or <see cref="IPartitionedJob{TItem}"/>.
/// Consumed by attribute-based discovery during host startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class JobAttribute : Attribute
{
    /// <summary>
    /// Unique logical job name. If <c>null</c>, the discovery layer derives the name from the
    /// type's full name (namespace + type name).
    /// </summary>
    public string? Name { get; init; }

    /// <summary>
    /// Cron expression for scheduled execution; <c>null</c> means trigger-only (not scheduled).
    /// Grammar: standard 5-field or extended 6-field cron (seconds-prefix optional).
    /// The exact dialect is determined by the configured scheduler (default: Cronos).
    /// </summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Maximum retry attempts on failure, excluding the initial attempt. <c>null</c> means
    /// "inherit runtime default"; <c>0</c> means "explicitly no retry". Negative values are invalid.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Wall-clock timeout in seconds. <c>null</c> means "inherit runtime default"; <c>0</c> means
    /// "explicitly no timeout". Negative values are invalid.
    /// </summary>
    public int? TimeoutSeconds { get; init; }

    /// <summary>
    /// Routing tags used in worker mode to filter dispatch to specific workers.
    /// Example: <c>["region:eu", "tier:critical"]</c>. <c>null</c> means no routing constraints.
    /// </summary>
    public string[]? Tags { get; init; }
}
