using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UKBatch.Runtime;

/// <summary>
/// Single <see cref="IHostedService"/> coordinator for the UKBatch runtime.
/// Owns the linked stopping CTS, the worker tasks, the scheduler task, the awaiter task, and the
/// progress-flusher task. Shutdown order is strict: the scheduler stops BEFORE workers drain.
/// </summary>
internal sealed class UKBatchHost : IHostedService, IAsyncDisposable
{
    private readonly IServiceProvider _rootProvider;
    private readonly IOptions<UKBatchOptions> _options;
    private readonly JobDispatcher _dispatcher;
    private readonly JobScheduler _scheduler;
    private readonly BatchScheduler _batchScheduler;
    private readonly DebouncedProgressFlusher _progressFlusher;
    private readonly JobExecutionAwaiter _awaiter;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly ILogger<UKBatchHost> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task[]? _workerTasks;
    private int _started;

    /// <summary>Constructs the host coordinator.</summary>
    public UKBatchHost(
        IServiceProvider rootProvider,
        IOptions<UKBatchOptions> options,
        JobDispatcher dispatcher,
        JobScheduler scheduler,
        BatchScheduler batchScheduler,
        DebouncedProgressFlusher progressFlusher,
        JobExecutionAwaiter awaiter,
        IHostApplicationLifetime hostLifetime,
        ILogger<UKBatchHost> logger)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(batchScheduler);
        ArgumentNullException.ThrowIfNull(progressFlusher);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(hostLifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _rootProvider = rootProvider;
        _options = options;
        _dispatcher = dispatcher;
        _scheduler = scheduler;
        _batchScheduler = batchScheduler;
        _progressFlusher = progressFlusher;
        _awaiter = awaiter;
        _hostLifetime = hostLifetime;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // One-shot, atomically: a duplicate start would overwrite the stopping source (leaving
        // the first worker set unstoppable) and double the workers on the same dispatcher.
        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            _logger.LogWarning("UKBatchHost.StartAsync called more than once; ignoring the duplicate start.");
            return;
        }

        // Validate options eagerly (IValidateOptions runs lazily — touching the Value forces it).
        _ = _options.Value;

        // Link BOTH the application-stopping token and the startup token (the BackgroundService
        // pattern): an abort during startup must reach the child services and worker loops too.
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime.ApplicationStopping, cancellationToken);

        await _progressFlusher.StartAsync(_stoppingCts.Token).ConfigureAwait(false);
        await _awaiter.StartAsync(_stoppingCts.Token).ConfigureAwait(false);
        await _scheduler.StartAsync(_stoppingCts.Token).ConfigureAwait(false);
        await _batchScheduler.StartAsync(_stoppingCts.Token).ConfigureAwait(false);

        var workerCount = _options.Value.MaxDegreeOfParallelism;
        _workerTasks = new Task[workerCount];
        // ONE singleton JobWorker instance runs all N loops in parallel. That is safe because the
        // worker is stateless by design: every field is a readonly dependency, and all
        // per-execution state lives in locals and the per-execution DI scope it opens. A mutable
        // instance field added to JobWorker would be shared by all N loops — keep it stateless.
        var worker = _rootProvider.GetRequiredService<JobWorker>();
        for (var i = 0; i < workerCount; i++)
        {
            var idx = i;
            _workerTasks[i] = Task.Run(() => worker.RunAsync(idx, _stoppingCts.Token), CancellationToken.None);
        }
        _logger.LogInformation("UKBatch started ({Workers} workers).", workerCount);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting new triggers first, then stop the scheduler, then drain workers.
        _dispatcher.StopAcceptingTriggers();

        try
        {
            await _scheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A faulted scheduler loop must not abort the shutdown chain: the workers still need
            // their cancel + drain, the dispatcher its completion, and the flusher/awaiter their
            // stops. Loud, then continue.
            _logger.LogError(ex, "Scheduler stop failed; continuing host shutdown.");
        }

        try
        {
            await _batchScheduler.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Independent of the job scheduler above: one faulted scheduler must not stop the other
            // from draining, nor abort the worker/dispatcher teardown that follows. Loud, then continue.
            _logger.LogError(ex, "Batch scheduler stop failed; continuing host shutdown.");
        }

        _stoppingCts?.Cancel();

        if (_workerTasks is { } workers)
        {
            try
            {
                await Task.WhenAll(workers).WaitAsync(_options.Value.ShutdownTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Workers did not drain in {Timeout}; in-flight executions remain in Cancelling state.",
                    _options.Value.ShutdownTimeout);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // host cancelled the StopAsync grace period — continue cleanup. Workers exit
                // NORMALLY on the stopping token (they swallow their own cancellation), so any
                // other cancellation here is a genuine fault and stays loud.
            }
        }

        _dispatcher.Complete();

        await _progressFlusher.StopAsync(cancellationToken).ConfigureAwait(false);
        await _awaiter.StopAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("UKBatch stopped.");
    }

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        // DI container owns the disposal of <see cref="DebouncedProgressFlusher"/> and
        // <see cref="JobExecutionAwaiter"/> — both are singleton <c>IAsyncDisposable</c>s the
        // host container disposes during shutdown. Calling their DisposeAsync here too would
        // double-dispose their internal CTS and throw <see cref="ObjectDisposedException"/> on
        // the second pass.
        _stoppingCts?.Cancel();
        _stoppingCts?.Dispose();
        _stoppingCts = null;
        return ValueTask.CompletedTask;
    }
}
