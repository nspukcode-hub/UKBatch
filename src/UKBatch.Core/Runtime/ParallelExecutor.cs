using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Jobs;

namespace UKBatch.Runtime;

/// <summary>
/// Per-execution <see cref="IParallelExecutor"/> implementation. Constructed by
/// <c>JobWorker</c> for each execution; inherits the parent execution's cancellation token so
/// backpressure semantics and cancellation are correctly scoped.
/// </summary>
/// <remarks>
/// Channel capacity is <c>workerCount * 32</c>.
/// </remarks>
internal sealed class ParallelExecutor : IParallelExecutor
{
    private readonly ILogger _logger;

    /// <summary>Constructs an executor bound to a logger (typically the job's own logger).</summary>
    public ParallelExecutor(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task ForEachAsync<TItem>(
        IAsyncEnumerable<TItem> source,
        int workerCount,
        Func<TItem, JobContext, CancellationToken, Task> body,
        ItemErrorPolicy errorPolicy,
        JobContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(context);
        var channelCapacity = Math.Max(1, workerCount * 32);
        return ChannelFanout.RunAsync(
            source,
            workerCount,
            (item, ct) => body(item, context, ct),
            errorPolicy,
            channelCapacity,
            context.Progress,
            cachedRetryPipeline: null, // ParallelExecutor does not provide a cached pipeline; per-item RetryThenContinue degrades to no-pipeline (logged + counted as failure).
            _logger,
            cancellationToken);
    }
}
