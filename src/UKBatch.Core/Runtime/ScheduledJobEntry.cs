using Cronos;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// Entry in <see cref="JobScheduler"/>'s priority queue. Carries the parsed
/// <see cref="CronExpression"/> so the scheduler doesn't re-parse on each reschedule.
/// </summary>
internal sealed record class ScheduledJobEntry
{
    /// <summary>Job definition driving the schedule.</summary>
    public required JobDefinition Definition { get; init; }

    /// <summary>Parsed cron expression.</summary>
    public required CronExpression CronExpression { get; init; }

    /// <summary>UTC time at which this entry next fires.</summary>
    public required DateTimeOffset NextFireUtc { get; init; }
}
