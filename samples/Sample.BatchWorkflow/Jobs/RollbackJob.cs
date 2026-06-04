using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.BatchWorkflow.Jobs;

/// <summary>Compensation step run via the batch's <c>OnFailure</c> branch when the pipeline fails.</summary>
public sealed class RollbackJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogWarning(
            "RollbackJob ran (executionId={ExecutionId}, batchId={BatchId}).",
            context.ExecutionId,
            context.BatchId);
        return Task.CompletedTask;
    }
}
