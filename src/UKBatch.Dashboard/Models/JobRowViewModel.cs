using UKBatch.Api.Jobs;

namespace UKBatch.Dashboard.Models;

/// <summary>View model adapter from <see cref="JobDefinitionDto"/> for the Jobs catalog row.</summary>
public sealed record class JobRowViewModel
{
    /// <summary>Job name (unique within the process).</summary>
    public required string Name { get; init; }

    /// <summary><c>true</c> when the implementation is <c>IPartitionedJob&lt;T&gt;</c>.</summary>
    public required bool IsPartitioned { get; init; }

    /// <summary>Optional cron expression when the job is scheduler-armed.</summary>
    public string? Schedule { get; init; }

    /// <summary>Maximum retry attempts on per-execution failure.</summary>
    public required int MaxRetries { get; init; }

    /// <summary>Timeout in seconds; <c>0</c> means no timeout.</summary>
    public required int TimeoutSeconds { get; init; }

    /// <summary>User-supplied tags for grouping / filtering.</summary>
    public required IReadOnlyList<string> Tags { get; init; }

    /// <summary>Maps from a wire <see cref="JobDefinitionDto"/> to the view model.</summary>
    public static JobRowViewModel FromDto(JobDefinitionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new JobRowViewModel
        {
            Name = dto.Name,
            IsPartitioned = dto.IsPartitioned,
            Schedule = dto.Schedule,
            MaxRetries = dto.MaxRetries,
            TimeoutSeconds = dto.TimeoutSeconds,
            Tags = dto.Tags,
        };
    }
}
