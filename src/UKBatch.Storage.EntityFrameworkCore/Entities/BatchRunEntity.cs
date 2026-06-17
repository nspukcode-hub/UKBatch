using UKBatch.Abstractions.Models;

namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for <see cref="BatchRun"/>. No JSON column — every field is a
/// scalar. <see cref="Status"/> stores as a nullable string (enum→string). No concurrency token: a run
/// is created once and completed once.
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
    public int StepCount { get; set; }
    public int Total { get; set; }
    public int Succeeded { get; set; }
    public int Failed { get; set; }
    public int Cancelled { get; set; }
}
