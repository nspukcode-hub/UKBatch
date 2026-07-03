using System.Text.Json;
using FluentAssertions;
using RabbitMQ.Client;
using UKBatch.Abstractions.Models;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// integration: publish→consume and RPC roundtrip over a live broker.
/// <para>
/// <b>.</b> direct-reply-to is strictly <i>channel-scoped</i>: a message whose
/// <c>reply-to</c> is <c>amq.rabbitmq.reply-to</c> must be published on the SAME channel that consumes that
/// pseudo-queue, else the broker rejects it with <c>PRECONDITION_FAILED - fast reply consumer does not
/// exist</c>. The pre-fix design published the request on the connection manager's confirm-channel while
/// the reply was consumed on the reply router's SEPARATE channel (the spec's fix moved only the WORKER
/// reply publish; the symmetric ORCHESTRATOR request-publish requirement was missed). Fix: the RPC request
/// is now published via <c>RabbitMqReplyRouter.PublishRequestAsync</c> on the reply router's own consuming
/// channel, under its own publish-lock + publisher confirms. The RPC tests below assert the working
/// behavior; <see cref="DirectReplyTo_RequestOnNonConsumingChannel_BrokerRejects_RootCauseOfRpcBlocker"/>
/// keeps a raw-AMQP characterization of the broker constraint that motivated the fix.
/// </para>
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class PublishConsumeAndRpcTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public PublishConsumeAndRpcTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    // ===== Working: fire-and-forget publish path =====

    [Fact]
    public async Task Publish_ToServiceQueue_ConsumerPumpRunsJob()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-pub";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(
            Message(nameof(CountingJob), Service), CancellationToken.None);

        var ran = await Task.WhenAny(CountingJob.RanOnce.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        ran.Should().BeSameAs(CountingJob.RanOnce.Task, "the published message must trigger the job within the timeout");
        Volatile.Read(ref CountingJob.RunCount).Should().Be(1);
    }

    // ===== RPC (correct-behavior spec; Skipped pending the direct-reply-to channel-affinity fix) =====

    [Fact]
    public async Task RequestReply_Completing_ReturnsCompletedResult()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-rpc-ok";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        var result = await sender.Transport.RequestReplyAsync(
            Service,
            Message(nameof(CompletingJob), Service),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        result.Status.Should().Be(JobStatus.Completed);
        result.ExecutionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestReply_FailingJob_ReturnsFailedResult_NotException()
    {
        // Critical (once RPC works): a failing job flows back as JobResult(Status=Failed) — NOT a transport
        // exception and NOT dead-lettered.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-rpc-fail";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        var result = await sender.Transport.RequestReplyAsync(
            Service,
            Message(nameof(FailingJob), Service),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        result.Status.Should().Be(JobStatus.Failed, "job failure travels via the reply, not via DLQ or an exception");
        result.ExecutionId.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task RequestReply_MultipleSequential_AllComplete()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-rpc-seq";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        for (var i = 0; i < 3; i++)
        {
            var result = await sender.Transport.RequestReplyAsync(
                Service,
                Message(nameof(CompletingJob), Service),
                TimeSpan.FromSeconds(20),
                CancellationToken.None);
            result.Status.Should().Be(JobStatus.Completed);
        }
    }

    [Fact]
    public async Task RequestReply_OutputProducingJob_ReturnsOutputs()
    {
        // Over a LIVE broker: a job that records an output has that value returned on the RPC reply. This is
        // the transport-level regression guard for cross-service step-output return — the reply carries the
        // worker's produced output back to the caller.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-rpc-outputs";

        await using var worker = await WorkerHost.StartAsync(
            _fixture.ConnectionUri, Service, prefix,
            configureJobs: b => b.AddJob<OutputProducingJob>().Named(nameof(OutputProducingJob)));
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        var result = await sender.Transport.RequestReplyAsync(
            Service,
            Message(nameof(OutputProducingJob), Service),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        result.Status.Should().Be(JobStatus.Completed);
        result.ReturnValues.Should().NotBeNull("a completed job's recorded outputs travel back on the reply");
        // The value arrives deserialized as a JsonElement over the wire.
        result.ReturnValues!["k"].Should().BeOfType<JsonElement>()
            .Which.GetString().Should().Be("v");
    }

    [Fact]
    public async Task RequestReply_DuplicateMessageIdInFlight_Throws()
    {
        // The reply router rejects a second in-flight request with the same correlation id (MessageId).
        var prefix = NewTopologyPrefix();
        const string Service = "worker-dup";

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        var sharedId = Guid.NewGuid().ToString("N");
        var first = sender.Transport.RequestReplyAsync(
            Service,
            Message(nameof(CompletingJob), Service, messageId: sharedId),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        var act = async () => await sender.Transport.RequestReplyAsync(
            Service,
            Message(nameof(CompletingJob), Service, messageId: sharedId),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate correlationId*");
        await first;
    }

    /// <summary>
    /// Broker constraint characterization (the proven root cause that motivated the RPC):
    /// publishing a <c>reply-to: amq.rabbitmq.reply-to</c> message on a RAW channel that does NOT hold the
    /// direct-reply-to consumer is rejected by the broker. This is exactly why the pre-fix RPC design
    /// (request on the confirm-channel, reply consumer on a SEPARATE channel) failed — and why the fix
    /// publishes the request on the reply router's own consuming channel. Kept as durable evidence; runs
    /// green because it asserts the broker's documented channel-scoped behavior directly (it does NOT drive
    /// the production RPC path, which now works — see <see cref="RequestReply_Completing_ReturnsCompletedResult"/>).
    /// </summary>
    [Fact]
    public async Task DirectReplyTo_RequestOnNonConsumingChannel_BrokerRejects_RootCauseOfRpcBlocker()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-rootcause";

        // Start a worker so the exchange + service queue EXIST (rules out a NOT_FOUND-exchange failure);
        // the publish must then fail specifically at the direct-reply-to channel-affinity check.
        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);

        // Raw AMQP: a fresh channel that does NOT consume amq.rabbitmq.reply-to. Publishing a request whose
        // reply-to is the direct-reply-to pseudo-queue from here is rejected by the broker
        // ('PRECONDITION_FAILED - fast reply consumer does not exist'). With publisher confirms enabled the
        // rejection surfaces as a faulted publish / channel exception.
        var factory = new ConnectionFactory { Uri = new Uri(_fixture.ConnectionUri) };
        await using var connection = await factory.CreateConnectionAsync("rootcause-nonconsumer");
        await using var channel = await connection.CreateChannelAsync(
            new CreateChannelOptions(publisherConfirmationsEnabled: true, publisherConfirmationTrackingEnabled: true));

        var props = new BasicProperties
        {
            Persistent = false,
            MessageId = Guid.NewGuid().ToString("N"),
            ReplyTo = "amq.rabbitmq.reply-to",
        };

        var act = async () => await channel.BasicPublishAsync(
            exchange: $"{prefix}.jobs",
            routingKey: Service,
            mandatory: true,
            basicProperties: props,
            body: "{}"u8.ToArray(),
            cancellationToken: CancellationToken.None);

        // .ToString flattens the inner-exception chain (the broker's PRECONDITION_FAILED text may surface
        // either on the outer publish exception or a wrapped shutdown/channel exception).
        var thrown = (await act.Should().ThrowAsync<Exception>(
            "direct-reply-to is channel-scoped: a reply-to:amq.rabbitmq.reply-to publish on a channel without "
            + "the reply consumer is rejected by the broker ('fast reply consumer does not exist') — the root "
            + "cause the fix addresses")).Which;

        thrown.ToString().Should().Match(s =>
            s.Contains("fast reply consumer", StringComparison.OrdinalIgnoreCase)
            || s.Contains("PRECONDITION_FAILED", StringComparison.OrdinalIgnoreCase));
    }
}
