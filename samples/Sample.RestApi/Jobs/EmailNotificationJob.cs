using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.RestApi.Jobs;

/// <summary>Step 2 — sends email notifications.</summary>
public sealed class EmailNotificationJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "EmailNotificationJob ran (executionId={ExecutionId}).", context.ExecutionId);
        return Task.CompletedTask;
    }
}
