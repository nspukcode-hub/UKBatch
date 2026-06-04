namespace UKBatch.Abstractions.Jobs;

/// <summary>
/// Executes an async body across N worker tasks consuming from an <see cref="IAsyncEnumerable{T}"/> source.
/// Exposed on <see cref="JobContext.ParallelExecutor"/> and consumed by
/// <see cref="JobContextParallelExtensions.ParallelForEachAsync{TItem}"/>.
/// Implementations MUST be thread-safe and MUST apply backpressure on the source when consumers fall behind.
/// </summary>
public interface IParallelExecutor
{
    /// <summary>
    /// Runs <paramref name="body"/> for each item produced by <paramref name="source"/> on
    /// <paramref name="workerCount"/> concurrent workers. Errors are routed per <paramref name="errorPolicy"/>.
    /// </summary>
    /// <typeparam name="TItem">Item type.</typeparam>
    /// <param name="source">Item stream; the executor pulls lazily and applies backpressure.</param>
    /// <param name="workerCount">Number of concurrent worker tasks; must be &gt;= 1.</param>
    /// <param name="body">Per-item work. Called concurrently; implementations MUST be thread-safe.</param>
    /// <param name="errorPolicy">Per-item failure handling.</param>
    /// <param name="context">Parent job context (passed to <paramref name="body"/>).</param>
    /// <param name="cancellationToken">Cancellation; propagated to all workers and the source.</param>
    Task ForEachAsync<TItem>(
        IAsyncEnumerable<TItem> source,
        int workerCount,
        Func<TItem, JobContext, CancellationToken, Task> body,
        ItemErrorPolicy errorPolicy,
        JobContext context,
        CancellationToken cancellationToken);
}
