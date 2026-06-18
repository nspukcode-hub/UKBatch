using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Mapping;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IBatchRunStore"/> over the <c>BatchRuns</c> table. One short-lived
/// pooled context per public method. No watch fan-out (runs have no change feed).
/// </summary>
internal sealed class EfBatchRunStore : IBatchRunStore
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;

    public EfBatchRunStore(IDbContextFactory<UKBatchDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task CreateAsync(BatchRun run, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrEmpty(run.BatchId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.BatchRuns.Add(BatchRunMapper.ToEntity(run));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            // Parity with the in-memory store's message on a primary-key collision.
            throw new InvalidOperationException($"Batch run {run.BatchId} already exists.", ex);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// May be called with <see cref="CancellationToken.None"/> from the runtime's completion finally.
    /// An <see cref="ObjectDisposedException"/> from a disposed pooled context factory at host shutdown
    /// is the caller's to swallow (it is, in the runtime's run-completion path).
    /// </remarks>
    public async Task CompleteAsync(
        string batchId, JobStatus terminalStatus, BatchRunCounts counts,
        DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchRuns
            .FirstOrDefaultAsync(e => e.BatchId == batchId, cancellationToken).ConfigureAwait(false);   // TRACKED
        if (entity is null)
        {
            // Absent run row (create may have failed) — no-op, mirroring the in-memory store. Completion
            // must not crash the runtime's fire-and-forget finally on a missing row.
            return;
        }
        entity.Status = terminalStatus;
        entity.Total = counts.Total;
        entity.Succeeded = counts.Succeeded;
        entity.Failed = counts.Failed;
        entity.Cancelled = counts.Cancelled;
        entity.CompletedAtUtc = completedAtUtc;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchRuns
            .FirstOrDefaultAsync(e => e.BatchId == batchId, cancellationToken).ConfigureAwait(false);   // TRACKED
        if (entity is null)
        {
            return;   // absent run — no-op, mirrors CompleteAsync
        }
        entity.CurrentStepIndex = nextStepIndex;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchRuns.AsNoTracking()
            .FirstOrDefaultAsync(e => e.BatchId == batchId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : BatchRunMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        IQueryable<Entities.BatchRunEntity> q = ApplyFilter(db.BatchRuns.AsNoTracking(), query);
        q = query.DescendingByStartedAt
            ? q.OrderByDescending(e => e.StartedAtUtc).ThenByDescending(e => e.BatchId)
            : q.OrderBy(e => e.StartedAtUtc).ThenBy(e => e.BatchId);
        var page = await q
            .Skip(Math.Max(0, query.Offset))
            .Take(Math.Max(0, query.Limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(BatchRunMapper.ToModel).ToList();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await ApplyFilter(db.BatchRuns.AsNoTracking(), query)
            .LongCountAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IQueryable<Entities.BatchRunEntity> ApplyFilter(IQueryable<Entities.BatchRunEntity> q, BatchRunQuery query)
    {
        if (!string.IsNullOrEmpty(query.BatchDefinitionId))
        {
            q = q.Where(e => e.BatchDefinitionId == query.BatchDefinitionId);
        }
        var statuses = query.Statuses;
        if (statuses is { Count: > 0 })
        {
            // Translatable predicate: a running run (Status == null) is included only when IncludeRunning;
            // a terminal run is included when its status is in the set. The null guard makes the
            // .Value access provably non-null at SQL-gen time on both providers.
            q = query.IncludeRunning
                ? q.Where(e => e.Status == null || statuses.Contains(e.Status.Value))
                : q.Where(e => e.Status != null && statuses.Contains(e.Status.Value));
        }
        else if (!query.IncludeRunning)
        {
            // No status set but running excluded → only terminal runs.
            q = q.Where(e => e.Status != null);
        }
        // else: no status set + IncludeRunning → no status predicate (all runs).
        return q;
    }
}
