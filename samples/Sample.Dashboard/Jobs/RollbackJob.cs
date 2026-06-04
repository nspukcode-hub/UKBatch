using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>Compensation step — rolls back invoice generation on failure.</summary>
public sealed class RollbackJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "RollbackJob ran (executionId={ExecutionId}).", context.ExecutionId);
        // Demo-only pacing so the live DAG shows a visible running→completed transition per node.
        await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
    }
}
