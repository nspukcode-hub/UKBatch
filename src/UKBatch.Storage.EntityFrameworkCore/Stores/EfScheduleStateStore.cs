using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IScheduleStateStore"/> over the <c>ScheduleStates</c> table. One
/// short-lived pooled context per public method. No watch fan-out (watermarks have no change feed).
/// </summary>
internal sealed class EfScheduleStateStore : IScheduleStateStore
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;

    public EfScheduleStateStore(IDbContextFactory<UKBatchDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> GetAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.ScheduleStates.AsNoTracking()
            .ToDictionaryAsync(e => e.BatchDefinitionId, e => e.LastFiredOccurrenceUtc, StringComparer.Ordinal, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Monotonic on purpose: the watermark only ever moves forward. Writers can race — e.g. in a
    /// shared-database multi-node deployment two schedulers may both catch up the same definition at
    /// startup. The advance is a single atomic <c>UPDATE … WHERE LastFiredOccurrenceUtc &lt; @occurrence</c>
    /// rather than a read-modify-write, so a later-committing OLDER occurrence can never regress a newer
    /// one. The first fire inserts the row; a lost insert race (a unique violation, detected with the same
    /// classifier the run store uses) is resolved by re-running that same atomic advance.
    /// </remarks>
    public async Task RecordFiredAsync(string batchDefinitionId, DateTimeOffset occurrenceUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        if (await TryAdvanceAsync(db, batchDefinitionId, occurrenceUtc, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        // Zero rows advanced: the row is either absent (first fire) or already at/after this occurrence.
        // Insert the absent case; if a concurrent writer inserted first the unique violation is benign —
        // re-run the same atomic advance against the now-present row (a no-op if it already holds a newer one).
        db.ScheduleStates.Add(new ScheduleStateEntity
        {
            BatchDefinitionId = batchDefinitionId,
            LastFiredOccurrenceUtc = occurrenceUtc,
        });
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            await using var retry = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
            await TryAdvanceAsync(retry, batchDefinitionId, occurrenceUtc, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Atomically advances the watermark to <paramref name="occurrenceUtc"/> if the stored value is
    /// strictly older, in one statement. Returns <c>true</c> if a row was advanced, <c>false</c> if none
    /// matched (the row is absent or already at/after the occurrence).
    /// </summary>
    private static async Task<bool> TryAdvanceAsync(
        UKBatchDbContext db, string batchDefinitionId, DateTimeOffset occurrenceUtc, CancellationToken cancellationToken)
    {
        var advanced = await db.ScheduleStates
            .Where(e => e.BatchDefinitionId == batchDefinitionId && e.LastFiredOccurrenceUtc < occurrenceUtc)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.LastFiredOccurrenceUtc, occurrenceUtc), cancellationToken)
            .ConfigureAwait(false);
        return advanced > 0;
    }
}
