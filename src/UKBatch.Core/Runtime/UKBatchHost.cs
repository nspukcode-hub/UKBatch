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
    private readonly DebouncedProgressFlusher _progressFlusher;
    private readonly JobExecutionAwaiter _awaiter;
    private readonly IHostApplicationLifetime _hostLifetime;
    private readonly ILogger<UKBatchHost> _logger;

    private CancellationTokenSource? _stoppingCts;
    private Task[]? _workerTasks;

    /// <summary>Constructs the host coordinator.</summary>
    public UKBatchHost(
        IServiceProvider rootProvider,
        IOptions<UKBatchOptions> options,
        JobDispatcher dispatcher,
        JobScheduler scheduler,
        DebouncedProgressFlusher progressFlusher,
        JobExecutionAwaiter awaiter,
        IHostApplicationLifetime hostLifetime,
        ILogger<UKBatchHost> logger)
    {
        ArgumentNullException.ThrowIfNull(rootProvider);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(scheduler);
        ArgumentNullException.ThrowIfNull(progressFlusher);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(hostLifetime);
        ArgumentNullException.ThrowIfNull(logger);
        _rootProvider = rootProvider;
        _options = options;
        _dispatcher = dispatcher;
        _scheduler = scheduler;
        _progressFlusher = progressFlusher;
        _awaiter = awaiter;
        _hostLifetime = hostLifetime;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        // Validate options eagerly (IValidateOptions runs lazily — touching the Value forces it).
        _ = _options.Value;

        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(_hostLifetime.ApplicationStopping);

        await _progressFlusher.StartAsync(_stoppingCts.Token).ConfigureAwait(false);
        await _awaiter.StartAsync(_stoppingCts.Token).ConfigureAwait(false);
        await _scheduler.StartAsync(_stoppingCts.Token).ConfigureAwait(false);

        var workerCount = _options.Value.MaxDegreeOfParallelism;
        _workerTasks = new Task[workerCount];
        for (var i = 0; i < workerCount; i++)
        {
            var idx = i;
            // Each worker is resolved from the root provider per-iteration; JobWorker itself is
            // a singleton type but we capture the per-worker index inside the task.
            var worker = _rootProvider.GetRequiredService<JobWorker>();
            _workerTasks[i] = Task.Run(() => worker.RunAsync(idx, _stoppingCts.Token), CancellationToken.None);
        }
        _logger.LogInformation("UKBatch started ({Workers} workers).", workerCount);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting new triggers first, then stop the scheduler, then drain workers.
        _dispatcher.StopAcceptingTriggers();

        await _scheduler.StopAsync().ConfigureAwait(false);

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
            catch (OperationCanceledException)
            {
                // host cancelled the StopAsync grace period — continue cleanup.
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
