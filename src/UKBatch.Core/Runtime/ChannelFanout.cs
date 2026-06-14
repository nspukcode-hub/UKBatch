using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Polly;
using UKBatch.Abstractions.Jobs;

namespace UKBatch.Runtime;

/// <summary>
/// Shared producer-consumer fan-out used by both <see cref="ParallelExecutor"/> and
/// <c>PartitionedJobRunner</c>. Producer drains the source into a bounded
/// <see cref="Channel{T}"/>; N consumer tasks call the body per item; failures are routed via
/// <see cref="ItemErrorPolicy"/>.
/// </summary>
/// <remarks>
/// For <see cref="ItemErrorPolicy.RetryThenContinue"/>, the per-item
/// <see cref="ResiliencePipeline"/> is the SAME instance built once per <c>JobDefinition</c>
/// by <c>JobDefinitionFactory.BuildItemRetryPipeline</c>; ChannelFanout does not allocate
/// a builder per item.
/// </remarks>
internal static class ChannelFanout
{
    /// <summary>
    /// Runs the producer + consumer pipeline. Each item handled by <paramref name="body"/> is
    /// counted into <paramref name="progress"/> via <see cref="IJobProgress.Increment()"/> on success or
    /// <see cref="IJobProgress.ReportFailure()"/> on terminal failure (per the supplied error policy).
    /// </summary>
    public static async Task RunAsync<TItem>(
        IAsyncEnumerable<TItem> source,
        int workerCount,
        Func<TItem, CancellationToken, Task> body,
        ItemErrorPolicy errorPolicy,
        int channelCapacity,
        IJobProgress progress,
        ResiliencePipeline? cachedRetryPipeline,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(progress);
        ArgumentNullException.ThrowIfNull(logger);
        if (workerCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(workerCount), workerCount, "Worker count must be >= 1.");
        }
        if (channelCapacity < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(channelCapacity), channelCapacity, "Channel capacity must be >= 1.");
        }

        var channel = Channel.CreateBounded<TItem>(new BoundedChannelOptions(channelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = true,
        });

        using var policyCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in source.WithCancellation(policyCts.Token).ConfigureAwait(false))
                {
                    await channel.Writer.WriteAsync(item, policyCts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (policyCts.IsCancellationRequested)
            {
                // shutdown / FailFast
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, CancellationToken.None);

        var consumers = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var workerIndex = i; // capture per-iteration
            consumers[i] = Task.Run(() => ConsumerLoopAsync(workerIndex, channel.Reader, body, errorPolicy, progress, cachedRetryPipeline, logger, policyCts), CancellationToken.None);
        }

        try
        {
            await Task.WhenAll(consumers).ConfigureAwait(false);
        }
        finally
        {
            // ensure the producer completes regardless of consumer outcome
            policyCts.Cancel();
            try
            {
                await producer.ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }

        // Surface a consumer exception (FailFast) for the caller to observe.
        foreach (var c in consumers)
        {
            if (c.IsFaulted && c.Exception is { } agg)
            {
                throw agg.GetBaseException();
            }
        }
    }

    private static async Task ConsumerLoopAsync<TItem>(
        int workerIndex,
        ChannelReader<TItem> reader,
        Func<TItem, CancellationToken, Task> body,
        ItemErrorPolicy errorPolicy,
        IJobProgress progress,
        ResiliencePipeline? cachedRetryPipeline,
        ILogger logger,
        CancellationTokenSource policyCts)
    {
        // Establish WorkerIndex for THIS worker's async flow; it flows via AsyncLocal to every
        // body(...) invocation (ProcessAsync / the ParallelForEach body) on this consumer task, and
        // is restored when the scope disposes. Each Task.Run starts a fresh ExecutionContext, so the
        // N consumers do not observe each other's index.
        using var workerScope = JobContext.EnterWorkerScope(workerIndex);
        try
        {
            await foreach (var item in reader.ReadAllAsync(policyCts.Token).ConfigureAwait(false))
            {
                try
                {
                    await body(item, policyCts.Token).ConfigureAwait(false);
                    progress.Increment();
                }
                catch (OperationCanceledException) when (policyCts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    switch (errorPolicy)
                    {
                        case ItemErrorPolicy.FailFast:
                            policyCts.Cancel();
                            throw;

                        case ItemErrorPolicy.ContinueOnError:
                            logger.LogWarning(ex, "Item failed (policy=ContinueOnError)");
                            progress.ReportFailure();
                            break;

                        case ItemErrorPolicy.RetryThenContinue:
                            if (cachedRetryPipeline is null)
                            {
                                logger.LogWarning(ex, "Item failed and no retry pipeline configured (policy=RetryThenContinue)");
                                progress.ReportFailure();
                                break;
                            }
                            if (await TryRetryAsync(item, body, cachedRetryPipeline, policyCts.Token).ConfigureAwait(false))
                            {
                                progress.Increment();
                            }
                            else
                            {
                                logger.LogWarning(ex, "Item failed after retries (policy=RetryThenContinue)");
                                progress.ReportFailure();
                            }
                            break;

                        default:
                            // Unknown policy — treat as FailFast for safety.
                            policyCts.Cancel();
                            throw;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (policyCts.IsCancellationRequested)
        {
            // shutdown / FailFast
        }
    }

    private static async Task<bool> TryRetryAsync<TItem>(
        TItem item,
        Func<TItem, CancellationToken, Task> body,
        ResiliencePipeline pipeline,
        CancellationToken ct)
    {
        try
        {
            await pipeline.ExecuteAsync(async c => await body(item, c).ConfigureAwait(false), ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch
        {
            return false;
        }
    }
}
