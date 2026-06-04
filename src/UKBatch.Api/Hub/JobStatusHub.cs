using Microsoft.AspNetCore.SignalR;

namespace UKBatch.Api.Hub;

/// <summary>
/// SignalR hub for real-time job/batch/approval push. Mounted by <c>MapHubApi</c> at
/// <c>&lt;group&gt;/hubs/jobs</c> (or <c>UKBatchOptions.HubPath</c> if configured). Auth-agnostic —
/// call <c>RequireAuthorization</c> on the route group to opt in.
/// </summary>
/// <remarks>
/// <para><b>Group fan-out strategy:</b> server-side groups are named via well-known prefixes:</para>
/// <list type="bullet">
///   <item><c>exec:{executionId}</c> — one group per execution; subscribed via <see cref="SubscribeToExecution"/>.</item>
///   <item><c>batch:{batchId}</c> — one group per batch run; subscribed via <see cref="SubscribeToBatch"/>.</item>
///   <item><c>job:{jobName}</c> — one group per job-name; subscribed via <see cref="SubscribeToJob"/>.</item>
///   <item><c>all</c> — fire-hose; subscribed via <see cref="SubscribeAll"/> (admin / dashboard).</item>
/// </list>
/// <para>Fan-out is performed by <c>JobStatusHubFanout</c> (singleton IHostedService) which
/// subscribes to <c>IJobExecutionReader.WatchAsync</c> and pushes events to the relevant groups.</para>
/// <para><b>Reconnection:</b> SignalR loses group memberships across reconnect. Clients MUST
/// re-subscribe after <c>WithAutomaticReconnect</c> fires.</para>
/// </remarks>
public sealed class JobStatusHub : Hub<IJobStatusHubClient>
{
    /// <summary>Adds the connection to the per-execution group.</summary>
    public Task SubscribeToExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        return Groups.AddToGroupAsync(Context.ConnectionId, $"exec:{executionId}");
    }

    /// <summary>Adds the connection to the per-batch group.</summary>
    public Task SubscribeToBatch(string batchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        return Groups.AddToGroupAsync(Context.ConnectionId, $"batch:{batchId}");
    }

    /// <summary>Adds the connection to the per-job-name group.</summary>
    public Task SubscribeToJob(string jobName)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return Groups.AddToGroupAsync(Context.ConnectionId, $"job:{jobName}");
    }

    /// <summary>Adds the connection to the fire-hose group (every event).</summary>
    public Task SubscribeAll() => Groups.AddToGroupAsync(Context.ConnectionId, "all");

    /// <summary>Removes the connection from the per-execution group.</summary>
    public Task UnsubscribeExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"exec:{executionId}");
    }

    /// <summary>Removes the connection from the per-batch group.</summary>
    public Task UnsubscribeBatch(string batchId)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"batch:{batchId}");
    }

    /// <summary>Removes the connection from the per-job-name group.</summary>
    public Task UnsubscribeJob(string jobName)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"job:{jobName}");
    }

    /// <summary>Removes the connection from the fire-hose group.</summary>
    public Task UnsubscribeAll() => Groups.RemoveFromGroupAsync(Context.ConnectionId, "all");
}
