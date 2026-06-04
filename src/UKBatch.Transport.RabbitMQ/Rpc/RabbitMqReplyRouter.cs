using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UKBatch.Abstractions.Models;
using UKBatch.Transport.RabbitMQ.Connection;

namespace UKBatch.Transport.RabbitMQ.Rpc;

/// <summary>
/// Orchestrator-side RPC reply router. Owns the <c>amq.rabbitmq.reply-to</c> direct-reply-to
/// consumer and the pending-reply registry that <see cref="RabbitMqTransport.RequestReplyAsync"/>
/// registers correlation ids into. On a reply delivery it matches
/// <c>BasicProperties.CorrelationId</c> → completes the awaiting <see cref="TaskCompletionSource{T}"/>
/// (mirrors <c>InProcessTransport.CompleteReply</c>).
/// </summary>
/// <remarks>
/// <para><b>Same channel:</b> <c>amq.rabbitmq.reply-to</c> is strictly
/// <i>channel</i>-scoped — the broker accepts a <c>reply-to: amq.rabbitmq.reply-to</c> request publish
/// ONLY on the channel that consumes the pseudo-queue (any other channel is rejected with
/// <c>PRECONDITION_FAILED - fast reply consumer does not exist</c>), and routes the reply back only over
/// that same channel. The router therefore both <b>publishes the RPC request</b>
/// (<see cref="PublishRequestAsync"/>) and consumes the reply on this one channel. Publisher confirms are
/// enabled so <c>mandatory:true</c> fails fast on an unroutable target. Consume dispatch is serialized
/// by <c>ConsumerDispatchConcurrency=1</c> and request publishes by <c>_publishLock</c> (the channel is not
/// thread-safe).</para>
/// <para><b>autoAck=true:</b> direct-reply-to deliveries MUST be auto-acked — they cannot be
/// nacked/requeued (the pseudo-queue has no real backing). A lost reply (e.g. a connection drop) leaves
/// the pending request to time out; idempotent broker redelivery of the original request compensates.</para>
/// <para><b>Lazy start:</b> <see cref="EnsureStartedAsync"/> is idempotent and double-checked under a
/// lock; the first <see cref="RabbitMqTransport.RequestReplyAsync"/> starts the consumer.</para>
/// </remarks>
public sealed class RabbitMqReplyRouter : IAsyncDisposable
{
    /// <summary>The RabbitMQ built-in direct-reply-to pseudo-queue (no declare; consume autoAck=true).</summary>
    internal const string DirectReplyToQueue = "amq.rabbitmq.reply-to";

    private readonly RabbitMqConnectionManager _connection;
    private readonly ILogger<RabbitMqReplyRouter> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JobResult>> _pendingReplies =
        new(StringComparer.Ordinal);

    private readonly SemaphoreSlim _startLock = new(1, 1);

    /// <summary>
    /// Serializes RPC-request publishes on <see cref="_replyChannel"/>. The same channel
    /// holds the direct-reply-to consumer AND must publish the request (direct-reply-to is strictly
    /// channel-scoped — the broker rejects a <c>reply-to: amq.rabbitmq.reply-to</c> publish on any other
    /// channel with <c>PRECONDITION_FAILED - fast reply consumer does not exist</c>). <see cref="IChannel"/>
    /// is NOT thread-safe and this channel also dispatches consumed replies, so publishes are serialized
    /// here (consume dispatch is already serialized by <c>ConsumerDispatchConcurrency=1</c>, validator-capped).
    /// </summary>
    private readonly SemaphoreSlim _publishLock = new(1, 1);
    private IChannel? _replyChannel;
    private string? _consumerTag;
    private long _observedGeneration = -1;
    private int _disposed;

    /// <summary>Constructs the reply router.</summary>
    public RabbitMqReplyRouter(
        RabbitMqConnectionManager connection,
        ILogger<RabbitMqReplyRouter> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _logger = logger;
    }

    /// <summary>
    /// Registers a pending reply slot for <paramref name="correlationId"/>. Throws
    /// <see cref="InvalidOperationException"/> if the id is already pending (duplicate MessageId in
    /// flight). The returned task completes when the matching reply arrives (or is cancelled by the
    /// caller's linked CTS in <see cref="RabbitMqTransport.RequestReplyAsync"/>).
    /// </summary>
    public TaskCompletionSource<JobResult> RegisterPending(string correlationId)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        var tcs = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingReplies.TryAdd(correlationId, tcs))
        {
            throw new InvalidOperationException(
                $"Duplicate correlationId '{correlationId}' for RequestReplyAsync — a request with this MessageId is already in flight.");
        }

        return tcs;
    }

    /// <summary>Removes a pending reply slot (the RPC finally-block always calls this).</summary>
    public void RemovePending(string correlationId)
    {
        if (string.IsNullOrEmpty(correlationId))
        {
            return;
        }

        _pendingReplies.TryRemove(correlationId, out _);
    }

    /// <summary>
    /// Ensures the direct-reply-to consumer is running on the singleton connection. Idempotent and
    /// thread-safe.
    /// </summary>
    public async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var generation = _connection.Generation;
        if (_replyChannel is { IsOpen: true } && Volatile.Read(ref _observedGeneration) == generation)
        {
            return;
        }

        await _startLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

            generation = _connection.Generation;
            if (_replyChannel is { IsOpen: true } && Volatile.Read(ref _observedGeneration) == generation)
            {
                return;
            }

            // The connection auto-recovered since we last armed (Generation bumped), or the
            // channel closed. direct-reply-to consumers are NOT restored by TopologyRecovery (pseudo-queue),
            // so a recovered channel may report IsOpen yet no longer be subscribed → replies silently dropped
            // and every subsequent RPC times out. Tear the stale channel down and re-consume on a fresh one.
            if (_replyChannel is not null)
            {
                await SafeCloseChannelAsync(_replyChannel).ConfigureAwait(false);
                _replyChannel = null;
                _consumerTag = null;
            }

            // This channel now publishes the RPC REQUEST too (direct-reply-to is channel-scoped
            // — the request whose reply-to is the pseudo-queue MUST be published on the channel that consumes
            // it). Publisher confirms are enabled so mandatory:true + unroutable target raises a
            // PublishException(IsReturn=true) — fail-fast instead of hanging to the timeout.
            var channel = await _connection.OpenChannelAsync(confirmsEnabled: true, cancellationToken)
                .ConfigureAwait(false);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += OnReplyReceivedAsync;

            // autoAck=true: direct-reply-to deliveries cannot be acked/nacked explicitly.
            _consumerTag = await channel.BasicConsumeAsync(
                queue: DirectReplyToQueue,
                autoAck: true,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            _replyChannel = channel;
            Volatile.Write(ref _observedGeneration, generation);
            _logger.LogDebug(
                "RabbitMQ reply router consuming {Queue} (consumerTag={ConsumerTag}, generation={Generation}).",
                DirectReplyToQueue, _consumerTag, generation);
        }
        finally
        {
            _startLock.Release();
        }
    }

    /// <summary>
    /// Publishes an RPC request on the reply router's OWN channel — the one holding the
    /// <c>amq.rabbitmq.reply-to</c> consumer. Direct-reply-to is strictly channel-scoped:
    /// the broker only accepts a <c>reply-to: amq.rabbitmq.reply-to</c> publish on the channel that consumes
    /// that pseudo-queue; publishing it on any other channel is rejected with
    /// <c>PRECONDITION_FAILED - fast reply consumer does not exist</c>. Serialized through
    /// <see cref="_publishLock"/> (the channel also dispatches consumed replies and <see cref="IChannel"/> is
    /// not thread-safe). With publisher confirms enabled, <paramref name="mandatory"/><c>=true</c> + an
    /// unroutable target raises a <c>PublishException</c> (fail-fast) which is NOT swallowed.
    /// <see cref="EnsureStartedAsync"/> MUST have been called first (the caller does so).
    /// </summary>
    public async Task PublishRequestAsync(
        string exchange,
        string routingKey,
        bool mandatory,
        BasicProperties properties,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exchange);
        ArgumentException.ThrowIfNullOrEmpty(routingKey);
        ArgumentNullException.ThrowIfNull(properties);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var channel = _replyChannel
            ?? throw new InvalidOperationException(
                "RabbitMqReplyRouter.PublishRequestAsync called before EnsureStartedAsync — the reply channel is not armed.");

        await _publishLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await channel.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: mandatory,
                basicProperties: properties,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _publishLock.Release();
        }
    }

    /// <summary>
    /// Tears down a stale reply channel (best-effort cancel + close + dispose) before re-arming on a fresh
    /// one after broker auto-recovery. Failures are swallowed — the channel is being replaced.
    /// </summary>
    private async Task SafeCloseChannelAsync(IChannel channel)
    {
        try
        {
            if (_consumerTag is not null)
            {
                await channel.BasicCancelAsync(_consumerTag, noWait: false, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RabbitMQ reply router: stale channel teardown threw during re-arm (ignored).");
        }
        await channel.DisposeAsync().ConfigureAwait(false);
    }

    private Task OnReplyReceivedAsync(object sender, BasicDeliverEventArgs ea)
    {
        var correlationId = ea.BasicProperties.CorrelationId;
        if (string.IsNullOrEmpty(correlationId))
        {
            _logger.LogWarning("RabbitMQ reply router: reply with no CorrelationId — dropped.");
            return Task.CompletedTask;
        }

        JobResult? result;
        try
        {
            result = JsonSerializer.Deserialize<JobResult>(ea.Body.Span, RabbitMqTransport.JsonOpts);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex, "RabbitMQ reply router: malformed JobResult for CorrelationId={CorrelationId} — dropped.",
                correlationId);
            return Task.CompletedTask;
        }

        if (result is null)
        {
            _logger.LogWarning(
                "RabbitMQ reply router: null JobResult for CorrelationId={CorrelationId} — dropped.",
                correlationId);
            return Task.CompletedTask;
        }

        if (_pendingReplies.TryGetValue(correlationId, out var tcs))
        {
            tcs.TrySetResult(result);
        }
        else
        {
            // Already timed out / cancelled / unknown — benign (mirrors InProcessTransport.CompleteReply).
            _logger.LogWarning(
                "RabbitMQ reply router: no pending request for CorrelationId={CorrelationId} "
                + "(already timed-out, cancelled, or unknown).",
                correlationId);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        var channel = _replyChannel;
        if (channel is not null)
        {
            try
            {
                if (_consumerTag is not null)
                {
                    await channel.BasicCancelAsync(_consumerTag, noWait: false, CancellationToken.None)
                        .ConfigureAwait(false);
                }
                await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RabbitMQ reply router close threw during dispose (ignored).");
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }

        // Fail any stragglers so awaiting RPCs unblock rather than hang to their timeout.
        foreach (var kv in _pendingReplies)
        {
            kv.Value.TrySetCanceled();
        }
        _pendingReplies.Clear();

        _startLock.Dispose();
        _publishLock.Dispose();
    }
}
