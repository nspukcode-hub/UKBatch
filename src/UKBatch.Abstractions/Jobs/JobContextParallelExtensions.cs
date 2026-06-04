namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Inline parallelism helpers for <see cref="IJob"/> implementations.
/// Lives in Abstractions so consumer job code does not need to depend on UKBatch.Core.
/// </summary>
public static class JobContextParallelExtensions
{
    /// <summary>
    /// Runs <paramref name="body"/> for each item produced by <paramref name="source"/> on
    /// <paramref name="workerCount"/> concurrent workers, using the parent execution's
    /// <see cref="JobContext.ParallelExecutor"/>.
    /// </summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <param name="context">Parent job context (executor + cancellation propagation source).</param>
    /// <param name="source">Item stream; pulled lazily with backpressure.</param>
    /// <param name="workerCount">Number of concurrent worker tasks; must be &gt;= 1.</param>
    /// <param name="body">Per-item work. Called concurrently; implementations MUST be thread-safe.</param>
    /// <param name="errorPolicy">Per-item failure handling. Defaults to <see cref="ItemErrorPolicy.ContinueOnError"/>.</param>
    /// <param name="cancellationToken">Cancellation; propagated to all workers and the source.</param>
    public static Task ParallelForEachAsync<TItem>(
        this JobContext context,
        IAsyncEnumerable<TItem> source,
        int workerCount,
        Func<TItem, JobContext, CancellationToken, Task> body,
        ItemErrorPolicy errorPolicy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        return context.ParallelExecutor.ForEachAsync(source, workerCount, body, errorPolicy, context, cancellationToken);
    }
}
