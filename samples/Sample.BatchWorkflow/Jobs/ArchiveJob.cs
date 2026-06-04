using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.BatchWorkflow.Jobs;

/// <summary>Final pipeline step — archives the successfully processed invoices.</summary>
public sealed class ArchiveJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "ArchiveJob ran (executionId={ExecutionId}, batchId={BatchId}).",
            context.ExecutionId,
            context.BatchId);
        return Task.CompletedTask;
    }
}
