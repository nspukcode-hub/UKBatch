using UKBatch.Abstractions.Models;

namespace UKBatch.Api.Hub;

/// <summary>
/// Strongly-typed client method surface (server → client RPC) for the SignalR hub.
/// </summary>
/// <remarks>
/// <para><b>v0.1 surface — four push methods:</b></para>
/// <list type="bullet">
///   <item><see cref="ExecutionStateChanged"/> — emitted on every <see cref="JobStatus"/> transition.</item>
///   <item><see cref="ProgressUpdated"/> — emitted on every progress beat (debounced upstream).</item>
///   <item><see cref="ApprovalRequested"/> — emitted when a new approval gate becomes pending.</item>
///   <item><see cref="BatchCompleted"/> — emitted once when the LAST execution in a batch run terminates.</item>
/// </list>
/// <para><b>v0.1 explicitly excludes <c>HubBackpressureWarning</c></b> (deferred).
/// The in-memory adapter does NOT surface per-subscription drop counters; adapter-side drop
/// telemetry (RabbitMQ / EF / Redis) is the v0.2.0 seam where the metric becomes first-class.
/// The v0.1 hub trusts the channel's silent-drop posture (see <c>WatchOverflowPolicy.Backpressure</c>).</para>
/// <para><b>Client-side dedupe required:</b> the fan-out fires events to up to 4
/// matched groups (<c>exec:{id}</c>, <c>batch:{id}</c>, <c>job:{name}</c>, <c>all</c>). A client
/// subscribed to N matching groups receives the same event N times in arrival order. Dedupe at
/// the consumer using a stable key like <c>(ExecutionId, Status, AttemptNumber)</c>.</para>
/// </remarks>
public interface IJobStatusHubClient
{
    /// <summary>Pushed when a <see cref="JobExecution.Status"/> transition occurs.</summary>
    Task ExecutionStateChanged(JobExecution snapshot);

    /// <summary>Pushed when a progress beat is published by an executing job.</summary>
    Task ProgressUpdated(ProgressBeat beat);

    /// <summary>Pushed when a new approval gate becomes pending.</summary>
    Task ApprovalRequested(PendingApproval approval);

    /// <summary>Pushed once when the LAST execution in a batch run terminates.</summary>
    Task BatchCompleted(BatchCompletionSummary summary);

    // NO HubBackpressureWarning method in v0.1 — deferred to v0.2.0.
}
