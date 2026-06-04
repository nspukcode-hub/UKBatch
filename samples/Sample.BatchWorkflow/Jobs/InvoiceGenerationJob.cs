using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.BatchWorkflow.Jobs;

/// <summary>Step 1 of the invoice pipeline — generates invoices.</summary>
public sealed class InvoiceGenerationJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "InvoiceGenerationJob ran (executionId={ExecutionId}, batchId={BatchId}, triggeredBy={TriggeredBy}).",
            context.ExecutionId,
            context.BatchId,
            context.TriggeredBy ?? "<none>");
        return Task.CompletedTask;
    }
}
