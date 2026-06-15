using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Workers;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Combined REST + SignalR client for a single UKBatch service. ONE instance per service across
/// the entire Blazor app (app-scoped via <see cref="IUKBatchClientFactory"/>); page components
/// receive a reference and subscribe to events. Per-circuit page components MUST unsubscribe
/// from events on dispose.
/// </summary>
/// <remarks>
/// <para><b>Surface organization (38 reflection members):</b></para>
/// <list type="bullet">
///   <item><b>Identity + lifecycle</b> (5 members — 2 props + 1 event + 2 methods): <c>Service</c>, <c>State</c>, <c>StateChanged</c>, <c>ConnectAsync</c>, <c>DisconnectAsync</c>.</item>
///   <item><b>REST</b> (21 methods): job catalog/detail/trigger (3), batch catalog/by-id/by-name/run/run-status (5) + create/update/delete (3) + run-list/run-cancel (2) = 10, execution detail/query/cancel (3), approval list/approve/reject/by-batch-gates (4), worker snapshot (1).</item>
///   <item><b>Hub events</b> (4 events): <see cref="ExecutionStateChanged"/>, <see cref="ProgressUpdated"/>, <see cref="ApprovalRequested"/>, <see cref="BatchCompleted"/>.</item>
///   <item><b>Hub subscriptions</b> (8 methods): Subscribe/Unsubscribe × {Execution, Batch, Job, All}.</item>
/// </list>
/// <para><b>Fan-out reminder:</b> hub events may arrive up to 4× per real
/// event when a client subscribes to overlapping groups (<c>exec:</c> + <c>batch:</c> + <c>job:</c>
/// + <c>all</c>). The client's <c>LruDedupeCache</c> + the page-level monotonic guard
/// (<c>JobStatusRank.Rank</c>) absorb the duplicates; subscribers receive a single logical update.</para>
/// <para><b>App-scoped invariant:</b> exactly ONE instance per
/// <see cref="UKBatchServiceDescriptor.Name"/> across the host.
/// <see cref="IUKBatchClientFactory"/> is registered as <c>Singleton</c>; the factory's internal
/// <see cref="System.Collections.Concurrent.ConcurrentDictionary{TKey,TValue}"/> caches one
/// <c>RestUKBatchClient</c> per service.</para>
/// <para><b>Hub event dispatch contract:</b> subscribers are invoked in parallel
/// (<c>Task.WhenAll</c>) on the SignalR receive thread. Page components MUST hop to the
/// render dispatcher via <c>await InvokeAsync(StateHasChanged)</c> — bare
/// <c>StateHasChanged()</c> is illegal off-render and throws <see cref="InvalidOperationException"/>.</para>
/// <para><b>Dedupe contract:</b> internal <c>LruDedupeCache</c> filters duplicate hub events keyed
/// by <c>(ExecutionId, Status, AttemptNumber)</c> for <see cref="ExecutionStateChanged"/> and
/// <c>(ExecutionId, Processed, Failed)</c> for <see cref="ProgressUpdated"/>.</para>
/// <para><b>Reconnect contract:</b> on <c>HubConnection.Reconnected</c>, the client re-subscribes
/// to every group in <c>_activeGroups</c>. Pages do NOT need to re-subscribe manually (SignalR
/// loses group memberships on reconnect). If any re-subscribe fails, the client transitions to
/// <see cref="UKBatchClientState.PartiallyConnected"/> — pre-existing failed groups will NOT
/// receive live updates until manual recovery; new subscribes STILL succeed (NEW-SF-D v1.2).</para>
/// </remarks>
public interface IUKBatchClient : IAsyncDisposable
{
    // ── Identity + lifecycle (5 members — 2 props + 1 event + 2 methods) ───────────────

    /// <summary>Immutable descriptor of the underlying service.</summary>
    UKBatchServiceDescriptor Service { get; }

    /// <summary>Current connection state (snapshot — does NOT block).</summary>
    UKBatchClientState State { get; }

    /// <summary>
    /// Fired on every state transition. Subscribers MUST be async-safe and short. Handler
    /// exceptions are logged + swallowed (per-subscriber isolation).
    /// Subscribers MUST NOT call <see cref="ConnectAsync"/> / <see cref="DisconnectAsync"/> —
    /// re-entry is a deadlock hazard.
    /// </summary>
    event Func<UKBatchClientState, Task>? StateChanged;

    /// <summary>
    /// Eagerly connects the hub. Idempotent — second invocation returns when already connected.
    /// Called by <c>UKBatchServiceConductor.StartAsync</c>; page components do NOT call this.
    /// </summary>
    Task ConnectAsync(CancellationToken ct);

    /// <summary>Gracefully disconnects the hub. Idempotent. Called on host shutdown.</summary>
    Task DisconnectAsync(CancellationToken ct);

    // ── REST — Jobs (3) ────────────────────────────────────────────────────────────────

    /// <summary>GET <c>/jobs</c> — paged.</summary>
    Task<PageEnvelope<JobDefinitionDto>> ListJobsAsync(int offset, int limit, bool? partitioned, CancellationToken ct);

    /// <summary>GET <c>/jobs/{name}</c>. Returns <c>null</c> on 404.</summary>
    Task<JobDefinitionDto?> GetJobAsync(string jobName, CancellationToken ct);

    /// <summary>POST <c>/jobs/{name}/trigger</c>. Returns the new execution id.</summary>
    Task<string> TriggerJobAsync(string jobName, IReadOnlyDictionary<string, object?>? parameters, string? triggeredBy, CancellationToken ct);

    // ── REST — Batches (10) ────────────────────────────────────────────────────────────

    /// <summary>GET <c>/batches</c> — paged via the server-side batch catalog service.</summary>
    Task<PageEnvelope<BatchDefinitionDto>> ListBatchesAsync(int offset, int limit, string? nameContains, BatchSource? source, CancellationToken ct);

    /// <summary>GET <c>/batches/by-id/{id}</c>. Returns <c>null</c> on 404.</summary>
    Task<BatchDefinitionDto?> GetBatchByIdAsync(string definitionId, CancellationToken ct);

    /// <summary>GET <c>/batches/by-name/{name}</c>. Returns <c>null</c> on 404.</summary>
    Task<BatchDefinitionDto?> GetBatchByNameAsync(string name, BatchSource? source, CancellationToken ct);

    /// <summary>POST <c>/batches/by-id/{id}/run</c>. Returns the new batch RUN id.</summary>
    Task<string> RunBatchByIdAsync(string definitionId, IReadOnlyDictionary<string, object?>? initialParameters, string? triggeredBy, CancellationToken ct);

    /// <summary>GET <c>/batches/{batchRunId}/status</c> — paged executions of ONE batch run.</summary>
    Task<PageEnvelope<JobExecution>> GetBatchRunStatusAsync(string batchRunId, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// POST <c>/batches</c>. Creates a Dashboard- or Api-source batch (the server assigns the id).
    /// Throws <see cref="UKBatchClientException"/> on 400 (validation / code-source) or 409 (duplicate name).
    /// </summary>
    Task<BatchDefinitionDto> CreateBatchAsync(CreateBatchRequest request, CancellationToken ct);

    /// <summary>
    /// PUT <c>/batches/by-id/{id}</c>. Updates a Store-source batch with optimistic concurrency
    /// (<see cref="UpdateBatchRequest.Version"/>). Throws on 404 (not found), 409 (duplicate name OR
    /// concurrency conflict — distinguish via <see cref="UKBatchClientException.ProblemType"/>), or 400.
    /// </summary>
    Task<BatchDefinitionDto> UpdateBatchAsync(string definitionId, UpdateBatchRequest request, CancellationToken ct);

    /// <summary>
    /// DELETE <c>/batches/by-id/{id}</c>. Idempotent (404-absent treated as success). Code-source → 400.
    /// </summary>
    Task DeleteBatchAsync(string definitionId, CancellationToken ct);

    /// <summary>
    /// GET <c>/batches/runs</c> — run-paginated history. Filter by <paramref name="batchDefinitionId"/>
    /// (null = across definitions); <paramref name="includeRunning"/> false hides in-progress runs.
    /// The page's <c>TotalCount</c> is the filter-wide total (so the pager can page).
    /// </summary>
    Task<PageEnvelope<BatchRun>> QueryRunsAsync(string? batchDefinitionId, bool includeRunning, int offset, int limit, CancellationToken ct);

    /// <summary>
    /// POST <c>/batches/{batchRunId}/cancel</c>. Administrative cancel of an in-flight run (unblocks a
    /// parked approval gate). Idempotent — succeeds even if the run already finished or never existed.
    /// </summary>
    Task CancelRunAsync(string batchRunId, CancellationToken ct);

    // ── REST — Executions (3) ──────────────────────────────────────────────────────────

    /// <summary>GET <c>/executions/{id}</c>. Returns <c>null</c> on 404.</summary>
    Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken ct);

    /// <summary>POST <c>/executions/query</c> — paged.</summary>
    Task<PageEnvelope<JobExecution>> QueryExecutionsAsync(JobQueryRequest query, CancellationToken ct);

    /// <summary>POST <c>/executions/{id}/cancel</c>. Idempotent. Throws on 404.</summary>
    Task CancelExecutionAsync(string executionId, CancellationToken ct);

    // ── REST — Approvals (4) ───────────────────────────────────────────────────────────

    /// <summary>GET <c>/approvals</c> — flat list (unwraps the artificial PageEnvelope).</summary>
    Task<IReadOnlyList<PendingApprovalDto>> ListApprovalsAsync(string? role, CancellationToken ct);

    /// <summary>POST <c>/approvals/{id}/approve</c>. Approver identity is server-derived (from <c>HttpContext.User</c>, not the request body).</summary>
    Task ApproveAsync(string approvalId, string? note, CancellationToken ct);

    /// <summary>POST <c>/approvals/{id}/reject</c>. Reason required.</summary>
    Task RejectAsync(string approvalId, string reason, CancellationToken ct);

    /// <summary>
    /// GET <c>/approvals/by-batch/{batchId}</c> — every gate (pending AND decided) for one batch run,
    /// each carrying its own recorded outcome. Used to colour a gate DAG node from the gate's own
    /// decision (a gate has no <c>JobExecution</c> row, so its outcome is invisible to row roll-ups).
    /// Empty list for an unknown run.
    /// </summary>
    Task<IReadOnlyList<ApprovalGateViewDto>> ListBatchGatesAsync(string batchId, CancellationToken ct);

    // ── REST — Workers (1) ─────────────────────────────────────────────────────────────

    /// <summary>GET <c>/workers</c> — live worker snapshot. Empty list if none.</summary>
    Task<IReadOnlyList<WorkerInfo>> GetWorkersAsync(CancellationToken ct);

    // ── Hub events (4) ─────────────────────────────────────────────────────────────────

    /// <summary>Fired on <c>IJobStatusHubClient.ExecutionStateChanged</c>. Dedup'd by (ExecutionId, Status, AttemptNumber).</summary>
    event Func<JobExecution, Task>? ExecutionStateChanged;

    /// <summary>Fired on <c>IJobStatusHubClient.ProgressUpdated</c>. Dedup'd best-effort by (ExecutionId, Processed, Failed).</summary>
    event Func<ProgressBeat, Task>? ProgressUpdated;

    /// <summary>Fired on <c>IJobStatusHubClient.ApprovalRequested</c>. NOT dedup'd (rare event).</summary>
    event Func<PendingApproval, Task>? ApprovalRequested;

    /// <summary>Fired on <c>IJobStatusHubClient.BatchCompleted</c>. Dedup'd by <c>BatchId</c>.</summary>
    event Func<BatchCompletionSummary, Task>? BatchCompleted;

    // ── Hub subscriptions (8) ──────────────────────────────────────────────────────────

    /// <summary>Hub: <c>SubscribeToExecution(executionId)</c>. Added to <c>_activeGroups</c>.</summary>
    Task SubscribeToExecutionAsync(string executionId, CancellationToken ct);

    /// <summary>Hub: <c>UnsubscribeExecution(executionId)</c>. Removed from <c>_activeGroups</c>.</summary>
    Task UnsubscribeFromExecutionAsync(string executionId, CancellationToken ct);

    /// <summary>Hub: <c>SubscribeToBatch(batchRunId)</c>.</summary>
    Task SubscribeToBatchAsync(string batchRunId, CancellationToken ct);

    /// <summary>Hub: <c>UnsubscribeBatch(batchRunId)</c>.</summary>
    Task UnsubscribeFromBatchAsync(string batchRunId, CancellationToken ct);

    /// <summary>Hub: <c>SubscribeToJob(jobName)</c>.</summary>
    Task SubscribeToJobAsync(string jobName, CancellationToken ct);

    /// <summary>Hub: <c>UnsubscribeJob(jobName)</c>.</summary>
    Task UnsubscribeFromJobAsync(string jobName, CancellationToken ct);

    /// <summary>Hub: <c>SubscribeAll()</c> — fire-hose (admin / aggregate views).</summary>
    Task SubscribeAllAsync(CancellationToken ct);

    /// <summary>Hub: <c>UnsubscribeAll()</c>.</summary>
    Task UnsubscribeAllAsync(CancellationToken ct);
}
