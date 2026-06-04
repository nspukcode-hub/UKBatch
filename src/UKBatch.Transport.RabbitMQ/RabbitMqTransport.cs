using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.RabbitMQ.Connection;
using UKBatch.Transport.RabbitMQ.Rpc;

namespace UKBatch.Transport.RabbitMQ;

/// <summary>
/// RabbitMQ (AMQP) <see cref="ITransport"/> adapter. Wire format: JSON-serialized
/// <see cref="JobMessage"/> / <see cref="JobResult"/> (with <see cref="JsonStringEnumConverter"/> so
/// <see cref="JobStatus"/> round-trips against Web-default workers that emit string enums).
/// </summary>
/// <remarks>
/// <para><b>Durability:</b> messages are published <see cref="BasicProperties.Persistent"/> through the
/// durable <c>ukbatch.jobs</c> exchange; the worker service queue is a durable quorum queue.</para>
/// <para><b>Thread-safe:</b> singleton. Publish/reply serialize through the connection manager's
/// confirm-channel lock; <see cref="SubscribeAsync"/> opens its own per-subscription consumer channel.</para>
/// <para><b>Security:</b> no application-level HMAC — trust is the broker (user/pass + TLS).</para>
/// </remarks>
public sealed class RabbitMqTransport : ITransport
{
    /// <summary>
    /// Reserved <see cref="JobMessage.Headers"/> keys mirrored onto AMQP message headers. Kept in sync
    /// with the consumer pump which reads them back.
    /// </summary>
    internal const string HeaderSource = "x-ukbatch-source";
    internal const string HeaderBatch = "x-ukbatch-batch";
    internal const string HeaderStep = "x-ukbatch-step";
    internal const string HeaderAttempt = "x-ukbatch-attempt";
    internal const string HeaderTraceParent = "traceparent";
    internal const string HeaderTraceState = "tracestate";

    /// <summary>
    /// JSON options for wire (de)serialization. Camel-case + <see cref="JsonStringEnumConverter"/>:
    /// <see cref="JobResult.Status"/> MUST round-trip as a string against workers hosting
    /// <c>AddUKBatchApi</c> (Web-default string enums). Internal for the contract regression test.
    /// </summary>
    internal static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly RabbitMqConnectionManager _connection;
    private readonly RabbitMqReplyRouter _replyRouter;
    private readonly ILogger<RabbitMqTransport> _logger;

    /// <summary>Constructs the transport.</summary>
    public RabbitMqTransport(
        RabbitMqConnectionManager connection,
        RabbitMqReplyRouter replyRouter,
        ILogger<RabbitMqTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(replyRouter);
        ArgumentNullException.ThrowIfNull(logger);
        _connection = connection;
        _replyRouter = replyRouter;
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "RabbitMQ";

    /// <inheritdoc/>
    public async Task PublishAsync(JobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = _connection.Options;
        var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);
        // TargetService==null routes by JobName — if no queue is bound to JobName on the direct
        // exchange the broker silently drops it (NOT a broadcast). The v0.1 batch path never uses null.
        var routingKey = message.TargetService ?? message.JobName;

        await _connection.PublishWithConfirmAsync(
            async (channel, ct) =>
            {
                var props = BuildProperties(message, replyTo: null);
                await channel.BasicPublishAsync(
                    exchange: options.ExchangeName,
                    routingKey: routingKey,
                    mandatory: false,
                    basicProperties: props,
                    body: body,
                    cancellationToken: ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        _logger.LogDebug(
            "RabbitMQ published MessageId {MessageId} (job {JobName}) to exchange {Exchange} routingKey {RoutingKey}.",
            message.MessageId, message.JobName, options.ExchangeName, routingKey);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobMessage> SubscribeAsync(
        string topic,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);

        // Contract-completeness subscribe: bridge a broker consumer into a bounded channel.
        // The batch execution path uses RequestReplyAsync, not this; the cross-service consumer pump
        // is the at-least-once dispatch path. autoAck=true here: pure observation stream.
        var bridge = Channel.CreateBounded<JobMessage>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = true,
        });

        await _connection.EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        var channel = await _connection.OpenChannelAsync(confirmsEnabled: false, cancellationToken).ConfigureAwait(false);

        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += (_, ea) =>
        {
            var msg = TryDeserialize(ea.Body);
            if (msg is not null)
            {
                // Non-blocking — DropOldest bounded channel never awaits the writer.
                bridge.Writer.TryWrite(msg);
            }
            return Task.CompletedTask;
        };

        string consumerTag;
        try
        {
            consumerTag = await channel.BasicConsumeAsync(
                queue: topic,
                autoAck: true,
                consumer: consumer,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await SafeCloseChannelAsync(channel).ConfigureAwait(false);
            throw;
        }

        try
        {
            await foreach (var msg in bridge.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return msg;
            }
        }
        finally
        {
            try
            {
                await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RabbitMQ SubscribeAsync: BasicCancelAsync threw on teardown (ignored).");
            }
            await SafeCloseChannelAsync(channel).ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    /// <remarks>
    /// <para>Direct-reply-to RPC. The correlation id is <see cref="JobMessage.MessageId"/>
    /// (also the receiver-side dedupe key). The request is published via
    /// <see cref="RabbitMqReplyRouter.PublishRequestAsync"/> on the reply router's OWN channel — the one
    /// holding the <c>amq.rabbitmq.reply-to</c> consumer — with <c>mandatory:true</c> so an unroutable
    /// target (e.g. the worker queue does not exist yet) fails fast with a <c>PublishException</c>
    /// (<c>IsReturn=true</c>) instead of hanging until the timeout. direct-reply-to is strictly
    /// channel-scoped: the broker rejects a <c>reply-to: amq.rabbitmq.reply-to</c>
    /// publish on any channel other than the one consuming the pseudo-queue
    /// (<c>PRECONDITION_FAILED - fast reply consumer does not exist</c>), so request-publish and
    /// reply-consume share that single channel.</para>
    /// <para><b>Cancellation ordering:</b> after the await unblocks via the linked CTS, the
    /// caller-supplied <paramref name="cancellationToken"/> is checked FIRST (rethrow OCE), only THEN is
    /// <see cref="TimeoutException"/> thrown — avoiding the <c>when</c>-filter race where a simultaneous
    /// caller-cancel + timeout lets a raw OCE escape (mirrors <c>InProcessTransport</c> ordering).</para>
    /// </remarks>
    public async Task<JobResult> RequestReplyAsync(
        string targetService,
        JobMessage message,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetService);
        ArgumentNullException.ThrowIfNull(message);

        var correlationId = message.MessageId;
        await _replyRouter.EnsureStartedAsync(cancellationToken).ConfigureAwait(false);

        var tcs = _replyRouter.RegisterPending(correlationId);
        try
        {
            var options = _connection.Options;
            var body = JsonSerializer.SerializeToUtf8Bytes(message, JsonOpts);

            // Publish the request on the reply router's OWN channel (the one consuming
            // amq.rabbitmq.reply-to). direct-reply-to is strictly channel-scoped — publishing the
            // reply-to:amq.rabbitmq.reply-to request from any other channel (e.g. the confirm-channel) is
            // rejected by the broker with 'PRECONDITION_FAILED - fast reply consumer does not exist'.
            // mandatory:true → unroutable (worker queue absent) raises PublishException(IsReturn=true)
            // (publisher confirms are enabled on the reply channel). PublishException is NOT swallowed →
            // the batch step observes a transport failure (Failed).
            var props = BuildProperties(message, replyTo: RabbitMqReplyRouter.DirectReplyToQueue);
            props.CorrelationId = correlationId;
            await _replyRouter.PublishRequestAsync(
                exchange: options.ExchangeName,
                routingKey: targetService,
                mandatory: true,
                properties: props,
                body: body,
                cancellationToken: cancellationToken).ConfigureAwait(false);

            using var timeoutCts = new CancellationTokenSource(timeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(
                static state => ((TaskCompletionSource<JobResult>)state!).TrySetCanceled(),
                tcs);

            try
            {
                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // caller-ct FIRST, then timeout. (Either could have tripped the linked CTS.)
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException(
                    $"RequestReply to '{targetService}' (MessageId {correlationId}) timed out after {timeout}.");
            }
        }
        finally
        {
            _replyRouter.RemovePending(correlationId);
        }
    }

    /// <summary>
    /// Builds AMQP <see cref="BasicProperties"/> from a <see cref="JobMessage"/>. Header values are
    /// written as UTF-8 <c>byte[]</c> (AMQP delivers them back as <c>byte[]</c>; attempt count is
    /// stored as a boxed <see cref="int"/>).
    /// </summary>
    internal static BasicProperties BuildProperties(JobMessage message, string? replyTo)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        AddHeader(headers, HeaderSource, message.SourceService);
        AddHeader(headers, HeaderBatch, message.BatchId);
        AddHeader(headers, HeaderStep, message.BatchStepId);
        headers[HeaderAttempt] = message.AttemptNumber; // boxed int

        // Preserve caller-supplied reserved headers (W3C trace context etc.) round-trip.
        foreach (var kv in message.Headers)
        {
            AddHeader(headers, kv.Key, kv.Value);
        }

        return new BasicProperties
        {
            Persistent = true,
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId ?? message.MessageId,
            ReplyTo = replyTo,
            Headers = headers!,
        };
    }

    private static void AddHeader(Dictionary<string, object?> headers, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            headers[key] = Encoding.UTF8.GetBytes(value);
        }
    }

    private static JobMessage? TryDeserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<JobMessage>(body.Span, JsonOpts);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private async Task SafeCloseChannelAsync(IChannel channel)
    {
        try
        {
            await channel.CloseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "RabbitMQ SubscribeAsync: channel close threw on teardown (ignored).");
        }
        await channel.DisposeAsync().ConfigureAwait(false);
    }
}
