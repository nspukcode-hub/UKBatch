using FluentAssertions;
using UKBatch.Abstractions.Models;
using Xunit;
using static UKBatch.Transport.RabbitMQ.Tests.Integration.RabbitMqTestHarness;

namespace UKBatch.Transport.RabbitMQ.Tests.Integration;

/// <summary>
/// Two-service end-to-end over the live broker: an orchestrator (sender, no service queue)
/// dispatches cross-service work to a worker (consumer on its own durable quorum service queue), routed
/// by <c>TargetService</c>. Both the fire-and-forget delivery path and the RPC round-trip are verified
/// (the direct-reply-to fixed — see <see cref="PublishConsumeAndRpcTests"/>).
/// </summary>
[Trait("Category", "RequiresDocker")]
[Collection("RabbitMQ integration")]
public sealed class TwoServiceEndToEndTests : IClassFixture<RabbitMqContainerFixture>
{
    private readonly RabbitMqContainerFixture _fixture;

    public TwoServiceEndToEndTests(RabbitMqContainerFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task OrchestratorToWorker_CrossServiceDelivery_RunsJobOnWorker()
    {
        var prefix = NewTopologyPrefix();
        const string WorkerService = "billing-worker";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, WorkerService, prefix);
        await using var orchestrator = Sender.Build(_fixture.ConnectionUri, prefix, senderName: "orchestrator");

        var message = Message(
            nameof(CountingJob),
            WorkerService,
            sourceService: "orchestrator",
            parameters: new Dictionary<string, object?> { ["orderId"] = 4242 });

        await orchestrator.Transport.PublishAsync(message, CancellationToken.None);

        var ran = await Task.WhenAny(CountingJob.RanOnce.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        ran.Should().BeSameAs(CountingJob.RanOnce.Task, "the cross-service message reached the worker and ran the job");
        Volatile.Read(ref CountingJob.RunCount).Should().Be(1);
    }

    [Fact]
    public async Task OrchestratorToWorker_RoutedByTargetService_OnlyTargetWorkerReceives()
    {
        // Two workers on the same exchange; routing-key == TargetService delivers to exactly one queue.
        var prefix = NewTopologyPrefix();
        const string WorkerA = "worker-a";
        const string WorkerB = "worker-b";
        var queueA = $"{prefix}.service.{WorkerA}";
        var queueB = $"{prefix}.service.{WorkerB}";

        await using var a = await WorkerHost.StartAsync(_fixture.ConnectionUri, WorkerA, prefix);
        await using var b = await WorkerHost.StartAsync(_fixture.ConnectionUri, WorkerB, prefix);
        await using var orchestrator = Sender.Build(_fixture.ConnectionUri, prefix, senderName: "orchestrator");

        // Target only worker-a. worker-b's queue must stay empty (and its job must not run).
        await orchestrator.Transport.PublishAsync(Message(nameof(CompletingJob), WorkerA), CancellationToken.None);

        await using var inspector = await BrokerInspector.ConnectAsync(_fixture.ConnectionUri);
        (await inspector.WaitForMessageCountAsync(queueA, 0, TimeSpan.FromSeconds(15))).Should().Be(0u,
            "worker-a consumed its targeted message");
        (await inspector.MessageCountAsync(queueB)).Should().Be(0u,
            "worker-b was NOT targeted — direct routing delivered only to worker-a's queue");
    }

    [Fact]
    public async Task OrchestratorToWorker_CrossServiceRpc_RoundTrips()
    {
        var prefix = NewTopologyPrefix();
        const string WorkerService = "billing-worker";
        CountingJob.Reset();

        await using var worker = await WorkerHost.StartAsync(_fixture.ConnectionUri, WorkerService, prefix);
        await using var orchestrator = Sender.Build(_fixture.ConnectionUri, prefix, senderName: "orchestrator");

        var result = await orchestrator.Transport.RequestReplyAsync(
            WorkerService,
            Message(nameof(CountingJob), WorkerService, sourceService: "orchestrator"),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);

        result.Status.Should().Be(JobStatus.Completed);
        Volatile.Read(ref CountingJob.RunCount).Should().Be(1);
    }
}
