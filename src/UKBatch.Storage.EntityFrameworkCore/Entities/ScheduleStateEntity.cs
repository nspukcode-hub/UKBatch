namespace UKBatch.Storage.EntityFrameworkCore.Entities;

/// <summary>
/// Mutable, EF-owned persistence shape for a batch-schedule watermark: the last cron occurrence a
/// scheduled batch fired for. One row per definition (the definition id is the PK). No JSON column —
/// both fields are scalars; no concurrency token (the store writes monotonically).
/// </summary>
internal sealed class ScheduleStateEntity
{
    public string BatchDefinitionId { get; set; } = default!;      // PK
    public DateTimeOffset LastFiredOccurrenceUtc { get; set; }
}
