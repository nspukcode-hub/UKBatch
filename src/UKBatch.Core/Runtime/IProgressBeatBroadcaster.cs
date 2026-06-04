using System.Threading.Channels;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Internal seam exposing progress beats as a channel for the SignalR hub fan-out pump
/// (<c>JobStatusHubFanout</c>). Implemented by <see cref="DebouncedProgressFlusher"/>.
/// </summary>
/// <remarks>
/// <para>Friend-accessible from <c>UKBatch.Api</c> via <c>InternalsVisibleTo</c>.
/// <c>UKBatch.Api</c> is the SOLE intended consumer; no other package should depend on this seam.</para>
/// <para>The channel writer is multi-threaded (every <see cref="DebouncedProgressFlusher.PostBeat"/>
/// can write) and bounded by <c>UKBatchOptions.HubBufferCapacity</c>; on overflow the oldest beat
/// is dropped (consistent with the per-execution DropOldest in the flusher itself).</para>
/// </remarks>
internal interface IProgressBeatBroadcaster
{
    /// <summary>
    /// Channel reader emitting one <see cref="ProgressBeat"/> per <c>PostBeat</c> call. Completes
    /// when the host shuts down.
    /// </summary>
    ChannelReader<ProgressBeat> Beats { get; }
}
