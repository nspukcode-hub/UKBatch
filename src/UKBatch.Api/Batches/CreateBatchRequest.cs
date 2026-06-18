using UKBatch.Abstractions.Batches;

namespace UKBatch.Api.Batches;

/// <summary>Body for <c>POST /batches</c>. Source MUST be <c>Dashboard</c> or <c>Api</c>.</summary>
public sealed record class CreateBatchRequest
{
    /// <summary>Display name (unique within source).</summary>
    public required string Name { get; init; }

    /// <summary>Source — Dashboard or Api only; Code rejected with 400.</summary>
    public required BatchSource Source { get; init; }

    /// <summary>Optional cron expression for scheduler-armed batches.</summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Optional per-batch window for catching up a single missed scheduled fire on restart. Must be
    /// non-negative when set; ignored when <see cref="Schedule"/> is null. Requires the EF storage adapter.
    /// </summary>
    public TimeSpan? ScheduleCatchUpWindow { get; init; }

    /// <summary>Step list (Abstractions type used directly).</summary>
    public required IReadOnlyList<BatchStep> Steps { get; init; }

    /// <summary>Failure policy.</summary>
    public required BatchFailurePolicy FailurePolicy { get; init; }

    /// <summary>Steps to run on failure when policy = RunOnFailureSteps / Compensate.</summary>
    public IReadOnlyList<BatchStep> OnFailureSteps { get; init; } = [];

    /// <summary>Optional identity to attribute the creation to.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Optional opaque metadata at creation; usually <c>null</c> — layout hints (key:
    /// <c>"dashboard.layoutHints"</c>) are set later after the first drag in the interactive DAG view.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
