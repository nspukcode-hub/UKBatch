using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage;

/// <summary>
/// In-process fan-out hub for live <see cref="JobExecution"/> updates. Implements the
/// Abstractions-public <see cref="IJobExecutionWatchHub"/> seam so EVERY <see cref="IJobStore"/>
/// adapter (in-memory, EF Core, future Redis/RabbitMQ) shares ONE <c>WatchAsync</c> implementation
/// without friend access to Core.
/// </summary>
/// <remarks>
/// <para><b>Single subscription implementation:</b> the subscription set + fan-out logic.
/// <see cref="InMemoryJobStoreSubscription"/> backs each watcher — a bounded channel with the
/// configured <see cref="WatchOverflowPolicy"/> mapping and a non-blocking <c>TryPublish</c>.</para>
/// <para><b>Public so adapters compose it:</b> the concrete type is <c>public sealed</c> and registered
/// as a singleton in Core's builder; both stores inject the <see cref="IJobExecutionWatchHub"/>
/// interface and resolve the same instance. The subscription internals stay Core-internal — only the
/// fan-out surface (<see cref="WatchAsync"/> + <see cref="Publish"/>) is public.</para>
/// <para><b>SQL has no native change-feed:</b> the EF adapter publishes to this hub AFTER its DB commit
/// (post-commit ordering) so subscribers only ever see committed rows. Cross-process push over a
/// shared DB (LISTEN/NOTIFY) is a v0.2 hook; in embedded mode and DB-per-service worker mode, the hub delivers live
/// updates for the local node's writes.</para>
/// </remarks>
public sealed class JobExecutionWatchHub : IJobExecutionWatchHub
{
    private readonly ConcurrentDictionary<Guid, InMemoryJobStoreSubscription> _subscriptions = new();
    private readonly ILogger<JobExecutionWatchHub> _logger;

    /// <summary>Constructs the hub with the injected logger (used for the "buffer full; dropped" debug log).</summary>
    public JobExecutionWatchHub(ILogger<JobExecutionWatchHub> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sub = new InMemoryJobStoreSubscription(options);
        var id = Guid.NewGuid();
        _subscriptions[id] = sub;
        try
        {
            await foreach (var ex in sub.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return ex;
            }
        }
        finally
        {
            _subscriptions.TryRemove(id, out _);
            await sub.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Publish(JobExecution execution)
    {
        ArgumentNullException.ThrowIfNull(execution);
        foreach (var sub in _subscriptions.Values)
        {
            if (!sub.TryPublish(execution))
            {
                _logger.LogDebug("WatchAsync subscriber buffer full; event dropped for execution {Id}.", execution.ExecutionId);
            }
        }
    }
}
