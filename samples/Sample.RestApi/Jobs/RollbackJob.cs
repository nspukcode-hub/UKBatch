using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.RestApi.Jobs;

/// <summary>Compensation step — rolls back invoice generation on failure.</summary>
public sealed class RollbackJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "RollbackJob ran (executionId={ExecutionId}).", context.ExecutionId);
        return Task.CompletedTask;
    }
}
