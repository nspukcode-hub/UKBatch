using FluentAssertions;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// core invariants over a live broker, exercised via fire-and-forget
/// <c>PublishAsync</c> (NO RPC dependency — see <see cref="PublishConsumeAndRpcTests"/> for the
/// separately-tracked direct-reply-to). Covers <b>requeue-never</b> (a Failed job is acked and
/// neither requeued nor dead-lettered) and <b>MessageId dedupe</b> (a redelivered same-MessageId message
/// does NOT re-run the job).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class RequeueNeverAndDedupeTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public RequeueNeverAndDedupeTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FailedJob_IsAcked_NotRequeued_NotDeadLettered()
    {
        // requeue-NEVER: the failing job runs, the runtime marks it Failed, the pump ACKS (step 9). The
        // message must NOT return to the service queue AND must NOT land in the DLQ.
        var prefix = NewTopologyPrefix();
        const string Service = "worker-requeue-never";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";
        Volatile.Write(ref FailingJob.RunCount, 0);

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(Message(nameof(FailingJob), Service), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);

        // The failing job runs (proving the message was delivered + dispatched), then is acked.
        var serviceDepth = await inspector.WaitForMessageCountAsync(serviceQueue, expected: 0, TimeSpan.FromSeconds(15));
        serviceDepth.Should().Be(0u, "the failed message was acked — it does NOT return to the service queue");

        // The failure flows back via the (would-be) reply, NOT the DLQ. Allow a settle window then assert DLQ empty.
        await Task.Delay(TimeSpan.FromSeconds(2));
        var dlqDepth = await inspector.MessageCountAsync(dlq);
        dlqDepth.Should().Be(0u, "a Failed job is NOT poison — it never hits the DLQ (K3 invariant)");
        Volatile.Read(ref FailingJob.RunCount).Should().BeGreaterThan(0, "the failing job actually executed");
    }

    [Fact]
    public async Task CompletedJob_LeavesQueueEmpty_AndDlqEmpty()
    {
        var prefix = NewTopologyPrefix();
        const string Service = "worker-complete-empty";
        var serviceQueue = $"{prefix}.service.{Service}";
        var dlq = $"{prefix}.dlq";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(Message(nameof(CountingJob), Service), CancellationToken.None);

        var ran = await Task.WhenAny(CountingJob.RanOnce.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        ran.Should().BeSameAs(CountingJob.RanOnce.Task);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(10))).Should().Be(0u);
        await Task.Delay(TimeSpan.FromSeconds(1));
        (await inspector.MessageCountAsync(dlq)).Should().Be(0u);
    }

    [Fact]
    public async Task SameMessageId_DeliveredTwice_RunsJobOnce()
    {
        // MessageId dedupe: two fire-and-forget publishes of the SAME MessageId → the consumer sees two
        // deliveries; the first is a MISS (runs), the second is a HIT (acked WITHOUT re-running).
        var prefix = NewTopologyPrefix();
        const string Service = "worker-dedupe";
        var serviceQueue = $"{prefix}.service.{Service}";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        var sharedId = Guid.NewGuid().ToString("N");

        // First publish → MISS → runs.
        await sender.Transport.PublishAsync(
            Message(nameof(CountingJob), Service, messageId: sharedId), CancellationToken.None);
        var ran = await Task.WhenAny(CountingJob.RanOnce.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        ran.Should().BeSameAs(CountingJob.RanOnce.Task);

        // Second publish of the SAME MessageId → HIT → acked without re-running.
        await sender.Transport.PublishAsync(
            Message(nameof(CountingJob), Service, messageId: sharedId), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(10))).Should().Be(0u,
            "both deliveries were acked");

        // Settle window: the second delivery must NOT increment the run count.
        await Task.Delay(TimeSpan.FromSeconds(1));
        Volatile.Read(ref CountingJob.RunCount).Should().Be(1, "the duplicate MessageId did NOT re-run the job");
    }

    [Fact]
    public async Task DistinctMessageIds_SameJob_RunTwice()
    {
        // Control for the dedupe test: two DIFFERENT MessageIds → the job runs twice (dedupe is keyed on
        // MessageId, not job name).
        var prefix = NewTopologyPrefix();
        const string Service = "worker-distinct";
        var serviceQueue = $"{prefix}.service.{Service}";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, Service, prefix);
        await using var sender = Sender.Build(_fixture.ConnectionUri, prefix);

        await sender.Transport.PublishAsync(Message(nameof(CountingJob), Service), CancellationToken.None);
        await sender.Transport.PublishAsync(Message(nameof(CountingJob), Service), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(serviceQueue, 0, TimeSpan.FromSeconds(15))).Should().Be(0u);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Volatile.Read(ref CountingJob.RunCount).Should().Be(2, "distinct MessageIds each run the job");
    }
}
