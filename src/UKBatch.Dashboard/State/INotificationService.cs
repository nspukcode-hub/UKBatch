namespace UKBatch.Dashboard.State;

/// <summary>
/// Scoped (per-circuit) notification dispatch consumed by <c>ToastContainer</c>. Small in-house
/// implementation — no community dependency.
/// </summary>
public interface INotificationService
{
    /// <summary>Fired when a notification is dispatched. <c>ToastContainer</c> subscribes.</summary>
    event Func<Notification, Task>? OnNotification;

    /// <summary>Dispatches a notification to all subscribers. Subscriber exceptions are isolated.</summary>
    Task NotifyAsync(Notification notification);

    /// <summary>Convenience: dispatch a success toast that auto-dismisses after 3s.</summary>
    Task SuccessAsync(string title, string? body = null);

    /// <summary>Convenience: dispatch an error toast that does NOT auto-dismiss.</summary>
    Task ErrorAsync(string title, string? body = null);

    /// <summary>Convenience: dispatch a warning toast that auto-dismisses after 5s.</summary>
    Task WarningAsync(string title, string? body = null);

    /// <summary>Convenience: dispatch an info toast that auto-dismisses after 3s.</summary>
    Task InfoAsync(string title, string? body = null);
}

/// <summary>Single toast notification. Auto-dismiss is opt-in via <see cref="AutoDismissAfter"/>.</summary>
public sealed record class Notification(
    NotificationLevel Level,
    string Title,
    string? Body = null,
    TimeSpan? AutoDismissAfter = null);

/// <summary>Notification severity — drives toast left-border color in <c>ToastContainer</c>.</summary>
public enum NotificationLevel
{
    /// <summary>Informational message — auto-dismisses by default.</summary>
    Info = 0,
    /// <summary>Successful operation — auto-dismisses by default.</summary>
    Success = 1,
    /// <summary>Warning — auto-dismisses after longer delay.</summary>
    Warning = 2,
    /// <summary>Error — does not auto-dismiss; requires operator acknowledgement.</summary>
    Error = 3,
}
