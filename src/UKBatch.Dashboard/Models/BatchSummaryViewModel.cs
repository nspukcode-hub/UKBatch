using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;

namespace UKBatch.Dashboard.Models;

/// <summary>View model adapter from <see cref="BatchDefinitionDto"/> for the Batches catalog row.</summary>
public sealed record class BatchSummaryViewModel
{
    /// <summary>Batch definition id.</summary>
    public required string Id { get; init; }

    /// <summary>Display name (unique within source).</summary>
    public required string Name { get; init; }

    /// <summary>Source — Code / Dashboard / Api.</summary>
    public required BatchSource Source { get; init; }

    /// <summary>Optional cron expression.</summary>
    public string? Schedule { get; init; }

    /// <summary>Number of top-level steps.</summary>
    public required int StepCount { get; init; }

    /// <summary>Failure policy: StopOnFirstFailure / RunOnFailureSteps / Compensate.</summary>
    public required BatchFailurePolicy FailurePolicy { get; init; }

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Identity that created the definition.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Maps from a wire <see cref="BatchDefinitionDto"/> to the view model.</summary>
    public static BatchSummaryViewModel FromDto(BatchDefinitionDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new BatchSummaryViewModel
        {
            Id = dto.Id,
            Name = dto.Name,
            Source = dto.Source,
            Schedule = dto.Schedule,
            StepCount = dto.Steps.Count,
            FailurePolicy = dto.FailurePolicy,
            CreatedAtUtc = dto.CreatedAtUtc,
            CreatedBy = dto.CreatedBy,
        };
    }
}
