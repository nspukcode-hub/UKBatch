using UKBatch.Abstractions.Models;

namespace UKBatch.Abstractions.Storage;

/// <summary>
/// In-process fan-out contract for live <see cref="JobExecution"/> updates — the seam every store
/// adapter shares so <see cref="IJobExecutionReader.WatchAsync"/> has ONE implementation across
/// InMemory + EF + future adapters. Promoted to Abstractions-public (the Core concrete
/// <c>JobExecutionWatchHub</c> implements it) so adapters compose the SAME singleton without friend
/// access to Core.
/// </summary>
/// <remarks>
/// The subscription internals (bounded channel, <see cref="WatchOverflowPolicy"/> mapping) stay
/// Core-internal — only this fan-out surface is public. SQL has no native change-feed; cross-process
/// push over a shared DB (LISTEN/NOTIFY) is a v0.2 hook. In embedded mode and DB-per-service worker mode, this hub
/// delivers live updates for the local node's writes.
/// </remarks>
public interface IJobExecutionWatchHub
{
    /// <summary>Subscribe to the live stream; the returned enumerator drains on cancellation/disposal.</summary>
    /// <remarks>
    /// The returned sequence MUST register its subscription synchronously during the first
    /// <c>MoveNextAsync</c> call — before its first <c>await</c> — so a consumer can guarantee it will
    /// observe every subsequent <see cref="Publish"/> by issuing that first move before publishing.
    /// The in-process hub implementation registers the subscriber set entry before its first await; a
    /// custom implementation must preserve this ordering.
    /// </remarks>
    IAsyncEnumerable<JobExecution> WatchAsync(WatchOptions options, CancellationToken cancellationToken);

    /// <summary>Non-blocking fan-out of one execution snapshot to all current subscribers (post-commit for durable stores).</summary>
    void Publish(JobExecution execution);
}
