using UKBatch.Abstractions.Batches;

namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for <see cref="BatchDefinition"/>. <see cref="Steps"/> and
/// <see cref="OnFailureSteps"/> each serialize to ONE JSON column (recursion-safe for nested
/// <c>ParallelGroupData.Steps</c>). <see cref="Version"/> is the optimistic-concurrency token.
/// </summary>
internal sealed class BatchDefinitionEntity
{
    public string Id { get; set; } = default!;              // PK (caller-supplied; store accepts as-is)
    public string Name { get; set; } = default!;
    public BatchSource Source { get; set; }                 // string conversion
    public string? Schedule { get; set; }
    public IReadOnlyList<BatchStep> Steps { get; set; } = default!;          // JSON column
    public BatchFailurePolicy FailurePolicy { get; set; }   // string conversion
    public IReadOnlyList<BatchStep> OnFailureSteps { get; set; } = default!; // JSON column (default [])
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string? CreatedBy { get; set; }

    /// <summary>
    /// OPTIMISTIC CONCURRENCY TOKEN (provider-agnostic via <c>.IsConcurrencyToken()</c>). EF emits
    /// <c>WHERE Id = @id AND Version = @originalVersion</c> on update. <c>xmin</c> (PG-native) is the
    /// v0.2 alternative.
    /// </summary>
    public int Version { get; set; }

    /// <summary>
    /// Storage-adapter opaque metadata (round-tripped verbatim). Used by the dashboard for layout hints
    /// (key: <c>"dashboard.layoutHints"</c>). NULLABLE at the DB level for backward-compat with
    /// rows created before the AddBatchDefinitionMetadata migration; the mapper normalizes both
    /// directions (null → empty dict on write, empty → null on read).
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; set; }
}
