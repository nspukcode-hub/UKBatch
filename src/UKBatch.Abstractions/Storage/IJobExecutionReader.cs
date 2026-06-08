using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Read-side of the execution-history store. Consumed by dashboards, REST API list endpoints,
/// and any reporting surface. Implementations MUST be thread-safe.
/// </summary>
public interface IJobExecutionReader
{
    /// <summary>Returns the execution by id, or <c>null</c> if absent.</summary>
    Task<JobExecution?> GetAsync(string executionId, CancellationToken cancellationToken);

    /// <summary>Paged query. Pagination is provided via <see cref="JobQuery.Offset"/> and <see cref="JobQuery.Limit"/>.</summary>
    Task<IReadOnlyList<JobExecution>> QueryAsync(JobQuery query, CancellationToken cancellationToken);

    /// <summary>Count for the same predicate as <see cref="QueryAsync"/>; offset and limit are ignored.</summary>
    Task<long> CountAsync(JobQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Streams change-feed events as they happen; consumed by SignalR push. The overflow contract
    /// is set by <paramref name="options"/> — see <see cref="WatchOverflowPolicy"/>.
    /// </summary>
    /// <remarks>
    /// The returned sequence MUST register its subscription synchronously during the first
    /// <c>MoveNextAsync</c> call — before its first <c>await</c> — so a consumer can guarantee it will
    /// observe every subsequent event by issuing that first move before publishing. Adapters that
    /// delegate to the shared in-process fan-out hub satisfy this automatically; a custom adapter that
    /// implements its own async iterator must preserve this ordering (do no work that awaits before
    /// the subscription is registered).
    /// </remarks>
    IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options, CancellationToken cancellationToken);
}
