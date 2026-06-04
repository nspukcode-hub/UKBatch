using System.Collections.Concurrent;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;

namespace UKBatch.Storage;

/// <summary>
/// In-memory <see cref="IBatchDefinitionStore"/>. Maintains a name index keyed by
/// <c>(source, name)</c> and serializes ALL mutating operations under a per-store write lock so
/// the two indexes (id + name-per-source) never observe a partial state.
/// </summary>
/// <remarks>
/// <para><b>Concurrency contract:</b> all mutating operations (<see cref="CreateAsync"/>,
/// <see cref="UpdateAsync"/>, <see cref="DeleteAsync"/>) are serialized via an internal write
/// lock; reads (<see cref="GetAsync"/>, <see cref="GetByNameAsync"/>, <see cref="ListAsync"/>,
/// <see cref="CountAsync"/>) are lock-free against the underlying <see cref="ConcurrentDictionary{TKey,TValue}"/>s.
/// Mutate-then-read from a different thread observes either the pre-mutation or post-mutation
/// state — never a partial state.</para>
/// </remarks>
public sealed class InMemoryBatchDefinitionStore : IBatchDefinitionStore
{
    private readonly ConcurrentDictionary<string, BatchDefinition> _byId = new(StringComparer.Ordinal);
    // Key shape: "{source}:{name}" ordinal. Source-scoped because name uniqueness is per-source.
    private readonly ConcurrentDictionary<string, BatchDefinition> _byNamePerSource = new(StringComparer.Ordinal);
    private readonly object _writeLock = new();

    /// <inheritdoc/>
    public Task<BatchDefinition> CreateAsync(BatchDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        // Fail-fast on bad input OUTSIDE the lock (whitespace is a programmer-time error at this boundary).
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        var created = definition with { Version = 1 };
        lock (_writeLock)
        {
            if (!_byId.TryAdd(created.Id, created))
            {
                throw new InvalidOperationException($"BatchDefinition with id {created.Id} already exists.");
            }
            var nameKey = NameKey(created.Source, created.Name);
            if (!_byNamePerSource.TryAdd(nameKey, created))
            {
                // Roll back the id insertion to keep the two dicts consistent.
                _byId.TryRemove(created.Id, out _);
                throw new BatchDefinitionDuplicateNameException(
                    $"BatchDefinition Name '{created.Name}' already exists in source {created.Source}.")
                {
                    Name = created.Name,
                    BatchSource = created.Source,
                };
            }
        }
        return Task.FromResult(created);
    }

    /// <inheritdoc/>
    public Task<BatchDefinition> UpdateAsync(BatchDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Name);

        BatchDefinition updated;
        lock (_writeLock)
        {
            if (!_byId.TryGetValue(definition.Id, out var existing))
            {
                throw new BatchDefinitionNotFoundException($"BatchDefinition {definition.Id} not found.")
                {
                    BatchDefinitionId = definition.Id,
                };
            }
            if (existing.Version != definition.Version)
            {
                throw new BatchConcurrencyConflictException(
                    $"Optimistic concurrency conflict on {definition.Id}: store version {existing.Version} != caller version {definition.Version}.")
                {
                    BatchDefinitionId = definition.Id,
                    StoreVersion = existing.Version,
                    CallerVersion = definition.Version,
                };
            }
            updated = definition with { Version = existing.Version + 1 };

            // Rename case: enforce name uniqueness in the new source slot before committing.
            var oldKey = NameKey(existing.Source, existing.Name);
            var newKey = NameKey(updated.Source, updated.Name);
            if (!string.Equals(oldKey, newKey, StringComparison.Ordinal))
            {
                if (!_byNamePerSource.TryAdd(newKey, updated))
                {
                    throw new BatchDefinitionDuplicateNameException(
                        $"Cannot rename to existing name '{updated.Name}' in source {updated.Source}.")
                    {
                        Name = updated.Name,
                        BatchSource = updated.Source,
                    };
                }
                _byNamePerSource.TryRemove(oldKey, out _);
            }
            else
            {
                // Same name slot — overwrite the value (in-place update of the existing entry).
                _byNamePerSource[newKey] = updated;
            }
            _byId[definition.Id] = updated;
        }
        return Task.FromResult(updated);
    }

    /// <inheritdoc/>
    public Task DeleteAsync(string batchDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        lock (_writeLock)
        {
            if (_byId.TryRemove(batchDefinitionId, out var removed))
            {
                _byNamePerSource.TryRemove(NameKey(removed.Source, removed.Name), out _);
            }
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<BatchDefinition?> GetAsync(string batchDefinitionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchDefinitionId);
        return Task.FromResult(_byId.TryGetValue(batchDefinitionId, out var def) ? def : null);
    }

    /// <inheritdoc/>
    public Task<BatchDefinition?> GetByNameAsync(string name, BatchSource source, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        // Whitespace-only returns null at the lookup boundary (asymmetry mirrors IBatchDefinitionLookup).
        if (string.IsNullOrWhiteSpace(name))
        {
            return Task.FromResult<BatchDefinition?>(null);
        }
        return Task.FromResult(_byNamePerSource.TryGetValue(NameKey(source, name), out var def) ? def : null);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<BatchDefinition>> ListAsync(BatchSource source, int offset, int limit, CancellationToken cancellationToken)
    {
        var page = _byId.Values
            .Where(d => d.Source == source)
            .OrderBy(d => d.Id, StringComparer.Ordinal)
            .Skip(Math.Max(0, offset))
            .Take(Math.Max(0, limit))
            .ToList();
        return Task.FromResult<IReadOnlyList<BatchDefinition>>(page);
    }

    /// <inheritdoc/>
    public Task<long> CountAsync(BatchSource source, CancellationToken cancellationToken)
    {
        var count = _byId.Values.LongCount(d => d.Source == source);
        return Task.FromResult(count);
    }

    private static string NameKey(BatchSource source, string name) => $"{source}:{name}";
}
