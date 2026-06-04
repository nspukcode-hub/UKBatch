using System.Globalization;
using Microsoft.EntityFrameworkCore;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;   // reuse Core's public exceptions (no duplication)
using UKBatch.Storage.EntityFrameworkCore.Mapping;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// EF Core implementation of <see cref="IBatchDefinitionStore"/> with semantic parity to
/// <c>InMemoryBatchDefinitionStore</c>: whitespace asymmetry, stable paging order,
/// optimistic concurrency via the <c>Version</c> token, named-constraint duplicate disambiguation, and
/// the SAME Core exceptions so REST ProblemDetails mapping works unchanged. Per-op pooled
/// context.
/// </summary>
internal sealed class EfBatchDefinitionStore : IBatchDefinitionStore
{
    private readonly IDbContextFactory<UKBatchDbContext> _factory;

    public EfBatchDefinitionStore(IDbContextFactory<UKBatchDbContext> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factory = factory;
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition> CreateAsync(BatchDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);   // parity with InMemory (outside any tx)

        var created = definition with { Version = 1 };
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        db.BatchDefinitions.Add(BatchDefinitionMapper.ToEntity(created));
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsSourceNameViolation(ex))
        {
            throw new BatchDefinitionDuplicateNameException(
                $"BatchDefinition Name '{created.Name}' already exists in source {created.Source}.", ex)
            {
                Name = created.Name,
                BatchSource = created.Source,
            };
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsUniqueViolation(ex))
        {
            // PK (id) collision — a generic programmer error, NOT the typed duplicate-NAME exception.
            throw new InvalidOperationException($"BatchDefinition with id {created.Id} already exists.", ex);
        }
        return created;
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition> UpdateAsync(BatchDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchDefinitions
            .FirstOrDefaultAsync(e => e.Id == definition.Id, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            throw new BatchDefinitionNotFoundException($"BatchDefinition {definition.Id} not found.")
            {
                BatchDefinitionId = definition.Id,
            };
        }

        // Force the EF WHERE predicate to use the caller's claimed version so a stale caller
        // (caller.Version < store.Version) produces a DbUpdateConcurrencyException.
        db.Entry(entity).Property(e => e.Version).OriginalValue = definition.Version;

        BatchDefinitionMapper.CopyEditableFields(definition, entity);
        entity.Version = definition.Version + 1;   // mirror InMemory

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)   // concurrency-conflict mapping site
        {
            // Re-read StoreVersion on a FRESH context — the conflicted context's change-tracker
            // is poisoned. If the row is gone (deleted concurrently), StoreVersion = null.
            int? storeVersion;
            await using (var reread = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
            {
                storeVersion = await reread.BatchDefinitions
                    .AsNoTracking()
                    .Where(e => e.Id == definition.Id)
                    .Select(e => (int?)e.Version)
                    .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            }

            var storeVersionText = storeVersion?.ToString(CultureInfo.InvariantCulture) ?? "(deleted)";
            throw new BatchConcurrencyConflictException(
                $"Optimistic concurrency conflict on {definition.Id}: store version {storeVersionText} != caller version {definition.Version.ToString(CultureInfo.InvariantCulture)}.", ex)
            {
                BatchDefinitionId = definition.Id,
                StoreVersion = storeVersion,
                CallerVersion = definition.Version,
            };
        }
        catch (DbUpdateException ex) when (DbExceptionClassifier.IsSourceNameViolation(ex))   // rename-to-existing-name
        {
            throw new BatchDefinitionDuplicateNameException(
                $"Cannot rename to existing name '{definition.Name}' in source {definition.Source}.", ex)
            {
                Name = definition.Name,
                BatchSource = definition.Source,
            };
        }
        return BatchDefinitionMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task DeleteAsync(string batchDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchDefinitions
            .FirstOrDefaultAsync(e => e.Id == batchDefinitionId, cancellationToken).ConfigureAwait(false);
        if (entity is null)
        {
            return;   // idempotent — silent if absent
        }
        db.BatchDefinitions.Remove(entity);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition?> GetAsync(string batchDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == batchDefinitionId, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : BatchDefinitionMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<BatchDefinition?> GetByNameAsync(string name, BatchSource source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        // Whitespace-only returns null at the lookup boundary (asymmetry mirrors InMemory / IBatchDefinitionLookup).
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var entity = await db.BatchDefinitions
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Source == source && e.Name == name, cancellationToken).ConfigureAwait(false);
        return entity is null ? null : BatchDefinitionMapper.ToModel(entity);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<BatchDefinition>> ListAsync(BatchSource source, int offset, int limit, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        var page = await db.BatchDefinitions
            .AsNoTracking()
            .Where(e => e.Source == source)
            .OrderBy(e => e.Id)   // UUIDv7 "N" hex ⇒ ASCII ordinal == default collation
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return page.Select(BatchDefinitionMapper.ToModel).ToList();
    }

    /// <inheritdoc/>
    public async Task<long> CountAsync(BatchSource source, CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        return await db.BatchDefinitions.LongCountAsync(e => e.Source == source, cancellationToken).ConfigureAwait(false);
    }
}
