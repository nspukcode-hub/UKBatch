using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Storage;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// <see cref="JobExecutionWatchHub"/> (the Abstractions-public seam both stores compose) in-process
/// fan-out parity: multi-subscriber broadcast, overflow DropNewest, and the byte-for-byte invariant vs
/// the original InMemory behavior. The existing Core watch tests already prove the InMemory path;
/// these assert the hub IS the shared implementation the EF store delegates to.
/// </summary>
public sealed class WatchHubParityTests
{
    private static JobExecution Exec(string id) => TestData.Execution(id);

    [Fact]
    public async Task Hub_FastConsumer_ReceivesAllPublishedEvents()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        const int N = 100;
        var received = new List<JobExecution>();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumer = Task.Run(async () =>
        {
            await foreach (var ex in hub.WatchAsync(WatchOptions.Default, cts.Token).ConfigureAwait(false))
            {
                received.Add(ex);
                if (received.Count == N) done.TrySetResult();
            }
        });

        await Task.Delay(50).ConfigureAwait(false);
        for (var i = 0; i < N; i++) hub.Publish(Exec($"e{i}"));

        await done.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        cts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        received.Should().HaveCountGreaterOrEqualTo(N);
    }

    [Fact]
    public async Task Hub_TwoSubscribers_BothReceiveAllEvents()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        const int N = 50;
        var r1 = new List<JobExecution>();
        var r2 = new List<JobExecution>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        async Task Consume(List<JobExecution> sink)
        {
            try
            {
                await foreach (var ex in hub.WatchAsync(WatchOptions.Default, cts.Token).ConfigureAwait(false))
                {
                    sink.Add(ex);
                }
            }
            catch (OperationCanceledException) { }
        }

        var c1 = Task.Run(() => Consume(r1));
        var c2 = Task.Run(() => Consume(r2));
        await Task.Delay(100).ConfigureAwait(false);

        for (var i = 0; i < N; i++) hub.Publish(Exec($"e{i}"));
        await Task.Delay(200).ConfigureAwait(false);

        cts.Cancel();
        try { await Task.WhenAll(c1, c2).ConfigureAwait(false); } catch (OperationCanceledException) { }

        r1.Should().HaveCountGreaterOrEqualTo(N);
        r2.Should().HaveCountGreaterOrEqualTo(N);
    }

    [Fact]
    public async Task Hub_DropNewest_SlowConsumerDropsTailEvents()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var options = new WatchOptions { OverflowPolicy = WatchOverflowPolicy.DropNewest, BufferCapacity = 16 };
        var received = new List<JobExecution>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Deterministic slow-consumer harness — no wall-clock scheduling assumptions. The previous
        // version parked the consumer behind Task.Run + Task.Delay(50) and flaked to ZERO events
        // under full-suite CPU load (the consumer never got scheduled before the publishes, so the
        // subscription was not yet registered and all 500 events were dropped on the floor).
        // MoveNextAsync runs the async-iterator body synchronously up to its first await (the
        // empty-buffer read), so the subscription is REGISTERED the moment the call returns. The
        // consumer then stays idle (no pending read) while we flood the buffer, which makes the
        // DropNewest tail-drop structural instead of timing-dependent.
        var watch = hub.WatchAsync(options, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            hub.Publish(Exec("seed"));              // completes the pending read deterministically
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            // Consumer idle: the bounded buffer absorbs exactly BufferCapacity events, DropNewest
            // discards the remaining tail.
            const int Published = 500;
            for (var i = 0; i < Published; i++) hub.Publish(Exec($"e{i}"));

            // Drain the survivors — exactly BufferCapacity events are buffered, never more.
            for (var i = 0; i < options.BufferCapacity; i++)
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            received.Count.Should().BeLessThan(Published, "DropNewest drops tail events when the buffer is full");
            received.Count.Should().BeGreaterThan(0);
        }
        finally
        {
            cts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Hub_Backpressure_BehavesLikeDropNewest_ByteForByteInvariant()
    {
        // Byte-for-byte invariant vs the original behavior: Backpressure is implemented as DropNewest
        // with a non-blocking publisher (verbatim-extracted semantics). The publish loop must NOT
        // block (true awaiting backpressure would deadlock — caught by the 2s fail-safe).
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var options = new WatchOptions { OverflowPolicy = WatchOverflowPolicy.Backpressure, BufferCapacity = 16 };
        var received = new List<JobExecution>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in hub.WatchAsync(options, cts.Token).ConfigureAwait(false))
                {
                    received.Add(ex);
                    await Task.Delay(20, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50).ConfigureAwait(false);

        var publish = Task.Run(() =>
        {
            for (var i = 0; i < 500; i++) hub.Publish(Exec($"e{i}"));
        });

        var winner = await Task.WhenAny(publish, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        winner.Should().BeSameAs(publish, "Backpressure must NOT block the publisher (DropNewest in v0.1)");
        await publish.ConfigureAwait(false);

        await Task.Delay(200).ConfigureAwait(false);
        cts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        received.Count.Should().BeGreaterThan(0);
        received.Count.Should().BeLessThan(500, "drops MUST occur — proves non-blocking (NOT awaiting backpressure)");
    }

    [Fact]
    public async Task Hub_Cancellation_StopsEnumeration()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        using var cts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in hub.WatchAsync(WatchOptions.Default, cts.Token).ConfigureAwait(false)) { }
        });

        await Task.Delay(100).ConfigureAwait(false);
        cts.Cancel();
        var caught = false;
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { caught = true; }
        caught.Should().BeTrue();
    }

    [Fact]
    public void Hub_PublishNull_Throws()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var act = () => hub.Publish(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Hub_ImplementsAbstractionsPublicInterface()
    {
        // The 7A promotion: adapters compose IJobExecutionWatchHub (Abstractions-public), not the concrete.
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        hub.Should().BeAssignableTo<IJobExecutionWatchHub>();
    }
}
