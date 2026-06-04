using RabbitMQ.Client;

namespace UKBatch.Transport.RabbitMQ.Topology;

/// <summary>
/// Idempotent async topology declarer. Builds the durable job exchange, the fanout
/// dead-letter exchange, the durable dead-letter queue, and this node's durable <b>quorum</b> service
/// queue (<c>x-queue-type=quorum</c> + broker-enforced <c>x-delivery-limit</c>),
/// then binds them.
/// </summary>
/// <remarks>
/// <para>All declares are idempotent: re-declaring with identical parameters is a broker no-op;
/// re-declaring with different parameters raises <c>PRECONDITION_FAILED</c> (surfaced as a hard error).
/// Broker <see cref="ConnectionFactory.TopologyRecoveryEnabled"/> re-creates these after a connection
/// drop, but the explicit declare on connect remains the source of truth.</para>
/// <para><b>Sender-only nodes</b> (no <c>ThisServiceName</c>) call <see cref="DeclareSenderTopologyAsync"/>
/// — exchange + DLX + DLQ only. They never declare a service queue (the target worker owns it).</para>
/// </remarks>
internal static class RabbitMqTopology
{
    /// <summary>AMQP argument key selecting a quorum queue (Raft-replicated, native delivery-limit).</summary>
    internal const string QueueTypeArg = "x-queue-type";

    /// <summary>AMQP argument key naming the dead-letter exchange for a queue.</summary>
    internal const string DeadLetterExchangeArg = "x-dead-letter-exchange";

    /// <summary>AMQP argument key for the broker-enforced redelivery cap on a quorum queue.</summary>
    internal const string DeliveryLimitArg = "x-delivery-limit";

    /// <summary>Quorum queue type value.</summary>
    internal const string QuorumQueueType = "quorum";

    /// <summary>
    /// Declares the full receiver-side topology: exchange + DLX + DLQ + this node's quorum service queue
    /// + bindings. Call when the node hosts a consumer (<paramref name="serviceName"/> non-empty).
    /// </summary>
    /// <param name="channel">An open channel.</param>
    /// <param name="options">Transport options (topology names + <c>MaxRedeliveryCount</c>).</param>
    /// <param name="serviceName">This node's service identity; queue name = <c>{QueuePrefix}{serviceName}</c>.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The fully-qualified service queue name.</returns>
    internal static async Task<string> DeclareReceiverTopologyAsync(
        IChannel channel,
        RabbitMqTransportOptions options,
        string serviceName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        await DeclareSenderTopologyAsync(channel, options, cancellationToken).ConfigureAwait(false);

        var queueName = options.QueuePrefix + serviceName;

        var queueArgs = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [QueueTypeArg] = QuorumQueueType,
            [DeadLetterExchangeArg] = options.DeadLetterExchangeName,
            // The broker enforces the delivery limit → automatic DLX on exhaustion. No client-side counting.
            [DeliveryLimitArg] = options.MaxRedeliveryCount,
        };

        await channel.QueueDeclareAsync(
            queue: queueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: queueArgs!,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Direct binding: routing key == service name (matches JobMessage.TargetService).
        await channel.QueueBindAsync(
            queue: queueName,
            exchange: options.ExchangeName,
            routingKey: serviceName,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return queueName;
    }

    /// <summary>
    /// Declares the sender-side topology: job exchange + dead-letter exchange + dead-letter queue +
    /// DLX→DLQ binding. Safe to call from both sender-only and receiver nodes.
    /// </summary>
    internal static async Task DeclareSenderTopologyAsync(
        IChannel channel,
        RabbitMqTransportOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(options);

        // Direct, durable job exchange.
        await channel.ExchangeDeclareAsync(
            exchange: options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Fanout, durable dead-letter exchange.
        await channel.ExchangeDeclareAsync(
            exchange: options.DeadLetterExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Durable dead-letter queue.
        await channel.QueueDeclareAsync(
            queue: options.DeadLetterQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            passive: false,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // Bind DLQ to the fanout DLX (routing key irrelevant for fanout).
        await channel.QueueBindAsync(
            queue: options.DeadLetterQueueName,
            exchange: options.DeadLetterExchangeName,
            routingKey: string.Empty,
            arguments: null,
            noWait: false,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }
}
