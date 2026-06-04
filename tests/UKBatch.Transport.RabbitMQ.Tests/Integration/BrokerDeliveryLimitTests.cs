using FluentAssertions;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// The broker-enforced <c>x-delivery-limit</c> on the quorum service queue. The pump
/// itself NEVER requeues, so we drive the limit with an independent raw consumer that nacks-with-requeue:
/// after <c>MaxRedeliveryCount</c> redeliveries the broker auto-dead-letters to the DLX (proving the
/// topology's <c>x-delivery-limit</c> is real, not a client-side count).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class BrokerDeliveryLimitTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public BrokerDeliveryLimitTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task QuorumDeliveryLimit_ExceededByRequeue_AutoDeadLettersToDlq()
    {
        const int MaxRedeliveries = 3;
        var prefix = NewTopologyPrefix();
        const string Service = "worker-deliverylimit";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";

        // Start the worker only to DECLARE the quorum topology (x-delivery-limit=MaxRedeliveries), then stop
        // it so our raw consumer is the only one attached.
        var bootstrap = await WorkerHost.StartAsync(
            _fixture.ConnectionUri, Service, prefix, maxRedeliveryCount: MaxRedeliveries);
        await bootstrap.DisposeAsync();

        // Publish one message onto the service queue.
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);
        await sender.Transport.PublishAsync(Message(nameof(CompletingJob), Service), CancellationToken.None);

        // Raw consumer that ALWAYS nacks-with-requeue → forces the broker to redeliver until the limit.
        var factory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionUri) };
        await using var connection = await factory.CreateConnectionAsync("requeue-driver");
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false));
        await channel.BasicQosAsync(0, 1, false, CancellationToken.None);

        var deliveries = 0;
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, ea) =>
        {
            Interlocked.Increment(ref deliveries);
            // requeue:true → the quorum queue increments x-delivery-count; past the limit the broker DLXes.
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: true, CancellationToken.None);
        };
        await channel.BasicConsumeAsync(serviceQueue, autoAck: false, consumer, CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        var dlqDepth = await inspector.WaitForMessageCountAsync(dlq, expected: 1, TimeSpan.FromSeconds(20));

        dlqDepth.Should().Be(1u,
 "once x-delivery-count exceeds the quorum queue's x-delivery-limit the broker auto-dead-letters");
        Volatile.Read(ref deliveries).Should().BeGreaterThan(1,
            "the message was redelivered multiple times before the broker gave up on it");
    }
}
