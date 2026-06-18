using Cronos;

namespace UKBatch.Runtime;

/// <summary>
/// Entry in <see cref="BatchScheduler"/>'s priority queue. Carries the parsed
/// <see cref="CronExpression"/> so the scheduler doesn't re-parse on each reschedule, plus the
/// definition id used to launch the run.
/// </summary>
internal sealed record class ScheduledBatchEntry
{
    /// <summary>Definition id passed to <c>IJobRunner.TriggerBatchAsync</c>.</summary>
    public required string BatchDefinitionId { get; init; }

    /// <summary>Display name for diagnostics.</summary>
    public required string BatchName { get; init; }

    /// <summary>Parsed cron expression.</summary>
    public required CronExpression CronExpression { get; init; }

    /// <summary>UTC time at which this entry next fires.</summary>
    public required DateTimeOffset NextFireUtc { get; init; }

    /// <summary>
    /// Per-batch missed-fire catch-up window copied from the definition at build time; <c>null</c> or
    /// <see cref="TimeSpan.Zero"/> means no catch-up. Used to decide whether to persist a last-fire
    /// watermark before firing, and (at startup) whether a missed occurrence is fresh enough to replay.
    /// </summary>
    public TimeSpan? CatchUpWindow { get; init; }
}
