using System.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UKBatch.Transport.RabbitMQ.Resilience;
using UKBatch.Transport.RabbitMQ.Topology;

namespace UKBatch.Transport.RabbitMQ.Connection;

/// <summary>
/// Owns the singleton broker <see cref="IConnection"/> and the lock-protected confirm-channel used for
/// publish + (orchestrator-side request) publish. Consumer + reply-consumer channels are
/// opened on demand by their respective owners (consumer pump / reply router) via
/// <see cref="OpenChannelAsync"/>.
/// </summary>
/// <remarks>
/// <para><b>Channel strategy:</b> <see cref="IChannel"/> is NOT thread-safe. Publish + reply share a
/// single confirm-channel guarded by a <see cref="SemaphoreSlim"/>; every <c>BasicPublishAsync</c> is
/// awaited to the broker ack (publisher confirms tracking enabled) — sequential, lower throughput, but
/// duplicate-safe and sufficient for v0.1.</para>
/// <para><b>Recovery:</b> <see cref="ConnectionFactory.AutomaticRecoveryEnabled"/> +
/// <see cref="ConnectionFactory.TopologyRecoveryEnabled"/> re-establish the connection, channels,
/// declared topology and consumers after a broker drop.</para>
/// <para><b>Lazy connect:</b> the first call to <see cref="EnsureConnectedAsync"/> opens the connection
/// (under a connect lock) and the confirm-channel. Initial-connect resilience (Polly) is layered by the
/// caller (which wires <c>RabbitMqResiliencePipeline</c>).</para>
/// </remarks>
public sealed class RabbitMqConnectionManager : IAsyncDisposable
{
    private readonly IOptions<RabbitMqTransportOptions> _options;
    private readonly IOptions<UKBatchOptions> _coreOptions;
    private readonly ILogger<RabbitMqConnectionManager> _logger;

    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _confirmLock = new(1, 1);

    private RabbitMqResiliencePipeline? _resilience;
    private IConnection? _connection;
    private IChannel? _confirmChannel;
    private long _generation;
    private int _disposed;

    /// <summary>Constructs the connection manager.</summary>
    public RabbitMqConnectionManager(
        IOptions<RabbitMqTransportOptions> options,
        IOptions<UKBatchOptions> coreOptions,
        ILogger<RabbitMqConnectionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(coreOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _coreOptions = coreOptions;
        _logger = logger;
    }

    /// <summary>
    /// This node's service identity: <see cref="UKBatchOptions.ThisServiceName"/> → env var
    /// <c>UKBATCH_SERVICE_NAME</c> → entry assembly name. <c>null</c> when none resolve (sender-only node
    /// that only publishes / does request-reply and never consumes from a service queue).
    /// </summary>
    public string? ThisServiceName => ResolveThisServiceName();

    /// <summary>The configured serialization/topology options. Exposed for collaborators (publish/consume).</summary>
    public RabbitMqTransportOptions Options => _options.Value;

    /// <summary>
    /// Monotonic counter bumped on every successful broker auto-recovery. Collaborators that
    /// hold connection-scoped state which <c>TopologyRecovery</c> does NOT restore — notably the
    /// <c>amq.rabbitmq.reply-to</c> consumer in <see cref="Rpc.RabbitMqReplyRouter"/> — compare this to
    /// detect a recovery and re-arm their consumer on a fresh channel.
    /// </summary>
    public long Generation => Interlocked.Read(ref _generation);

    /// <summary>
    /// Ensures the connection and confirm-channel are open. Idempotent and thread-safe; double-checked
    /// under <see cref="_connectLock"/>.
    /// </summary>
    public async Task EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        if (_connection is { IsOpen: true } && _confirmChannel is { IsOpen: true })
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            if (_connection is not { IsOpen: true })
            {
                var factory = BuildConnectionFactory();
                var clientName = ThisServiceName ?? "ukbatch";
                // Initial-connect resilience (Polly retry + CB). Broker auto-recovery owns reconnects
                // AFTER the first successful connect; this covers the cold-start window only.
                _resilience ??= new RabbitMqResiliencePipeline(_options.Value, _logger);
                IConnection? connection = null;
                await _resilience.ExecuteAsync(
                    async ct =>
                    {
                        connection = await factory
                            .CreateConnectionAsync(clientName, ct)
                            .ConfigureAwait(false);
                    },
                    cancellationToken).ConfigureAwait(false);
                _connection = connection!;
                _connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
                _logger.LogInformation(
                    "RabbitMQ connection established (clientProvidedName={ClientName}).", clientName);
            }

            if (_confirmChannel is not { IsOpen: true })
            {
                // Publisher confirms + tracking enabled → BasicPublishAsync awaits the broker ack and
                // throws PublishException on nack. (ctor: publisherConfirmationsEnabled,
                // publisherConfirmationTrackingEnabled, rateLimiter?, consumerDispatchConcurrency?)
                var confirmOptions = new CreateChannelOptions(
                    publisherConfirmationsEnabled: true,
                    publisherConfirmationTrackingEnabled: true);
                _confirmChannel = await _connection
                    .CreateChannelAsync(confirmOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>
    /// Opens a fresh channel on the (lazily-established) connection. Used by the consumer pump and reply
    /// router for their own dedicated channels. <paramref name="confirmsEnabled"/> controls whether
    /// publisher confirms are tracked: the consumer pump's channel sets it <c>false</c> (it publishes RPC
    /// replies fire-and-forget, no confirm-track needed); the reply router's channel sets it <c>true</c>
    /// (it publishes the RPC REQUEST with <c>mandatory:true</c> and relies on confirms so an unroutable
    /// target fails fast).
    /// </summary>
    public async Task<IChannel> OpenChannelAsync(bool confirmsEnabled, CancellationToken cancellationToken)
    {
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var options = new CreateChannelOptions(
            publisherConfirmationsEnabled: confirmsEnabled,
            publisherConfirmationTrackingEnabled: confirmsEnabled,
            outstandingPublisherConfirmationsRateLimiter: null,
            consumerDispatchConcurrency: _options.Value.ConsumerDispatchConcurrency);
        return await _connection!.CreateChannelAsync(options, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs <paramref name="publishAction"/> against the shared confirm-channel under the publish lock
    /// (the channel is not thread-safe). The action awaits the broker ack via publisher confirms.
    /// </summary>
    public async Task PublishWithConfirmAsync(
        Func<IChannel, CancellationToken, Task> publishAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(publishAction);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        await _confirmLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await publishAction(_confirmChannel!, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _confirmLock.Release();
        }
    }

    /// <summary>Declares the sender-side topology (exchange + DLX + DLQ) on the confirm-channel.</summary>
    public Task DeclareSenderTopologyAsync(CancellationToken cancellationToken) =>
        PublishWithConfirmAsync(
            (ch, ct) => RabbitMqTopology.DeclareSenderTopologyAsync(ch, _options.Value, ct),
            cancellationToken);

    private ConnectionFactory BuildConnectionFactory()
    {
        var o = _options.Value;
        var factory = new ConnectionFactory
        {
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = o.ConsumerDispatchConcurrency,
        };

        if (!string.IsNullOrWhiteSpace(o.Uri))
        {
            factory.Uri = new Uri(o.Uri);
        }
        else
        {
            factory.HostName = o.HostName;
            factory.Port = o.Port;
            factory.VirtualHost = o.VirtualHost;
            factory.UserName = o.UserName;
            factory.Password = o.Password;
            if (o.UseTls)
            {
                factory.Ssl = new SslOption
                {
                    Enabled = true,
                    ServerName = o.HostName,
                };
            }
        }

        return factory;
    }

    private string? ResolveThisServiceName()
    {
        var fromConfig = _coreOptions.Value.ThisServiceName;
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }
        var fromEnv = Environment.GetEnvironmentVariable("UKBATCH_SERVICE_NAME");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }
        return Assembly.GetEntryAssembly()?.GetName().Name;
    }

    /// <summary>
    /// Bumps <see cref="Generation"/> on broker auto-recovery so connection-scoped consumers (the reply
    /// router's direct-reply-to consumer) re-arm — <c>TopologyRecovery</c> does not restore pseudo-queue
    /// consumers.
    /// </summary>
    private Task OnRecoverySucceededAsync(object? sender, AsyncEventArgs e)
    {
        var generation = Interlocked.Increment(ref _generation);
        _logger.LogInformation(
            "RabbitMQ connection auto-recovered (generation={Generation}) — connection-scoped consumers re-arm on next use.",
            generation);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var channel = _confirmChannel;
        if (channel is not null)
        {
            try
            {
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RabbitMQ confirm-channel close threw during dispose (ignored).");
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        var connection = _connection;
        if (connection is not null)
        {
            try
            {
                await connection.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RabbitMQ connection close threw during dispose (ignored).");
            }
            await connection.DisposeAsync().ConfigureAwait(false);
        }

        _connectLock.Dispose();
        _confirmLock.Dispose();
    }
}
