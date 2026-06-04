namespace UKBatch.Dashboard;

/// <summary>State machine for <see cref="Clients.IUKBatchClient.State"/>.</summary>
public enum UKBatchClientState
{
    /// <summary>Initial state; before any <see cref="Clients.IUKBatchClient.ConnectAsync"/> call OR after a clean disconnect.</summary>
    Disconnected = 0,

    /// <summary>An in-flight <c>HubConnection.StartAsync</c> is running.</summary>
    Connecting = 1,

    /// <summary>Hub is connected and ready to push events.</summary>
    Connected = 2,

    /// <summary>SignalR automatic-reconnect is in progress (between <c>HubConnection.Reconnecting</c> and <c>Reconnected</c>).</summary>
    Reconnecting = 3,

    /// <summary>
    /// Hub is connected but one or more group subscriptions failed to re-establish after reconnect.
    /// Live updates for the failed groups will NOT arrive; the rest of the subscriptions are healthy.
    /// Operators must retry manually (UI surfaces an amber banner + "Retry" button).
    /// See RestUKBatchClient.OnHubReconnectedAsync.
    /// </summary>
    PartiallyConnected = 4,
}
