using UKBatch.Abstractions.Batches;

namespace UKBatch.Api.Batches;

/// <summary>Body for <c>PUT /batches/by-id/{id}</c>. Optimistic concurrency via <see cref="Version"/>.</summary>
public sealed record class UpdateBatchRequest
{
    /// <summary>Definition id (MUST match the route id).</summary>
    public required string Id { get; init; }

    /// <summary>Display name (MUST be unique within source after rename).</summary>
    public required string Name { get; init; }

    /// <summary>Source — Dashboard or Api only; Code rejected with 400.</summary>
    public required BatchSource Source { get; init; }

    /// <summary>Optional cron expression for scheduler-armed batches.</summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Whether the schedule is active; <c>false</c> means paused. Round-tripped so a definition edit
    /// preserves the pause state instead of silently re-enabling a paused schedule. Default <c>true</c>.
    /// </summary>
    public bool ScheduleEnabled { get; init; } = true;

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

    /// <summary>Optimistic concurrency token — MUST match the current store version.</summary>
    public required int Version { get; init; }

    /// <summary>
    /// Dashboard layout hints (key: <c>"dashboard.layoutHints"</c>) + future opaque per-batch
    /// metadata; round-tripped verbatim by storage. Send <c>null</c> to clear metadata entirely.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
