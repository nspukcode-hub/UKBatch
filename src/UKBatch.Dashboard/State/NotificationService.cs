namespace UKBatch.Dashboard.State;

/// <summary>Default <see cref="INotificationService"/> — per-circuit scoped; subscriber-isolated dispatch.</summary>
internal sealed class NotificationService : INotificationService
{
    public event Func<Notification, Task>? OnNotification;

    public async Task NotifyAsync(Notification notification)
    {
        ArgumentNullException.ThrowIfNull(notification);
        var handler = OnNotification;
        if (handler is null) return;
        foreach (Func<Notification, Task> sub in handler.GetInvocationList())
        {
            try
            {
                await sub(notification).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Subscriber failure is isolated — one broken toast renderer must not block others.
            }
        }
    }

    public Task SuccessAsync(string title, string? body = null)
        => NotifyAsync(new Notification(NotificationLevel.Success, title, body, TimeSpan.FromSeconds(3)));

    public Task ErrorAsync(string title, string? body = null)
        => NotifyAsync(new Notification(NotificationLevel.Error, title, body, null));

    public Task WarningAsync(string title, string? body = null)
        => NotifyAsync(new Notification(NotificationLevel.Warning, title, body, TimeSpan.FromSeconds(5)));

    public Task InfoAsync(string title, string? body = null)
        => NotifyAsync(new Notification(NotificationLevel.Info, title, body, TimeSpan.FromSeconds(3)));
}
