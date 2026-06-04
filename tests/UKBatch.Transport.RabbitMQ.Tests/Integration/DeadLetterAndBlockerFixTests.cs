using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using UKBatch.Transport.RabbitMQ.Dedupe;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// dead-letter containment + the regression lock. Poison messages
/// (unregistered job / undeserializable body) dead-letter to the DLQ; and — critically — an UNEXPECTED
/// throw in steps 4–9 AFTER the dedupe <c>TryAdd</c> succeeded must EVICT the dedupe key and
/// dead-letter, so the job is NOT silently dropped via a resultless dedupe HIT.
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class DeadLetterAndBlockerFixTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public DeadLetterAndBlockerFixTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    /// <summary>Publishes a raw AMQP message directly to a service queue (default exchange routing by queue name).</summary>
    private async Task PublishRawAsync(string serviceQueue, ReadOnlyMemory<byte> body, string? messageId)
    {
        var factory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionUri) };
        await using var connection = await factory.CreateConnectionAsync("raw-publisher");
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));
        var props = new BasicProperties { Persistent = true, MessageId = messageId };
        await channel.BasicPublishAsync(
            exchange: string.Empty, routingKey: serviceQueue, mandatory: true,
            basicProperties: props, body: body, cancellationToken: CancellationToken.None);
    }

    [Fact]
    public async Task UnregisteredJob_DeadLettersToDlq_ServiceQueueEmpty()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-poison-unreg";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        // Publish a well-formed message for a job that is NOT registered on this worker → step-4 poison.
        await sender.Transport.PublishAsync(
            Message("ThisJobIsNotRegistered", Service), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        var dlqDepth = await inspector.WaitForMessageCountAsync(dlq, expected: 1, TimeSpan.FromSeconds(15));
        var serviceDepth = await inspector.WaitForMessageCountAsync(serviceQueue, expected: 0, TimeSpan.FromSeconds(5));

        dlqDepth.Should().Be(1u, "an unregistered-job message is poison → dead-lettered to the DLQ");
        serviceDepth.Should().Be(0u, "the poison message was nacked off the service queue");
    }

    [Fact]
    public async Task UndeserializableBody_DeadLettersToDlq()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-poison-garbage";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);

        // Garbage bytes that are not valid JobMessage JSON → step-1 poison.
        await PublishRawAsync(serviceQueue, "this is not json {{{"u8.ToArray(), messageId: "garbage-1");

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(dlq, 1, TimeSpan.FromSeconds(15))).Should().Be(1u,
            "an undeserializable body is poison → DLQ");
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(5))).Should().Be(0u);
    }

    [Fact]
    public async Task MissingMessageId_DeadLettersToDlq()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-poison-noid";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);

        // Valid JSON shape but blank MessageId → step-1 poison guard (string.IsNullOrEmpty(MessageId)).
        const string Json = "{\"messageId\":\"\",\"jobName\":\"CompletingJob\",\"sourceService\":\"x\","
            + "\"parameters\":{},\"headers\":{},\"enqueuedAtUtc\":\"2026-01-01T00:00:00+00:00\",\"attemptNumber\":1}";
        await PublishRawAsync(serviceQueue, System.Text.Encoding.UTF8.GetBytes(Json), messageId: null);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(dlq, 1, TimeSpan.FromSeconds(15))).Should().Be(1u,
            "a message with a blank MessageId is poison → DLQ");
    }

    // ===== regression lock =====

    [Fact]
    public async Task BlockerFix_UnexpectedThrowAfterDedupeTryAdd_EvictsDedupeAndDeadLetters_NoSilentDrop()
    {
        // REGRESSION LOCK: the FaultingAwaiter throws a non-OCE in step 6, AFTER the dedupe
        // TryAdd succeeded. The guard MUST:
        // (a) dead-letter the delivery (containment) — NOT silently ack-and-drop it, AND
        // (b) EVICT the dedupe key (un-poison) — so a redelivery is a MISS that re-runs, never a
        // resultless HIT that acks-without-running.
        // The pre-fix bug would ack-without-run and leave a resultless dedupe entry → silent job loss.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-blocker";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";
        var messageId = Guid.NewGuid().ToString("N");

        await using var worker = await WorkerHost.StartAsync(
            _fixture.ConnectionUri, Service, prefix, faultingAwaiter: true);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(
            Message(nameof(CompletingJob), Service, messageId: messageId), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);

        // (a) Containment: the faulting delivery is dead-lettered, NOT silently dropped.
        var dlqDepth = await inspector.WaitForMessageCountAsync(dlq, expected: 1, TimeSpan.FromSeconds(15));
        dlqDepth.Should().Be(1u,
 "an unexpected post-dedupe throw dead-letters (containment) instead of ack-and-drop");
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(5))).Should().Be(0u);

        // (b) Un-poison: the dedupe key was evicted, so the SAME MessageId is a MISS again (would re-run).
        var dedupe = worker.Services.GetRequiredService<MessageIdDedupeCache>();
        dedupe.TryGetResult(messageId, out _).Should().BeFalse(
            "the faulting delivery stored no result");
        dedupe.TryAdd(messageId).Should().BeTrue(
 "the dedupe key was evicted → redelivery would MISS and re-run, NOT a resultless HIT");
    }

    [Fact]
    public async Task BlockerFix_FaultingAwaiterWasInvoked_ProvingPostDedupePath()
    {
        // Sanity that the fault is injected on the post-dedupe path (step 6), not earlier.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-blocker-sanity";
        var dlq = $"{prefix}.dlq";

        await using var worker = await WorkerHost.StartAsync(
            _fixture.ConnectionUri, Service, prefix, faultingAwaiter: true);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(
            Message(nameof(CompletingJob), Service), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        await inspector.WaitForMessageCountAsync(dlq, 1, TimeSpan.FromSeconds(15));

        var awaiter = (FaultingAwaiter)worker.Services
            .GetRequiredService<UKBatch.Abstractions.Runtime.IJobExecutionAwaiter>();
        awaiter.CallCount.Should().BeGreaterThan(0, "the awaiter (step 6) was reached after the dedupe TryAdd");
    }
}
