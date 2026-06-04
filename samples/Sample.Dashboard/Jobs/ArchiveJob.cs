using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.Dashboard.Jobs;

/// <summary>Step 3 — archives invoices to long-term storage.</summary>
public sealed class ArchiveJob : IJob
{
    /// <inheritdoc/>
    public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogInformation(
            "ArchiveJob ran (executionId={ExecutionId}).", context.ExecutionId);
        // Demo-only pacing — slightly longer than Email so the parallel siblings finish at different times.
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
    }
}
