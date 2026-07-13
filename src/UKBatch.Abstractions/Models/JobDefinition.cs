using UKBatch.Abstractions.Jobs;

namespace UKBatch.Abstractions.Models;

/// <summary>
/// Definition of a registered job at runtime. Built from <see cref="JobAttribute"/>, fluent
/// registration, or dashboard registration.
/// </summary>
public sealed record class JobDefinition
{
    /// <summary>Logical job name; unique within a runtime instance.</summary>
    public required string Name { get; init; }

    /// <summary>
    /// Assembly-qualified type name implementing <see cref="IJob"/> or <see cref="IPartitionedJob{TItem}"/>;
    /// <c>null</c> for cross-service jobs registered only as proxies on the orchestrator.
    /// </summary>
    public string? ImplementationTypeName { get; init; }

    /// <summary>True if the underlying type implements <see cref="IPartitionedJob{TItem}"/>.</summary>
    public required bool IsPartitioned { get; init; }

    /// <summary>Cron expression; <c>null</c> for trigger-only.</summary>
    public string? Schedule { get; init; }

    /// <summary>Maximum retry attempts excluding the initial attempt.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Wall-clock timeout in seconds. <c>0</c> means no timeout.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Partition worker count (partitioned jobs only); ignored otherwise.</summary>
    public int PartitionWorkerCount { get; init; }

    /// <summary>Per-item error policy (partitioned jobs only).</summary>
    public ItemErrorPolicy ItemErrorPolicy { get; init; }

    /// <summary>Static parameters applied at every dispatch unless overridden.</summary>
    public required IReadOnlyDictionary<string, object?> DefaultParameters { get; init; }

    /// <summary>Routing tags (worker mode). Empty list means no routing constraints.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>
    /// Parameters this job announced at registration via <c>WithParameter&lt;T&gt;</c>. Empty when none
    /// were declared. Announcement metadata — it drives the typed trigger form and per-job schema; it is
    /// NOT merged into <see cref="DefaultParameters"/> and never rejects an undeclared key.
    /// </summary>
    public IReadOnlyList<JobParameterDescriptor> DeclaredParameters { get; init; } = [];

    /// <summary>
    /// Originating service name for cross-service jobs (matches <see cref="Transport.JobMessage.SourceService"/>);
    /// <c>null</c> for local execution.
    /// </summary>
    public string? SourceService { get; init; }
}
