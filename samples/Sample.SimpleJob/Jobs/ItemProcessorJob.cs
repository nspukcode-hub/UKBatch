using UKBatch.Abstractions.Jobs;
using UKBatch.AspNetCore.Tracing;

namespace Sample.SimpleJob.Jobs;

/// <summary>
/// Data-parallel demo job. <see cref="SourceAsync"/> streams items 0..(count-1) (defaulting to 100);
/// <see cref="ProcessAsync"/> is called concurrently across N workers and logs each item.
/// </summary>
public sealed class ItemProcessorJob : IPartitionedJob<int>
{
    /// <inheritdoc/>
    public async IAsyncEnumerable<int> SourceAsync(
        JobContext context,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var count = context.Parameters.GetOrDefault<int>("count", 100);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return i;
            await Task.Yield();
        }
    }

    /// <inheritdoc/>
    public Task ProcessAsync(int item, JobContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        // Restore inside the partitioned job too so each worker iteration emits correlated logs.
        using var _ = context.RestoreRequestActivity();
        context.Logger.LogDebug(
            "ItemProcessorJob processed item {Item} (executionId={ExecutionId}).",
            item,
            context.ExecutionId);
        return Task.CompletedTask;
    }
}
