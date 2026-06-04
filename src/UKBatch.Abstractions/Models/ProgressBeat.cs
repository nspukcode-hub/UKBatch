namespace UKBatch.Abstractions.Models;

/// <summary>
/// Single progress snapshot per execution. Public so the SignalR hub
/// (<c>IJobStatusHubClient.ProgressUpdated</c>) can deliver it across the wire as a
/// strongly-typed event.
/// </summary>
/// <remarks>
/// Snapshots are deliberately whole-state (not deltas) so DropOldest semantics on the
/// per-execution channel give consumers the most-recent counter values regardless of
/// intermediate beats that may have been dropped under load.
/// </remarks>
public sealed record class ProgressBeat
{
    /// <summary>Execution id this beat refers to.</summary>
    public required string ExecutionId { get; init; }

    /// <summary>Cumulative items processed at the moment of the snapshot.</summary>
    public required long Processed { get; init; }

    /// <summary>Cumulative items failed at the moment of the snapshot.</summary>
    public required long Failed { get; init; }

    /// <summary>Total expected items, if set. <c>null</c> for streaming sources.</summary>
    public required long? Total { get; init; }

    /// <summary>
    /// Diagnostic only — flushers do NOT consume terminal beats.
    /// Terminal writes are awaited directly by <c>JobWorker</c> against the writer.
    /// </summary>
    public required bool IsTerminal { get; init; }
}
