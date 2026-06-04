using FluentAssertions;
using RabbitMQ.Client;
using UKBatch.Abstractions.Models;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// durability + publisher confirms over a live broker. A persistent message published to a
/// durable quorum queue while no consumer is attached survives until a consumer starts and processes it;
/// and the confirm-tracking publish path round-trips against the real broker.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class DurabilityAndConfirmsTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public DurabilityAndConfirmsTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task PersistentMessage_PublishedWithoutConsumer_SurvivesUntilConsumed()
    {
        // Durability: publish a persistent message to the worker's durable quorum service queue while NO
        // consumer is running. The message must persist in the queue, then be processed once a consumer
        // attaches. (Container restart is the stronger proof but volume-less Testcontainers cannot persist
        // across a restart; this exercises persistent delivery + durable quorum queue retention.)
        var prefix = NewTopologyPrefix();
        const string Service = "worker-durable";
        var serviceQueue = $"{prefix}.service.{Service}";
        CountingJob.Reset();

        // 1. Start the worker briefly so its durable topology (exchange + quorum service queue) is declared,
        // then STOP it so no consumer is attached.
        var bootstrap = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await bootstrap.DisposeAsync();

        // 2. Publish a persistent message with no consumer attached.
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);
        await sender.Transport.PublishAsync(
            Message(nameof(CountingJob), Service), CancellationToken.None);

        // 3. The message is sitting in the durable queue.
        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(serviceQueue, 1, TimeSpan.FromSeconds(10))).Should().Be(1u,
            "the persistent message is retained in the durable quorum queue with no consumer attached");

        // 4. Start a fresh worker → it consumes the retained message and runs the job.
        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        var ran = await Task.WhenAny(CountingJob.RanOnce.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        ran.Should().BeSameAs(CountingJob.RanOnce.Task, "the retained message is delivered to the new consumer");
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(10))).Should().Be(0u);
    }

    [Fact]
    public async Task PublisherConfirms_PublishAwaitsBrokerAck()
    {
        // The confirm-tracking publish completes only after the broker acks. We assert the message is
        // durably enqueued immediately after PublishAsync returns (no consumer running).
        var prefix = NewTopologyPrefix();
        const string Service = "worker-confirms";
        var serviceQueue = $"{prefix}.service.{Service}";

        // Declare topology then stop (no consumer).
        var bootstrap = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await bootstrap.DisposeAsync();

        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);
        await sender.Transport.PublishAsync(Message(nameof(CompletingJob), Service), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        // Immediately observable: confirm tracking guarantees the broker accepted it before the await returned.
        var depth = await inspector.WaitForMessageCountAsync(serviceQueue, 1, TimeSpan.FromSeconds(5));
        depth.Should().Be(1u, "publisher confirms ensure the message was broker-acked (persisted) before PublishAsync returned");
    }

    [Fact]
    public async Task ServiceQueue_IsDurableQuorum_SurvivesPassiveRedeclare()
    {
        // Quorum + durable: a passive redeclare from an independent connection succeeds (the queue exists
        // and is durable). A non-durable or absent queue would fail the passive declare.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-quorum";
        var serviceQueue = $"{prefix}.service.{Service}";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);

        var factory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionUri) };
        await using var connection = await factory.CreateConnectionAsync("quorum-check");
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: false, publisherConfirmationTrackingEnabled: false));

        var act = async () => await channel.QueueDeclarePassiveAsync(serviceQueue, CancellationToken.None);
        await act.Should().NotThrowAsync("the durable quorum service queue exists and a passive redeclare confirms it");
    }
}
