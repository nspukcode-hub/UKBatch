using UKBatch.Abstractions.Models;

namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for <see cref="JobExecution"/>. Field-for-field with the
/// Abstractions record; the change-tracker needs settable properties. Mapped via
/// <c>JobExecutionMapper</c> (pure static).
/// </summary>
internal sealed class JobExecutionEntity
{
    public string ExecutionId { get; set; } = default!;     // PK, UUIDv7 ("N" format, 32 chars)
    public string JobName { get; set; } = default!;
    public string? BatchId { get; set; }
    public string? BatchStepId { get; set; }
    public string? BatchDefinitionId { get; set; }          // MUST round-trip
    public JobStatus Status { get; set; }                   // stored as string (.HasConversion<string>())
    public IReadOnlyDictionary<string, object?> Parameters { get; set; } = default!;  // JSON column
    public DateTimeOffset EnqueuedAtUtc { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int AttemptNumber { get; set; }
    public int MaxRetries { get; set; }
    public string? LastError { get; set; }
    public long Processed { get; set; }
    public long Failed { get; set; }
    public long? Total { get; set; }
    public string? TriggeredBy { get; set; }
    public string? WorkerName { get; set; }
}
