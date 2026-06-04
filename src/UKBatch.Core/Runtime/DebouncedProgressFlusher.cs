using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Runtime;

/// <summary>
/// Per-execution debounced flusher.
/// <para>
/// Each execution owns its own bounded(1) DropOldest <see cref="Channel{T}"/>; the writer
/// (<see cref="CountingJobProgress.Beat"/>) is non-blocking and always overwrites the in-channel beat
/// with the latest snapshot. The ticker drains all per-execution channels every
/// <see cref="UKBatchOptions.ProgressFlushInterval"/>.
/// </para>
/// <para>
/// Terminal progress writes do NOT pass through the flusher; <c>JobWorker</c> awaits
/// <see cref="IJobExecutionWriter.UpdateProgressAsync"/> directly with <see cref="CancellationToken.None"/>.
/// </para>
/// </summary>
internal sealed class DebouncedProgressFlusher : IHostedService, IAsyncDisposable, IProgressBeatBroadcaster
{
    private readonly ConcurrentDictionary<string, Channel<ProgressBeat>> _executionInboxes
        = new(StringComparer.Ordinal);

    // Bounded broadcast channel consumed by the SignalR hub fan-out pump. Sized
    // generously (1024) so the hub pump rarely contends with the writer thread. Overflow drops
    // the OLDEST beat — consistent with per-execution DropOldest semantics.
    private readonly Channel<ProgressBeat> _broadcast = Channel.CreateBounded<ProgressBeat>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    private readonly IJobExecutionWriter _writer;
    private readonly IOptions<UKBatchOptions> _options;
    private readonly ILogger<DebouncedProgressFlusher> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _tickerTask;

    /// <summary>Constructs the flusher.</summary>
    public DebouncedProgressFlusher(
        IJobExecutionWriter writer,
        IOptions<UKBatchOptions> options,
        ILogger<DebouncedProgressFlusher> logger)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _writer = writer;
        _options = options;
        _logger = logger;
    }

    /// <summary>Non-blocking publish; first beat per execution allocates the channel.</summary>
    public void PostBeat(ProgressBeat beat)
    {
        ArgumentNullException.ThrowIfNull(beat);
        var ch = _executionInboxes.GetOrAdd(beat.ExecutionId, static _ =>
            Channel.CreateBounded<ProgressBeat>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }));
        ch.Writer.TryWrite(beat);
        // Also publish to the hub broadcast channel (best-effort drop on overflow).
        _broadcast.Writer.TryWrite(beat);
    }

    // ===== IProgressBeatBroadcaster (friend seam) =====

    /// <inheritdoc/>
    public ChannelReader<ProgressBeat> Beats => _broadcast.Reader;

    /// <summary>
    /// Releases the per-execution channel slot once the execution has terminated.
    /// Called by <c>JobWorker</c> AFTER the terminal direct progress write.
    /// </summary>
    public void ReleaseExecution(string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        if (_executionInboxes.TryRemove(executionId, out var ch))
        {
            ch.Writer.TryComplete();
        }
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stoppingCts.Token;
        _tickerTask = Task.Run(() => TickerLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task TickerLoopAsync(CancellationToken ct)
    {
        var flushEvery = _options.Value.ProgressFlushInterval;
        using var ticker = new PeriodicTimer(flushEvery);
        try
        {
            // Async-all-the-way; no `.GetAwaiter().GetResult()`.
            while (await ticker.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                await FlushAllOnceAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }

        // Final drain on shutdown uses CancellationToken.None so the terminal flush is not skipped.
        await FlushAllOnceAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task FlushAllOnceAsync(CancellationToken ct)
    {
        foreach (var (id, ch) in _executionInboxes)
        {
            if (!ch.Reader.TryRead(out var beat))
            {
                continue;
            }
            try
            {
                await _writer
                    .UpdateProgressAsync(id, beat.Processed, beat.Failed, beat.Total, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // shutdown — propagated up by the outer loop's catch
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Progress flush failed for {Id}", id);
            }
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts?.Cancel();
        if (_tickerTask is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutdown
            }
        }
    }

    /// <inheritdoc/>
    /// <remarks>Idempotent: clears the CTS reference after the first pass so a second call is a no-op.</remarks>
    public async ValueTask DisposeAsync()
    {
        var cts = Interlocked.Exchange(ref _stoppingCts, null);
        cts?.Cancel();
        if (_tickerTask is { } t)
        {
            try
            {
                await t.ConfigureAwait(false);
            }
            catch
            {
                // best-effort
            }
        }
        cts?.Dispose();
    }
}
