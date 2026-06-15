namespace UKBatch.Abstractions.Runtime;

/// <summary>
/// Notifies the batch scheduler that the stored batch definitions changed (create / update / delete), so a
/// newly-scheduled batch is armed without a host restart. A no-op before the host has started.
/// </summary>
public interface IBatchScheduleNotifier
{
    /// <summary>Re-scans store-defined batches and re-arms the schedule. Cheap and re-entrant; safe to fire-and-forget.</summary>
    Task NotifyDefinitionChangedAsync(CancellationToken cancellationToken);
}
