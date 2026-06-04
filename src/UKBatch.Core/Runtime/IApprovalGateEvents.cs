using System.Threading.Channels;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Internal seam exposing pending-approval registrations as a channel for the SignalR
/// hub fan-out pump (<c>JobStatusHubFanout</c>). Implemented by <see cref="ApprovalGateService"/>.
/// </summary>
/// <remarks>
/// <para>Friend-accessible from <c>UKBatch.Api</c> via <c>InternalsVisibleTo</c>.
/// <c>UKBatch.Api</c> is the SOLE intended consumer; no other package should depend on this seam.</para>
/// <para>The channel writer is single-threaded (only <c>ApprovalGateService.AwaitApprovalAsync</c>
/// writes); readers MUST tolerate silent drops on overflow (best-effort fan-out, consistent with
/// the rest of the WatchAsync posture).</para>
/// </remarks>
internal interface IApprovalGateEvents
{
    /// <summary>
    /// Channel reader emitting one <see cref="PendingApproval"/> per gate registration. Completes
    /// when the host shuts down.
    /// </summary>
    ChannelReader<PendingApproval> NewGates { get; }
}
