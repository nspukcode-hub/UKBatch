using UKBatch.Abstractions.Models;

namespace UKBatch.Api.Jobs;

/// <summary>
/// Wire DTO for <see cref="JobDefinition"/>. Public surface decoupled from the internal
/// Abstractions model so we never accidentally break a REST consumer by adding a field to
/// <see cref="JobDefinition"/>.
/// </summary>
/// <remarks>
/// NTH5: internal-only diagnostic fields (<c>ImplementationTypeName</c>, <c>SourceService</c>)
/// are intentionally omitted from the wire DTO to keep the public surface stable across
/// runtime/adapter substitution.
/// </remarks>
public sealed record class JobDefinitionDto
{
    /// <summary>Job name (unique within the process).</summary>
    public required string Name { get; init; }

    /// <summary><c>true</c> when the implementation is <c>IPartitionedJob&lt;T&gt;</c>.</summary>
    public required bool IsPartitioned { get; init; }

    /// <summary>Optional cron expression (Cronos grammar) when the job is scheduler-armed.</summary>
    public string? Schedule { get; init; }

    /// <summary>Maximum retry attempts on per-execution failure.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Timeout in seconds; 0 = no timeout.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>Partition worker count for partitioned jobs (0 for non-partitioned).</summary>
    public int PartitionWorkerCount { get; init; }

    /// <summary>Default parameters merged into every trigger.</summary>
    public required IReadOnlyDictionary<string, object?> DefaultParameters { get; init; }

    /// <summary>User-supplied tags for grouping / filtering.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Maps from a <see cref="JobDefinition"/> to the wire DTO.</summary>
    public static JobDefinitionDto FromModel(JobDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new JobDefinitionDto
        {
            Name = def.Name,
            IsPartitioned = def.IsPartitioned,
            Schedule = def.Schedule,
            MaxRetries = def.MaxRetries,
            TimeoutSeconds = def.TimeoutSeconds,
            PartitionWorkerCount = def.PartitionWorkerCount,
            DefaultParameters = def.DefaultParameters,
            Tags = def.Tags,
        };
    }
}
