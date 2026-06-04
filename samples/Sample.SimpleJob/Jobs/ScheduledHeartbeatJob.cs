using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.SimpleJob.Jobs;

/// <summary>
/// Scheduled heartbeat — fires every 30 seconds via Cron (6-field, seconds-prefix by default).
/// </summary>
[Job(Schedule = "*/30 * * * * *")]
public sealed class ScheduledHeartbeatJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "ScheduledHeartbeatJob beat (executionId={ExecutionId}, triggeredBy={TriggeredBy}).",
            context.ExecutionId,
            context.TriggeredBy ?? "<scheduler>");
        return Task.CompletedTask;
    }
}
