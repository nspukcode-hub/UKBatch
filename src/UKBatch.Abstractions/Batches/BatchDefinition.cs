namespace UKBatch.Abstractions.Batches;

/// <summary>
/// Immutable definition of a batch workflow. Persisted for dashboard-created batches
/// (<see cref="BatchSource.Dashboard"/>, <see cref="BatchSource.Api"/>); in-memory for code-defined
/// batches (<see cref="BatchSource.Code"/>).
/// </summary>
public sealed record class BatchDefinition
{
    /// <summary>Unique identifier (GUID or slug). Caller-supplied; the store accepts the value as-is.</summary>
    public required string Id { get; init; }

    /// <summary>Display name; unique within a <see cref="Source"/>.</summary>
    public required string Name { get; init; }

    /// <summary>Origin of the definition.</summary>
    public required BatchSource Source { get; init; }

    /// <summary>Cron expression for scheduled execution; <c>null</c> for trigger-only.</summary>
    public string? Schedule { get; init; }

    /// <summary>
    /// Whether the <see cref="Schedule"/> is active. <c>true</c> (the default) arms the cron; <c>false</c>
    /// suspends firing WITHOUT removing the cron expression, so an operator can pause and later resume the
    /// schedule unchanged. Persisted, so a paused schedule stays paused across restarts. Ignored when
    /// <see cref="Schedule"/> is <c>null</c> (a trigger-only batch never arms regardless of this flag).
    /// </summary>
    public bool ScheduleEnabled { get; init; } = true;

    /// <summary>
    /// Per-batch window for catching up a single scheduled fire that was missed while the process was
    /// down. <c>null</c> or <see cref="System.TimeSpan.Zero"/> means no catch-up — a missed fire is
    /// simply skipped (the default). When set, on restart the most recent occurrence missed within this
    /// window is replayed exactly once (coalesced — only the latest missed occurrence, never a burst),
    /// and an occurrence is never fired twice. Requires the EF storage adapter to persist the last-fire
    /// watermark; with in-memory storage it has no effect. Ignored when <see cref="Schedule"/> is
    /// <c>null</c>.
    /// </summary>
    public TimeSpan? ScheduleCatchUpWindow { get; init; }

    /// <summary>
    /// Ordered list of steps. Empty is valid only at creation time (a placeholder); invalid for execution
    /// — the runtime fails the launch with <see cref="InvalidOperationException"/> if a batch has no steps.
    /// </summary>
    public required IReadOnlyList<BatchStep> Steps { get; init; }

    /// <summary>Behaviour when a step fails irrecoverably.</summary>
    public required BatchFailurePolicy FailurePolicy { get; init; }

    /// <summary>
    /// Compensating steps executed when <see cref="FailurePolicy"/> is
    /// <see cref="BatchFailurePolicy.Compensate"/> and a step fails. Empty list means no compensation;
    /// in that case <see cref="BatchFailurePolicy.Compensate"/> degrades to
    /// <see cref="BatchFailurePolicy.StopOnFailure"/>.
    /// </summary>
    public IReadOnlyList<BatchStep> OnFailureSteps { get; init; } = [];

    /// <summary>UTC creation timestamp.</summary>
    public required DateTimeOffset CreatedAtUtc { get; init; }

    /// <summary>Identity that created the definition; <c>null</c> for code-defined batches.</summary>
    public string? CreatedBy { get; init; }

    /// <summary>
    /// Optimistic concurrency token; bumped on every update by
    /// <see cref="Storage.IBatchDefinitionStore.UpdateAsync"/>.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// Storage-adapter opaque metadata. Round-tripped verbatim by adapters; reserved for dashboard
    /// layout hints (key: <c>"dashboard.layoutHints"</c>) and future per-batch annotations.
    /// Consumers in v0.1 SHOULD NOT depend on keys here. Additive forward-compat
    /// (mirrors <see cref="BatchStep.Metadata"/>).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}
