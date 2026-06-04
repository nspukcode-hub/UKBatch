using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for <see cref="PersistedApprovalGate"/>. <see cref="Config"/>
/// is a JSON column. No concurrency token — gate records are single-writer per lifecycle event
/// (create → one decision).
/// </summary>
internal sealed class ApprovalGateEntity
{
    public string ApprovalId { get; set; } = default!;      // PK
    public string BatchId { get; set; } = default!;
    public string BatchStepId { get; set; } = default!;
    public string? BatchDefinitionId { get; set; }
    public ApprovalGateConfig Config { get; set; } = default!;   // JSON column
    public ApprovalRecordStatus Status { get; set; }            // string conversion
    public DateTimeOffset PendingSinceUtc { get; set; }
    public DateTimeOffset? DeadlineUtc { get; set; }
    public ApprovalRecordOutcome? Outcome { get; set; }         // string conversion (nullable)
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAtUtc { get; set; }
    public string? Note { get; set; }
}
