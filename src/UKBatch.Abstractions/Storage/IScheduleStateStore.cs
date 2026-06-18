namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Pluggable durable store for batch-schedule watermarks — the last cron occurrence a scheduled batch
/// was fired for, one per definition. Lets the batch scheduler replay a single missed occurrence after
/// a restart (per-batch opt-in via <c>BatchDefinition.ScheduleCatchUpWindow</c>) without ever firing the
/// same occurrence twice.
/// </summary>
/// <remarks>
/// <para>The only implementation is the EF adapter (durable). With in-memory storage there is no
/// implementation registered, so missed-fire catch-up is inactive — a fire missed while the process was
/// down is simply skipped, the same as before this surface existed. Implementations MUST be
/// thread-safe.</para>
/// <para><b>Monotonic (advance-only).</b> <see cref="RecordFiredAsync"/> ignores a write whose
/// occurrence is older than the stored value, so a concurrent rescan racing a catch-up fire can never
/// regress the watermark to the past (which would let the same occurrence be replayed). The store only
/// ever moves a definition's watermark forward.</para>
/// </remarks>
public interface IScheduleStateStore
{
    /// <summary>
    /// Returns every definition's last-fired watermark, keyed by batch-definition id. A definition with
    /// no recorded fire is simply absent from the dictionary.
    /// </summary>
    Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Records that <paramref name="batchDefinitionId"/> fired for the cron occurrence
    /// <paramref name="occurrenceUtc"/>. Monotonic: a write whose occurrence is not strictly newer than
    /// the stored value is a no-op, so the watermark only ever advances.
    /// </summary>
    Task RecordFiredAsync(string batchDefinitionId, DateTimeOffset occurrenceUtc, CancellationToken cancellationToken);
}
