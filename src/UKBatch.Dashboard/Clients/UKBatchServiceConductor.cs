using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Clients;

/// <summary>
/// Orchestrates startup connect for every registered UKBatch service. The initial connect is deferred
/// until the host has finished starting, and a retry loop recovers services still unreachable.
/// </summary>
/// <remarks>
/// <para><b>Startup contract:</b> <see cref="StartAsync"/> registers an
/// <see cref="IHostApplicationLifetime.ApplicationStarted"/> callback that resolves every
/// <see cref="IUKBatchClient"/> and calls <c>ConnectAsync</c> in parallel with per-service try/catch.
/// Deferring to ApplicationStarted matters for an embedded dashboard: its SignalR hub is served by the
/// same host, so connecting before the server is listening would fail and surface a false
/// "Disconnected" banner on first load. ONE service failure does NOT block other connects.</para>
/// <para><b>Shutdown contract:</b> <see cref="StopAsync"/> awaits <c>DisconnectAsync</c> on every
/// client with a 5-second timeout each.</para>
/// <para><b>Retry timer:</b> a <see cref="PeriodicTimer"/> on a background task re-attempts
/// <c>ConnectAsync</c> on every client whose <c>State == UKBatchClientState.Disconnected</c>. This
/// recovers a service that was unreachable at startup (e.g. a remote worker not up yet).
/// <c>WithAutomaticReconnect</c> only triggers on a CLOSED connection that was previously OPENED — it
/// does NOT cover the never-opened case, so this retry is required.</para>
/// <para><b>Idempotency:</b> <see cref="StartAsync"/> early-returns if <c>_stoppingCts != null</c>
/// (defense-in-depth; <c>AddUKBatchDashboard</c> idempotency guard is the primary line).</para>
/// </remarks>
internal sealed class UKBatchServiceConductor : IHostedService, IAsyncDisposable
{
    private readonly IUKBatchClientFactory _factory;
    private readonly IUKBatchServiceRegistry _registry;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<UKBatchServiceConductor> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _retryInterval;
    private CancellationTokenSource? _stoppingCts;
    private CancellationTokenRegistration _startedRegistration;
    private PeriodicTimer? _retryTimer;
    private Task? _retryLoop;

    public UKBatchServiceConductor(
        IUKBatchClientFactory factory,
        IUKBatchServiceRegistry registry,
        IHostApplicationLifetime lifetime,
        ILogger<UKBatchServiceConductor> logger)
        : this(factory, registry, lifetime, logger, TimeProvider.System, TimeSpan.FromSeconds(5))
    {
    }

    /// <summary>Test-only constructor: inject a <see cref="TimeProvider"/> (e.g. <c>Microsoft.Extensions.Time.Testing.FakeTimeProvider</c>) and a smaller interval.</summary>
    internal UKBatchServiceConductor(
        IUKBatchClientFactory factory,
        IUKBatchServiceRegistry registry,
        IHostApplicationLifetime lifetime,
        ILogger<UKBatchServiceConductor> logger,
        TimeProvider timeProvider,
        TimeSpan retryInterval)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _factory = factory;
        _registry = registry;
        _lifetime = lifetime;
        _logger = logger;
        _timeProvider = timeProvider;
        _retryInterval = retryInterval;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_stoppingCts is not null) return Task.CompletedTask; // idempotency guard
        _stoppingCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _stoppingCts.Token;

        // Defer the initial connect until the host has finished starting (server listening). An embedded
        // dashboard reaches its own loopback hub; connecting before the server is serving would fail and
        // surface a false "Disconnected" banner on first load. ApplicationStarted fires after the host is
        // up; if it has already fired, Register invokes the callback synchronously.
        _startedRegistration = _lifetime.ApplicationStarted.Register(() => _ = ConnectAllAsync(ct));

        // Retry loop for services still unreachable after startup (e.g. a remote worker not up yet).
        _retryTimer = new PeriodicTimer(_retryInterval, _timeProvider);
        _retryLoop = Task.Run(() => RetryDisconnectedClientsLoopAsync(ct), ct);

        return Task.CompletedTask;
    }

    /// <summary>Connects every registered service in parallel, isolating per-service failures.</summary>
    private Task ConnectAllAsync(CancellationToken ct)
    {
        var connectTasks = _registry.All().Select(async descriptor =>
        {
            try
            {
                var client = _factory.GetClient(descriptor.Name);
                await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutting down before/while connecting — nothing to do.
            }
            catch (Exception ex)
            {
                // Do NOT propagate — degraded UI > entire host failing to start. The retry loop reattempts.
                _logger.LogWarning(ex, "Initial connect failed for service {Service}; the retry loop will reattempt.",
                    descriptor.Name);
            }
        });
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
        _startedRegistration.Dispose();
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
        _startedRegistration.Dispose();
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
