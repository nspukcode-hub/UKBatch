using UKBatch.Abstractions.Batches;

namespace UKBatch.Api.Batches;

/// <summary>
/// Wire DTO for <see cref="BatchDefinition"/>. Top-level body decoupled from the Abstractions
/// model. <see cref="Steps"/> and <see cref="OnFailureSteps"/>
/// use <see cref="BatchStep"/> directly (no DTO mirror for the recursive step shape).
/// </summary>
public sealed record class BatchDefinitionDto
{
    /// <summary>Batch definition id (UUIDv7 or caller-supplied).</summary>
    public required string Id { get; init; }

    /// <summary>Display name (unique within source).</summary>
    public required string Name { get; init; }

    /// <summary>Source — Code / Dashboard / Api.</summary>
    public required BatchSource Source { get; init; }

    /// <summary>Optional cron expression when the batch is scheduler-armed.</summary>
    public string? Schedule { get; init; }

    /// <summary>Optional per-batch window for catching up a single missed scheduled fire on restart (EF storage only).</summary>
    public TimeSpan? ScheduleCatchUpWindow { get; init; }

    /// <summary>Steps in execution order (Abstractions type used directly).</summary>
    public required IReadOnlyList<BatchStep> Steps { get; init; }

    /// <summary>Failure policy: StopOnFirstFailure / RunOnFailureSteps / Compensate.</summary>
    public required BatchFailurePolicy FailurePolicy { get; init; }

    /// <summary>Steps to run on batch failure (when policy = RunOnFailureSteps / Compensate).</summary>
    public IReadOnlyList<BatchStep> OnFailureSteps { get; init; } = [];

    /// <summary>UTC instant the definition was created.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Identity that created the definition.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>Optimistic concurrency version (Store-source only; 0 for Code).</summary>
    public required int Version { get; init; }

    /// <summary>
    /// Storage-adapter opaque metadata (round-tripped verbatim). v0.1 — dashboard layout hints
    /// (key: <c>"dashboard.layoutHints"</c>); reserved for future per-batch annotations.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }

    /// <summary>Maps from a <see cref="BatchDefinition"/> to the wire DTO.</summary>
    public static BatchDefinitionDto FromModel(BatchDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new BatchDefinitionDto
        {
            Id = def.Id,
            Name = def.Name,
            Source = def.Source,
            Schedule = def.Schedule,
            ScheduleCatchUpWindow = def.ScheduleCatchUpWindow,
            Steps = def.Steps,
            FailurePolicy = def.FailurePolicy,
            OnFailureSteps = def.OnFailureSteps,
            CreatedAtUtc = def.CreatedAtUtc,
            CreatedBy = def.CreatedBy,
            Version = def.Version,
            Metadata = def.Metadata,
        };
    }
}
