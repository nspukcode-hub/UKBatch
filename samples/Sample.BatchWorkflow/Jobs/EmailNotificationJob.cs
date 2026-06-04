using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.BatchWorkflow.Jobs;

/// <summary>Parallel notification step — sends an email.</summary>
public sealed class EmailNotificationJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "EmailNotificationJob ran (executionId={ExecutionId}, batchId={BatchId}).",
            context.ExecutionId,
            context.BatchId);
        return Task.CompletedTask;
    }
}
