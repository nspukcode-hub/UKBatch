using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Runtime;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Dedupe;
using UKBatch.Transport.RabbitMQ.Topology;

namespace UKBatch.Transport.RabbitMQ.Receiver;

/// <summary>
/// Worker-side consumer pump. An <see cref="IHostedService"/> that declares the receiver
/// topology, consumes this node's durable quorum service queue with manual ack, runs each delivered
/// <see cref="JobMessage"/> as a local job, and (for RPC requests) replies on the direct-reply-to
/// queue. Implements the pinned 9-step flow with the <b>requeue-never</b> invariant.
/// </summary>
/// <remarks>
/// <para><b>requeue-never:</b> the only nack is <c>BasicNackAsync(..., requeue:false)</c> for
/// poison messages (undeserializable / missing fields / unregistered job) and the optional
/// max-redelivery defense — those dead-letter to the DLX. A job that runs to <see cref="JobStatus.Failed"/>
/// is ALWAYS acked (step 9) and its failure flows back through the RPC reply; it NEVER returns to the
/// queue and NEVER hits the DLQ.</para>
/// <para><b>Effectively-once:</b> redelivery (consumer crash before ack) is collapsed by the MessageId
/// dedupe cache — a HIT replays the cached <see cref="JobResult"/> and acks without re-running. If the
/// crash happened before the result was stored, the dedupe MISSes and the job re-runs (at-least-once);
/// job idempotency beyond that is the operator's responsibility.</para>
/// <para><b>Orchestrator-only node:</b> when no <c>ThisServiceName</c> resolves the pump skips consuming
/// (no service queue) but still ensures the sender topology + reply router are up.</para>
/// </remarks>
internal sealed class RabbitMqConsumerPump : IHostedService, IDisposable
{
    private readonly RabbitMqConnectionManager _connection;
    private readonly MessageIdDedupeCache _dedupe;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IJobExecutionAwaiter _awaiter;
    private readonly IJobDefinitionLookup _registry;
    private readonly ILogger<RabbitMqConsumerPump> _logger;

    private readonly CancellationTokenSource _stoppingCts = new();
    private readonly CancellationToken _stoppingToken;

    private IChannel? _consumerChannel;
    private string? _consumerTag;
    private int _started;
    private int _disposed;

    /// <summary>Constructs the consumer pump.</summary>
    public RabbitMqConsumerPump(
        RabbitMqConnectionManager connection,
        MessageIdDedupeCache dedupe,
        IServiceScopeFactory scopeFactory,
        IJobExecutionAwaiter awaiter,
        IJobDefinitionLookup registry,
        ILogger<RabbitMqConsumerPump> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(dedupe);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(awaiter);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _dedupe = dedupe;
        _scopeFactory = scopeFactory;
        _awaiter = awaiter;
        _registry = registry;
        _logger = logger;
        // Cache the token now. A CancellationToken (a struct) can be read safely AFTER its source is
        // disposed; reading CancellationTokenSource.Token after Dispose() would throw. In-flight delivery
        // handlers can resume after a concurrent shutdown disposes _stoppingCts, so every handler path
        // reads this cached token instead of _stoppingCts.Token.
        _stoppingToken = _stoppingCts.Token;
    }

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return; // idempotent (defense-in-depth; AddHostedService is single-instance)
        }

        await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);

        var serviceName = _connection.ThisServiceName;
        if (string.IsNullOrWhiteSpace(serviceName))
        {
            // Orchestrator-only node: no service queue to consume. Ensure sender topology exists so
            // RPC publishes route; the reply router starts lazily on the first RequestReplyAsync.
            await _connection.DeclareSenderTopologyAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "RabbitMQ consumer pump: no ThisServiceName resolved — running in sender-only mode (no service queue).");
            return;
        }

        var options = _connection.Options;
        var channel = await _connection.OpenChannelAsync(confirmsEnabled: false, cancellationToken)
            .ConfigureAwait(false);

        var queueName = await RabbitMqTopology
            .DeclareReceiverTopologyAsync(channel, options, serviceName, cancellationToken)
            .ConfigureAwait(false);

        // QoS: bound unacked deliveries in flight (prefetch). global:false → per-consumer.
        await channel.BasicQosAsync(
            prefetchSize: 0,
            prefetchCount: options.PrefetchCount,
            global: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) => OnReceivedAsync(channel, ea);

        _consumerTag = await channel.BasicConsumeAsync(
            queue: queueName,
            autoAck: false,
            consumer: consumer,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _consumerChannel = channel;
        _logger.LogInformation(
            "RabbitMQ consumer pump consuming {Queue} (prefetch={Prefetch}, consumerTag={ConsumerTag}).",
            queueName, options.PrefetchCount, _consumerTag);
    }

    /// <summary>
    /// The pinned 9-step delivery handler. <paramref name="channel"/> is the channel that
    /// delivered the message — RPC replies (step 8) are published from it (direct-reply-to is
    /// strictly channel-scoped; the confirm-channel would silently fail to route the reply).
    /// </summary>
    private async Task OnReceivedAsync(IChannel channel, BasicDeliverEventArgs ea)
    {
        var deliveryTag = ea.DeliveryTag;

        // 1. DESERIALIZE — poison (undeserializable / missing required fields) → DLX, never ran.
        var message = TryDeserialize(ea.Body);
        if (message is null || string.IsNullOrEmpty(message.MessageId) || string.IsNullOrEmpty(message.JobName))
        {
            _logger.LogWarning(
                "RabbitMQ consumer: poison message (deserialize failed or missing MessageId/JobName) "
                + "deliveryTag={DeliveryTag} — dead-lettering (requeue:false).",
                deliveryTag);
            await NackToDeadLetterAsync(channel, deliveryTag).ConfigureAwait(false);
            return;
        }

        // 2. MAX-REDELIVERY — defense-in-depth log ONLY (the broker's quorum x-delivery-limit
        //    enforces the cap and auto-dead-letters; client-side counting is NOT authoritative).
        if (TryGetDeliveryCount(ea, out var deliveryCount) && deliveryCount > _connection.Options.MaxRedeliveryCount)
        {
            _logger.LogWarning(
                "RabbitMQ consumer: MessageId {MessageId} x-delivery-count={Count} exceeds MaxRedeliveryCount={Max} "
                + "(broker enforces this; processing anyway).",
                message.MessageId, deliveryCount, _connection.Options.MaxRedeliveryCount);
        }

        // 3. DEDUPE — HIT (already seen) → replay cached reply (if any), ack, NO re-run.
        if (!_dedupe.TryAdd(message.MessageId))
        {
            if (_dedupe.TryGetResult(message.MessageId, out var cached) && cached is not null)
            {
                _logger.LogDebug(
                    "RabbitMQ consumer: dedupe HIT for MessageId {MessageId} — replaying cached result.",
                    message.MessageId);
                await ReplyIfRequestedAsync(channel, ea, cached).ConfigureAwait(false);
            }
            else
            {
                _logger.LogDebug(
                    "RabbitMQ consumer: dedupe HIT for MessageId {MessageId} but no stored result "
                    + "(in-flight race) — acking without re-run.",
                    message.MessageId);
            }

            await channel.BasicAckAsync(deliveryTag, multiple: false, _stoppingToken).ConfigureAwait(false);
            return;
        }

        // Steps 4–9 run under a guard: any UNEXPECTED throw after the successful dedupe
        // TryAdd must EVICT the dedupe key (un-poison) and dead-letter — otherwise a resultless dedupe HIT
        // on redelivery would ack-without-running and SILENTLY DROP the job. Shutdown-OCE is excluded: it
        // is handled inside step 6 and the in-memory dedupe cache is cleared by the process restart anyway.
        try
        {
            await ProcessRegisteredDeliveryAsync(channel, ea, message, deliveryTag).ConfigureAwait(false);
        }
        catch (Exception ex) when (!(_stoppingCts.IsCancellationRequested && ex is OperationCanceledException))
        {
            _dedupe.Evict(message.MessageId);
            _logger.LogError(
                ex,
                "RabbitMQ consumer: unhandled error processing MessageId {MessageId} — evicted dedupe entry + dead-lettering.",
                message.MessageId);
            try
            {
                await NackToDeadLetterAsync(channel, deliveryTag).ConfigureAwait(false);
            }
            catch (Exception nackEx)
            {
                _logger.LogDebug(
                    nackEx, "RabbitMQ consumer: nack-to-DLX after processing error also failed (channel faulted).");
            }
        }
    }

    /// <summary>
    /// Steps 4–9 of the pinned flow, run after a successful dedupe <c>TryAdd</c>. Extracted so
    /// <see cref="OnReceivedAsync"/> can guard it: an unexpected throw must evict the dedupe key and
    /// dead-letter the delivery (otherwise a resultless dedupe HIT silently drops the job).
    /// </summary>
    private async Task ProcessRegisteredDeliveryAsync(
        IChannel channel, BasicDeliverEventArgs ea, JobMessage message, ulong deliveryTag)
    {
        // 4. JOB REGISTRY guard — unregistered job is poison → DLX.
        if (_registry.TryGet(message.JobName) is null)
        {
            _logger.LogWarning(
                "RabbitMQ consumer: job '{JobName}' (MessageId {MessageId}) is not registered on this worker "
                + "— dead-lettering (requeue:false).",
                message.JobName, message.MessageId);
            await NackToDeadLetterAsync(channel, deliveryTag).ConfigureAwait(false);
            return;
        }

        // 5. TRIGGER — per-message DI scope; CT-decoupled (None) so client disconnect / shutdown does
        //    not orphan a Pending row mid-dispatch.
        JobExecution execution;
        using (var scope = _scopeFactory.CreateScope())
        {
            var runner = scope.ServiceProvider.GetRequiredService<IJobRunner>();
            var parameters = JobParameters.WrapWithoutCopy(
                new Dictionary<string, object?>(message.Parameters, StringComparer.Ordinal));
            execution = await runner.TriggerAsync(
                message.JobName,
                parameters,
                triggeredBy: $"rabbitmq:{message.SourceService}",
                CancellationToken.None).ConfigureAwait(false);
        }

        // 6. AWAIT TERMINAL — uses _stoppingToken: host shutdown mid-job → OCE → NO ack →
        //    broker redelivery on restart (dedupe MISS if result not stored → re-trigger). At-least-once.
        JobExecution terminal;
        try
        {
            terminal = await _awaiter.WaitForTerminalAsync(execution.ExecutionId, _stoppingToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stoppingCts.IsCancellationRequested)
        {
            // Deliberately NOT acked — the message will be redelivered after restart.
            _logger.LogInformation(
                "RabbitMQ consumer: shutdown during await of MessageId {MessageId} (execution {ExecutionId}) "
                + "— leaving unacked for redelivery.",
                message.MessageId, execution.ExecutionId);
            return;
        }

        // 7. BUILD + STORE RESULT (effectively-once replay source for any redelivery).
        var result = new JobResult
        {
            ExecutionId = terminal.ExecutionId,
            Status = terminal.Status,
            // Return the job's produced outputs to the orchestrator. terminal.Outputs relies on the store
            // invariant "outputs written before the terminal flip + retained" (InMemory/EF both hold it); a
            // future adapter that violates it must re-fetch the row like the HTTP receiver does. Completed-gate
            // matches the orchestrator fold gate and keeps the wire honest (a failed job forwards nothing).
            ReturnValues = terminal.Status == JobStatus.Completed ? terminal.Outputs : null,
            ErrorMessage = terminal.LastError,
            Headers = null,
            CompletedAtUtc = terminal.CompletedAtUtc ?? DateTimeOffset.UtcNow,
        };
        _dedupe.StoreResult(message.MessageId, result);

        // 8. REPLY (RPC result — Completed OR Failed) — published from the CONSUMER channel
        //    (direct-reply-to is channel-scoped).
        await ReplyIfRequestedAsync(channel, ea, result).ConfigureAwait(false);

        // 9. ACK — ALWAYS (Completed OR Failed). requeue NEVER.
        await channel.BasicAckAsync(deliveryTag, multiple: false, _stoppingToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Publishes the <see cref="JobResult"/> back to the request's <c>ReplyTo</c> (direct-reply-to) over
    /// the default exchange — from <paramref name="channel"/> (the consumer channel), NOT the
    /// confirm-channel (direct-reply-to is channel-scoped). No-op when the message carried no
    /// <c>ReplyTo</c> (fire-and-forget publish).
    /// </summary>
    private async Task ReplyIfRequestedAsync(IChannel channel, BasicDeliverEventArgs ea, JobResult result)
    {
        var replyTo = ea.BasicProperties.ReplyTo;
        if (string.IsNullOrEmpty(replyTo))
        {
            return;
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(result, RabbitMqTransport.JsonOpts);
        var props = new BasicProperties
        {
            CorrelationId = ea.BasicProperties.CorrelationId,
            Persistent = false, // direct-reply-to is transient by nature.
        };

        try
        {
            await channel.BasicPublishAsync(
                exchange: string.Empty,
                routingKey: replyTo,
                mandatory: false,
                basicProperties: props,
                body: body,
                cancellationToken: _stoppingToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // A failed reply must not block the ack (step 9) — the RPC caller will time out and the
            // idempotent redelivery / dedupe path compensates.
            _logger.LogWarning(
                ex, "RabbitMQ consumer: reply publish to {ReplyTo} (CorrelationId={CorrelationId}) failed.",
                replyTo, ea.BasicProperties.CorrelationId);
        }
    }

    private async Task NackToDeadLetterAsync(IChannel channel, ulong deliveryTag)
    {
        // requeue:false → routes to the queue's x-dead-letter-exchange (poison containment).
        await channel.BasicNackAsync(deliveryTag, multiple: false, requeue: false, _stoppingToken)
            .ConfigureAwait(false);
    }

    private static JobMessage? TryDeserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<JobMessage>(body.Span, RabbitMqTransport.JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the quorum-queue <c>x-delivery-count</c> header (boxed <see cref="long"/>) for the
    /// defense-in-depth log at step 2. AMQP integer headers arrive boxed; broker delivery-limit
    /// enforcement is authoritative.
    /// </summary>
    private static bool TryGetDeliveryCount(BasicDeliverEventArgs ea, out long count)
    {
        count = 0;
        var headers = ea.BasicProperties.Headers;
        if (headers is not null
            && headers.TryGetValue("x-delivery-count", out var raw)
            && raw is not null)
        {
            // Header may be boxed as long / int depending on broker version — coerce defensively.
            switch (raw)
            {
                case long l: count = l; return true;
                case int i: count = i; return true;
                case byte[] bytes when long.TryParse(Encoding.UTF8.GetString(bytes), out var parsed):
                    count = parsed; return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Dispose() owns disposal (latched via _disposed). If a concurrent or abrupt teardown disposed
        // the CTS first, skip the cancel — in-flight handlers read the cached _stoppingToken which already
        // reflects cancellation, so there is nothing left to signal.
        if (Volatile.Read(ref _disposed) == 0)
        {
            try { await _stoppingCts.CancelAsync().ConfigureAwait(false); }
            catch (ObjectDisposedException) { /* concurrent Dispose won the race */ }
        }

        var channel = _consumerChannel;
        if (channel is not null)
        {
            try
            {
                if (_consumerTag is not null)
                {
                    // Stop new deliveries; in-flight handlers drain under the host's stop grace period.
                    await channel.BasicCancelAsync(_consumerTag, noWait: false, cancellationToken)
                        .ConfigureAwait(false);
                }
                await channel.CloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RabbitMQ consumer pump: channel teardown threw on stop (ignored).");
            }
            await channel.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        // Latched + idempotent. The host disposes hosted services after StopAsync, but an abrupt teardown
        // can also race StopAsync. Exchange ensures the CTS is disposed exactly once and lets a concurrent
        // StopAsync skip its cancel (see Volatile.Read(ref _disposed) there).
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }
        _stoppingCts.Dispose();
    }
}
