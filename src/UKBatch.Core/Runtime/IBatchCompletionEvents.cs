using System.Threading.Channels;

namespace UKBatch.Runtime;

/// <summary>
/// Internal seam exposing batch-run completion signals as a channel for the SignalR
/// hub fan-out pump (<c>JobStatusHubFanout</c>). The runtime emits ONE signal per batch run
/// after <see cref="BatchExecutor.RunAsync"/> returns (regardless of whether the batch
/// succeeded, failed, or was cancelled).
/// </summary>
/// <remarks>
/// <para>Friend-accessible from <c>UKBatch.Api</c> via <c>InternalsVisibleTo</c>.
/// <c>UKBatch.Api</c> is the SOLE intended consumer.</para>
/// <para>This runtime-driven signal replaces a per-event store-query / tracker pattern that suffered
/// a race window between sequential step dispatch and terminal events arriving at the hub WatchAsync
/// stream. The runtime knows when the batch is genuinely complete; the hub queries the store ONCE
/// per signal to build the aggregate summary.</para>
/// <para>The channel is bounded (1024) with <c>DropOldest</c> on overflow. The hub fan-out's
/// dedupe set still applies as defense-in-depth.</para>
/// <para>The channel item type is <see cref="BatchCompletionSignalPayload"/> (run id + definition id
/// + display name) so the hub fan-out can populate <c>BatchCompletionSummary.BatchDefinitionId</c> +
/// <c>.BatchName</c> without a roundtrip to <c>IBatchCatalogService</c>. The property name
/// <c>CompletedBatchRunIds</c> is preserved for call-site stability.</para>
/// </remarks>
internal interface IBatchCompletionEvents
{
    /// <summary>
    /// Channel reader emitting one payload per completed batch (success / failure / cancel).
    /// </summary>
    ChannelReader<BatchCompletionSignalPayload> CompletedBatchRunIds { get; }
}
