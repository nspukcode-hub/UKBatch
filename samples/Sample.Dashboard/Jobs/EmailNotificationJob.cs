using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>Step 2 — sends email notifications.</summary>
public sealed class EmailNotificationJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "EmailNotificationJob ran (executionId={ExecutionId}).", context.ExecutionId);
        // Demo-only pacing so the live DAG shows a visible running→completed transition per node.
        await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
    }
}
