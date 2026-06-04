using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Orchestrates eager startup connect for every registered UKBatch service. Per-service
/// circuit-breaker prevents hot-loop on flapping services.
/// </summary>
/// <remarks>
/// <para><b>Startup contract:</b> <see cref="StartAsync"/> resolves every <see cref="IUKBatchClient"/>
/// from the factory (which instantiates them lazily) and calls <c>ConnectAsync</c> in parallel
/// with per-service try/catch. ONE service failure does NOT block other connects.</para>
/// <para><b>Shutdown contract:</b> <see cref="StopAsync"/> awaits <c>DisconnectAsync</c> on every
/// client with a 5-second timeout each.</para>
/// <para><b>Initial-connect retry timer:</b> a 60s <see cref="PeriodicTimer"/> on a
/// background task re-attempts <c>ConnectAsync</c> on every client whose
/// <c>State == UKBatchClientState.Disconnected</c>. This closes the failure mode where
/// <see cref="StartAsync"/> failed on a service that was briefly unreachable at host startup.
/// <c>WithAutomaticReconnect</c> only triggers on a CLOSED connection that was previously OPENED —
/// it does NOT cover the never-opened case.</para>
/// <para><b>Idempotency:</b> <see cref="StartAsync"/> early-returns if <c>_stoppingCts != null</c>
/// (defense-in-depth; <c>AddUKBatchDashboard</c> idempotency guard is the primary line).</para>
/// </remarks>
internal sealed class UKBatchServiceConductor : IHostedService, IAsyncDisposable
{
    private readonly IUKBatchClientFactory _factory;
    private readonly IUKBatchServiceRegistry _registry;
    private readonly ILogger<UKBatchServiceConductor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retryInterval;
    private CancellationTokenSource? _stoppingCts;
    private PeriodicTimer? _retryTimer;
    private Task? _retryLoop;

    public UKBatchServiceConductor(
        IUKBatchClientFactory factory,
        IUKBatchServiceRegistry registry,
        ILogger<UKBatchServiceConductor> logger)
        : this(factory, registry, logger, TimeProvider.System, TimeSpan.FromSeconds(60))
    {
    }

    /// <summary>Test-only constructor: inject a <see cref="TimeProvider"/> (e.g. <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) and a smaller interval.</summary>
    internal UKBatchServiceConductor(
        IUKBatchClientFactory factory,
        IUKBatchServiceRegistry registry,
        ILogger<UKBatchServiceConductor> logger,
        TimeProvider timeProvider,
        TimeSpan retryInterval)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _registry = registry;
        _logger = logger;
        _timeProvider = timeProvider;
        _retryInterval = retryInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is not null) return Task.CompletedTask; // idempotency guard
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var connectTasks = _registry.All().Select(async descriptor =>
        {
            try
            {
                var client = _factory.GetClient(descriptor.Name);
                await client.ConnectAsync(_stoppingCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Do NOT propagate — degraded UI > entire host failing to start.
                _logger.LogWarning(ex, "Initial connect failed for service {Service}; ConnectionBanner will show 'Disconnected'.",
                    descriptor.Name);
            }
        });

        // Start the 60s retry loop AFTER the parallel initial connects fire.
        _retryTimer = new PeriodicTimer(_retryInterval, _timeProvider);
        var loopCt = _stoppingCts.Token;
        _retryLoop = Task.Run(() => RetryDisconnectedClientsLoopAsync(loopCt), loopCt);

        // Parallel-but-isolated. ONE service down does NOT block others.
        return Task.WhenAll(connectTasks);
    }

    private async Task RetryDisconnectedClientsLoopAsync(CancellationToken ct)
    {
        try
        {
            while (await _retryTimer!.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                foreach (var descriptor in _registry.All())
                {
                    try
                    {
                        var client = _factory.GetClient(descriptor.Name);
                        if (client.State != UKBatchClientState.Disconnected) continue;
                        await client.ConnectAsync(ct).ConfigureAwait(false);
                        _logger.LogInformation("Retry-connect recovered service {Service}.", descriptor.Name);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "Retry-connect still failing for {Service}; next cycle in {Interval}s.",
                            descriptor.Name, _retryInterval.TotalSeconds);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is null) return;
        try { _stoppingCts.Cancel(); } catch (ObjectDisposedException) { }

        if (_retryLoop is not null)
        {
            try
            {
                await _retryLoop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Retry loop wound down with exception (ignored on shutdown).");
            }
        }
        _retryTimer?.Dispose();
        _retryTimer = null;

        var snapshot = _factory is UKBatchClientFactory concrete
            ? concrete.SnapshotClients().Cast<IUKBatchClient>().ToArray()
            : _registry.All().Select(d => _factory.GetClient(d.Name)).ToArray();

        var disconnectTasks = snapshot.Select(async client =>
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                await client.DisconnectAsync(linked.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Disconnect failed for service {Service}; ignoring on shutdown.",
                    client.Service.Name);
            }
        });
        await Task.WhenAll(disconnectTasks).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_stoppingCts is not null)
        {
            try { _stoppingCts.Cancel(); } catch (ObjectDisposedException) { }
            _stoppingCts.Dispose();
            _stoppingCts = null;
        }
        _retryTimer?.Dispose();
        _retryTimer = null;
        if (_factory is IAsyncDisposable disposableFactory)
        {
            await disposableFactory.DisposeAsync().ConfigureAwait(false);
        }
    }
}
