using System.Collections.Concurrent;
using UKBatch.Abstractions.Batches;

namespace UKBatch.Registry;

/// <summary>
/// Process-wide registry of code-defined <see cref="BatchDefinition"/> instances
/// (<see cref="BatchSource.Code"/>). Implements <see cref="IBatchDefinitionLookup"/>
/// as the public read-only seam. Mutation (<see cref="Register"/>) is internal —
/// only <c>UKBatchBuilder.Complete()</c> mutates this registry, exactly once, before
/// any consumer reads from it.
/// </summary>
internal sealed class BatchDefinitionRegistry : IBatchDefinitionLookup
{
    private readonly ConcurrentDictionary<string, BatchDefinition> _byId = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, BatchDefinition> _byName = new(StringComparer.Ordinal);
    // Registration-order list.
    // Guarded by its own monitor; only Register mutates, All() reads under the same lock.
    private readonly List<BatchDefinition> _orderedDefinitions = new();

    /// <summary>
    /// Registers a code-defined batch. Throws <see cref="InvalidOperationException"/> if either
    /// the id OR the name is already taken. Atomic-on-success: if name collision is detected
    /// after id insertion, the id is rolled back before throwing so the registry stays consistent.
    /// Post-rollback the registry is still functionally usable: a SUBSEQUENT successful
    /// <see cref="Register"/> call must observe the rolled-back slot as free.
    /// </summary>
    public void Register(BatchDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        if (!_byId.TryAdd(definition.Id, definition))
        {
            throw new InvalidOperationException($"Batch id '{definition.Id}' is already registered.");
        }
        if (!_byName.TryAdd(definition.Name, definition))
        {
            // Roll back the id insertion to keep the two dicts consistent.
            _byId.TryRemove(definition.Id, out _);
            throw new InvalidOperationException(
                $"Batch name '{definition.Name}' is already registered. " +
                "Code-defined batch names must be unique within the process.");
        }
        // Both dicts committed; append to ordered list under its own lock.
        lock (_orderedDefinitions) { _orderedDefinitions.Add(definition); }
    }

    // ===== IBatchDefinitionLookup =====

    /// <inheritdoc/>
    public BatchDefinition? TryGetByName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return _byName.TryGetValue(name, out var def) ? def : null;
    }

    /// <inheritdoc/>
    public BatchDefinition? TryGetById(string id)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        return _byId.TryGetValue(id, out var def) ? def : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<BatchDefinition> All()
    {
        // Defensive copy under the same lock that guards Register's append. The two
        // dictionaries are concurrent for lookup throughput; All()'s ordering invariant is
        // weaker (single-writer at host setup) so a plain lock is fine and avoids the
        // ConcurrentDictionary non-deterministic iteration order.
        lock (_orderedDefinitions) { return _orderedDefinitions.ToList(); }
    }
}
