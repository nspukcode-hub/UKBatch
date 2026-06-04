using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Runtime;

/// <summary>
/// Implementation of <see cref="IJobExecutionAwaiter"/>. Owns ONE
/// <see cref="IJobExecutionReader.WatchAsync"/> subscription for the entire process; multiplexes
/// terminal events to per-execution <see cref="TaskCompletionSource{T}"/>s held in a
/// <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// </summary>
internal sealed class JobExecutionAwaiter : IJobExecutionAwaiter, IHostedService, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JobExecution>> _waiters
        = new(StringComparer.Ordinal);
    private readonly IJobExecutionReader _reader;
    private readonly ILogger<JobExecutionAwaiter> _logger;
    private CancellationTokenSource? _stoppingCts;
    private Task? _watchTask;

    /// <summary>Constructs the awaiter; the reader is the same instance the runtime uses elsewhere.</summary>
    public JobExecutionAwaiter(IJobExecutionReader reader, ILogger<JobExecutionAwaiter> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(logger);
        _reader = reader;
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _stoppingCts.Token;
        _watchTask = Task.Run(() => WatchLoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task WatchLoopAsync(CancellationToken ct)
    {
        try
        {
            // Large buffer so the awaiter never blocks the store's publisher; backpressure is
            // the safe overflow policy for terminal-event correctness.
            var watchOptions = new WatchOptions
            {
                OverflowPolicy = WatchOverflowPolicy.Backpressure,
                BufferCapacity = 65536,
            };
            await foreach (var ex in _reader.WatchAsync(watchOptions, ct).ConfigureAwait(false))
            {
                if (!BatchStateMachine.IsTerminal(ex.Status))
                {
                    continue;
                }
                if (_waiters.TryRemove(ex.ExecutionId, out var tcs))
                {
                    tcs.TrySetResult(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // shutdown
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JobExecutionAwaiter watch loop terminated unexpectedly.");
        }
    }

    /// <inheritdoc/>
    public Task<JobExecution> WaitForTerminalAsync(string executionId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        // GetOrAdd is synchronous so by the time this method returns the waiter is observable
        // to the watch loop.
        var tcs = _waiters.GetOrAdd(executionId, static _ =>
            new TaskCompletionSource<JobExecution>(TaskCreationOptions.RunContinuationsAsynchronously));

        // The CancellationTokenRegistration is disposed when the TCS completes (any outcome).
        // Without this, every cancelled wait leaks a registration until the CT itself is GC'd —
        // measurable under high concurrent-waiter counts.
        var reg = cancellationToken.Register(static state =>
        {
            var (waiters, id, tcs, tok) = ((ConcurrentDictionary<string, TaskCompletionSource<JobExecution>>, string, TaskCompletionSource<JobExecution>, CancellationToken))state!;
            waiters.TryRemove(id, out _);
            tcs.TrySetCanceled(tok);
        }, (_waiters, executionId, tcs, cancellationToken));

        tcs.Task.ContinueWith(
            static (_, regObj) => ((CancellationTokenRegistration)regObj!).Dispose(),
            reg,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        // Catch-up against a pre-existing terminal row. The 4-step awaiter-before-trigger pattern
        // protects BatchExecutor and ParallelGroupRunner from missing a terminal event, but public
        // callers using IJobRunner.TriggerAsync then IJobExecutionAwaiter directly can hit a race
        // window where the worker reaches terminal BEFORE the waiter is registered.
        // The watch loop's TryRemove would find no waiter and silently drop the event, deadlocking
        // the caller. The catch-up reads the current row and completes the TCS directly if it is
        // already terminal — fully idempotent with the watch loop because both paths use the same
        // _waiters.TryRemove + TrySetResult contract, and TaskCompletionSource is single-shot.
        _ = CatchUpAsync(executionId, tcs);

        return tcs.Task;
    }

    /// <summary>
    /// Reads the current row and completes the waiter if the execution is already in a terminal
    /// state. Idempotent with the watch loop; best-effort on errors (the watch loop remains the
    /// primary completion path).
    /// </summary>
    private async Task CatchUpAsync(string executionId, TaskCompletionSource<JobExecution> tcs)
    {
        try
        {
            // CT.None — the catch-up read MUST NOT be cancelled by the caller's CT or it would leave
            // the waiter dangling under cancellation. The TCS cancellation registration above handles
            // the caller-cancellation path independently.
            var current = await _reader.GetAsync(executionId, CancellationToken.None).ConfigureAwait(false);
            if (current is not null && BatchStateMachine.IsTerminal(current.Status))
            {
                if (_waiters.TryRemove(executionId, out var existing) && ReferenceEquals(existing, tcs))
                {
                    tcs.TrySetResult(current);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "JobExecutionAwaiter catch-up read failed for {ExecutionId}; watch loop remains the primary completion path.", executionId);
        }
    }

    /// <inheritdoc/>
    public void CancelWaiter(string executionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        // Idempotent — TryRemove returns false if no waiter is registered (e.g. trigger succeeded
        // and the watch loop already drained the entry, or CancelWaiter is called twice). The
        // TrySetCanceled in turn triggers the ContinueWith cleanup in WaitForTerminalAsync, which
        // disposes the CancellationTokenRegistration. CT.None — this cleanup must not throw OCE.
        if (_waiters.TryRemove(executionId, out var tcs))
        {
            tcs.TrySetCanceled(CancellationToken.None);
        }
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stoppingCts?.Cancel();
        if (_watchTask is { } t)
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
        if (_watchTask is { } t)
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
