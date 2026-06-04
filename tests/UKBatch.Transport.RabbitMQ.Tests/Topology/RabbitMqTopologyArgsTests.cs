using FluentAssertions;
using NSubstitute;
using RabbitMQ.Client;
using UKBatch.Transport.RabbitMQ;
using UKBatch.Transport.RabbitMQ.Topology;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Topology;

/// <summary>
/// Topology argument-builder coverage. Drives <see cref="RabbitMqTopology"/> against a
/// substituted <see cref="IChannel"/> and asserts the captured declare arguments: the quorum service
/// queue (<c>x-queue-type=quorum</c> + broker-enforced <c>x-delivery-limit</c> + DLX), the fanout DLX,
/// the durable DLQ, and the direct binding. No broker contact — pure arg assertions.
/// </summary>
public sealed class RabbitMqTopologyArgsTests
{
    /// <summary>
    /// A substituted channel that records, at call-time, the <c>arguments</c> dictionary of every
    /// <c>QueueDeclareAsync</c> keyed by queue name (capture-on-invocation is reliable across repeated
    /// same-signature calls, unlike <c>Arg.Do</c> replayed during a <c>Received</c> verification).
    /// </summary>
    private static (IChannel Channel, Dictionary<string, IDictionary<string, object?>?> QueueArgs) BuildChannel()
    {
        var channel = Substitute.For<IChannel>();
        var queueArgs = new Dictionary<string, IDictionary<string, object?>?>(StringComparer.Ordinal);
        channel.QueueDeclareAsync(
                Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<IDictionary<string, object?>>(), Arg.Any<bool>(), Arg.Any<bool>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var queue = (string)ci[0];
                queueArgs[queue] = (IDictionary<string, object?>?)ci[4];
                return new QueueDeclareOk(queue, 0u, 0u);
            });
        return (channel, queueArgs);
    }

    // ===== Quorum service queue =====

    [Fact]
    public async Task DeclareReceiverTopology_ServiceQueue_IsQuorumWithDeliveryLimitAndDlx()
    {
        var (channel, queueArgs) = BuildChannel();
        var options = new RabbitMqTransportOptions { MaxRedeliveryCount = 7 };

        await RabbitMqTopology.DeclareReceiverTopologyAsync(channel, options, "billing-worker", CancellationToken.None);

        queueArgs.Should().ContainKey("ukbatch.service.billing-worker");
        var serviceQueueArgs = queueArgs["ukbatch.service.billing-worker"];
        serviceQueueArgs.Should().NotBeNull();
        serviceQueueArgs!["x-queue-type"].Should().Be("quorum");
        serviceQueueArgs["x-dead-letter-exchange"].Should().Be(options.DeadLetterExchangeName);
        serviceQueueArgs["x-delivery-limit"].Should().Be(7, "B1: broker-enforced delivery limit == MaxRedeliveryCount");
    }

    [Fact]
    public async Task DeclareReceiverTopology_Dlq_HasNoQuorumOrDeliveryLimitArgs()
    {
        // The DLQ is a plain durable queue (no x-queue-type/x-delivery-limit); only the service queue is quorum.
        var (channel, queueArgs) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareReceiverTopologyAsync(channel, options, "w", CancellationToken.None);

        queueArgs.Should().ContainKey(options.DeadLetterQueueName);
        queueArgs[options.DeadLetterQueueName].Should().BeNull(
            "the dead-letter queue carries no arguments — it must not itself dead-letter");
    }

    [Fact]
    public async Task DeclareReceiverTopology_ReturnsFullyQualifiedQueueName()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions { QueuePrefix = "svc." };

        var queueName = await RabbitMqTopology.DeclareReceiverTopologyAsync(
            channel, options, "alpha", CancellationToken.None);

        queueName.Should().Be("svc.alpha");
    }

    [Fact]
    public async Task DeclareReceiverTopology_EmptyQueuePrefix_QueueNameEqualsServiceName()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions { QueuePrefix = "" };

        var queueName = await RabbitMqTopology.DeclareReceiverTopologyAsync(
            channel, options, "alpha", CancellationToken.None);

        queueName.Should().Be("alpha");
    }

    [Fact]
    public async Task DeclareReceiverTopology_BindsServiceQueueToExchangeOnServiceName()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareReceiverTopologyAsync(channel, options, "billing-worker", CancellationToken.None);

        // Direct binding: routing key == service name (matches JobMessage.TargetService).
        await channel.Received().QueueBindAsync(
            queue: "ukbatch.service.billing-worker",
            exchange: options.ExchangeName,
            routingKey: "billing-worker",
            arguments: Arg.Any<IDictionary<string, object?>>(),
            noWait: false,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public async Task DeclareReceiverTopology_BlankServiceName_Throws(string? serviceName)
    {
        var (channel, _) = BuildChannel();
        var act = async () => await RabbitMqTopology.DeclareReceiverTopologyAsync(
            channel, new RabbitMqTransportOptions(), serviceName!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    // ===== Sender topology: exchange + DLX + DLQ + binding =====

    [Fact]
    public async Task DeclareSenderTopology_DeclaresDirectDurableJobExchange()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareSenderTopologyAsync(channel, options, CancellationToken.None);

        await channel.Received().ExchangeDeclareAsync(
            exchange: options.ExchangeName,
            type: ExchangeType.Direct,
            durable: true,
            autoDelete: false,
            arguments: Arg.Any<IDictionary<string, object?>>(),
            passive: false,
            noWait: false,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeclareSenderTopology_DeclaresFanoutDurableDlx()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareSenderTopologyAsync(channel, options, CancellationToken.None);

        await channel.Received().ExchangeDeclareAsync(
            exchange: options.DeadLetterExchangeName,
            type: ExchangeType.Fanout,
            durable: true,
            autoDelete: false,
            arguments: Arg.Any<IDictionary<string, object?>>(),
            passive: false,
            noWait: false,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeclareSenderTopology_DeclaresDurableDlq()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareSenderTopologyAsync(channel, options, CancellationToken.None);

        await channel.Received().QueueDeclareAsync(
            queue: options.DeadLetterQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: Arg.Any<IDictionary<string, object?>>(),
            passive: false,
            noWait: false,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeclareSenderTopology_BindsDlqToDlx()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareSenderTopologyAsync(channel, options, CancellationToken.None);

        await channel.Received().QueueBindAsync(
            queue: options.DeadLetterQueueName,
            exchange: options.DeadLetterExchangeName,
            routingKey: string.Empty,
            arguments: Arg.Any<IDictionary<string, object?>>(),
            noWait: false,
            cancellationToken: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeclareSenderTopology_CustomNames_Honored()
    {
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions
        {
            ExchangeName = "my.jobs",
            DeadLetterExchangeName = "my.dlx",
            DeadLetterQueueName = "my.dlq",
        };

        await RabbitMqTopology.DeclareSenderTopologyAsync(channel, options, CancellationToken.None);

        await channel.Received().ExchangeDeclareAsync(
            "my.jobs", ExchangeType.Direct, true, false,
            Arg.Any<IDictionary<string, object?>>(), false, false, Arg.Any<CancellationToken>());
        await channel.Received().QueueDeclareAsync(
            "my.dlq", true, false, false,
            Arg.Any<IDictionary<string, object?>>(), false, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeclareReceiverTopology_AlsoDeclaresSenderTopology()
    {
        // The receiver path declares the exchange/DLX/DLQ before its own service queue.
        var (channel, _) = BuildChannel();
        var options = new RabbitMqTransportOptions();

        await RabbitMqTopology.DeclareReceiverTopologyAsync(channel, options, "w", CancellationToken.None);

        await channel.Received().ExchangeDeclareAsync(
            options.ExchangeName, ExchangeType.Direct, true, false,
            Arg.Any<IDictionary<string, object?>>(), false, false, Arg.Any<CancellationToken>());
        await channel.Received().ExchangeDeclareAsync(
            options.DeadLetterExchangeName, ExchangeType.Fanout, true, false,
            Arg.Any<IDictionary<string, object?>>(), false, false, Arg.Any<CancellationToken>());
    }
}
