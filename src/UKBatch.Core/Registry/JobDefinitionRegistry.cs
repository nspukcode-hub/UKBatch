using System.Collections.Concurrent;
using Polly;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Registry;

/// <summary>
/// Process-wide registry of <see cref="JobDefinition"/> instances. Holds three sibling dicts keyed
/// by job name: the definition itself, the resolved implementation <see cref="Type"/> (for DI
/// scope resolution), and the cached <see cref="ResiliencePipeline"/> built by
/// <see cref="Discovery.JobDefinitionFactory"/> at registration time (no per-item allocation).
/// </summary>
/// <remarks>
/// Implements <see cref="IJobDefinitionLookup"/> as the public read-only seam — REST consumers
/// route through the interface rather than the concrete registry.
/// </remarks>
internal sealed class JobDefinitionRegistry : IJobDefinitionLookup
{
    private readonly ConcurrentDictionary<string, JobDefinition> _definitions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Type> _implementations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ResiliencePipeline> _itemRetryPipelines = new(StringComparer.Ordinal);

    // Registration-order list. Guarded by its own monitor; only Register mutates AFTER the
    // duplicate-name throw has already cleared, and All() reads under the same lock. The throw
    // at _definitions.TryAdd fires BEFORE any sibling writes (_implementations,
    // _itemRetryPipelines, _ordered), so no rollback is needed.
    private readonly List<JobDefinition> _ordered = new();

    /// <summary>Registers a definition; throws if the name is already taken.</summary>
    public void Register(JobDefinition definition, Type implementationType, ResiliencePipeline? itemRetryPipeline)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(implementationType);

        if (!_definitions.TryAdd(definition.Name, definition))
        {
            throw new InvalidOperationException($"Job '{definition.Name}' is already registered.");
        }
        _implementations[definition.Name] = implementationType;
        if (itemRetryPipeline is not null)
        {
            _itemRetryPipelines[definition.Name] = itemRetryPipeline;
        }
        // Append to the ordered list AFTER every dict write succeeds.
        lock (_ordered)
        {
            _ordered.Add(definition);
        }
    }

    /// <summary>Returns the implementation <see cref="Type"/>, or <c>null</c> if not registered.</summary>
    public Type? TryGetImplementationType(string jobName)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return _implementations.TryGetValue(jobName, out var type) ? type : null;
    }

    /// <summary>Returns the cached per-item retry pipeline (RetryThenContinue), or <c>null</c> if not configured.</summary>
    public ResiliencePipeline? TryGetItemRetryPipeline(string jobName)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return _itemRetryPipelines.TryGetValue(jobName, out var pipeline) ? pipeline : null;
    }

    // ===== IJobDefinitionLookup =====

    /// <inheritdoc/>
    public JobDefinition? TryGet(string jobName)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return _definitions.TryGetValue(jobName, out var def) ? def : null;
    }

    /// <inheritdoc/>
    public IReadOnlyList<JobDefinition> All()
    {
        // Defensive copy under the same lock that guards Register's append (mirror of the
        // batch registry pattern). Returns a fresh List<> so callers iterating cannot observe
        // a subsequent registration mid-iteration.
        lock (_ordered)
        {
            return _ordered.ToList();
        }
    }
}
