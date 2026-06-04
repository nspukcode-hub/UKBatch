using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.RestApi.Jobs;

/// <summary>Step 3 — archives invoices to long-term storage.</summary>
public sealed class ArchiveJob : IJob
{
    /// <inheritdoc/>
    public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "ArchiveJob ran (executionId={ExecutionId}).", context.ExecutionId);
        return Task.CompletedTask;
    }
}
