using UKBatch.Abstractions.Models;

namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for <see cref="BatchRun"/>. One JSON column
/// (<see cref="ForwardedState"/>); the rest are scalars. <see cref="Status"/> stores as a nullable string
/// (enum→string). No concurrency token: a run is created once and completed once (plus per-step cursor /
/// forwarded-state updates by a single run owner).
/// </summary>
internal sealed class BatchRunEntity
{
    public string BatchId { get; set; } = default!;            // PK, UUIDv7 ("N", 32 chars)
    public string BatchDefinitionId { get; set; } = default!;
    public string BatchName { get; set; } = default!;
    public JobStatus? Status { get; set; }                     // nullable enum→string; null == running
    public string? TriggeredBy { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public int? CurrentStepIndex { get; set; }                 // resume cursor; null == no cursor recorded
    public int? CompensationStepIndex { get; set; }            // reverse-unwind cursor; null == never entered compensation
    public string? RetryOfBatchId { get; set; }                // original run id when created by retry; null == normal trigger
    public int StepCount { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
    public IReadOnlyDictionary<string, object?>? ForwardedState { get; set; }   // JSON column (nullable; durable resume payload)
}
