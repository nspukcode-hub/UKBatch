using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Mapping;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IApprovalGateStore"/> — durable RECORD of approval gates over
/// the <c>ApprovalGates</c> table. CRUD with the per-op pooled context; <c>Config</c> is a
/// JSON column.
/// </summary>
internal sealed class EfApprovalGateStore : IApprovalGateStore
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;

    public EfApprovalGateStore(IDbContextFactory<UKBatchDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task SaveAsync(PersistedApprovalGate gate, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(gate);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var existing = await db.ApprovalGates
            .FirstOrDefaultAsync(e => e.ApprovalId == gate.ApprovalId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            db.ApprovalGates.Add(ApprovalGateMapper.ToEntity(gate));
        }
        else
        {
            ApprovalGateMapper.CopyInto(gate, existing);
        }
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<PersistedApprovalGate?> GetAsync(string approvalId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.ApprovalGates
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.ApprovalId == approvalId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : ApprovalGateMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PersistedApprovalGate>> ListPendingAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ApprovalGates
            .AsNoTracking()
            .Where(e => e.Status == ApprovalRecordStatus.Pending)
            .OrderBy(e => e.PendingSinceUtc).ThenBy(e => e.ApprovalId)   // stable
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ApprovalGateMapper.ToModel).ToList();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<PersistedApprovalGate>> ListByBatchAsync(string batchId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var rows = await db.ApprovalGates
            .AsNoTracking()
            .Where(e => e.BatchId == batchId)   // pending AND decided for this run
            .OrderBy(e => e.PendingSinceUtc).ThenBy(e => e.ApprovalId)   // stable
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(ApprovalGateMapper.ToModel).ToList();
    }

    /// <inheritdoc/>
    public async Task RecordOutcomeAsync(
        string approvalId,
        ApprovalRecordOutcome outcome,
        string decidedBy,
        DateTimeOffset decidedAtUtc,
        string? note,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(approvalId);
        ArgumentNullException.ThrowIfNull(decidedBy);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.ApprovalGates
            .FirstOrDefaultAsync(e => e.ApprovalId == approvalId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            // Direct-caller contract (e.g. dashboard approve of a truly-missing id → 404 via typed map).
            // ApprovalGateService downgrades this to a warn-log for its never-persisted-crash-orphan path.
            throw new InvalidOperationException($"Approval gate {approvalId} not found.");
        }
        if (entity.Status == ApprovalRecordStatus.Decided)
        {
            // Terminal outcomes are immutable — a second decision (duplicate approve, or operator vs reaper
            // race) must not overwrite the audit record. The first writer wins.
            throw new ApprovalAlreadyDecidedException(
                $"Approval gate {approvalId} is already decided ({entity.Outcome}); cannot overwrite.")
            {
                ApprovalId = approvalId,
                ExistingOutcome = entity.Outcome,
            };
        }
        entity.Status = ApprovalRecordStatus.Decided;
        entity.Outcome = outcome;
        entity.DecidedBy = decidedBy;
        entity.DecidedAtUtc = decidedAtUtc;
        entity.Note = note;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
