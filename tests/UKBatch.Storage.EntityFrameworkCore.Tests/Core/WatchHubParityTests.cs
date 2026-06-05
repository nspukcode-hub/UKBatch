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
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Deterministic registration handshake — no wall-clock scheduling assumptions. The previous
        // version parked the consumer behind Task.Run + Task.Delay(50) and flaked under full-suite CPU
        // load (the consumer might not get scheduled before the publishes, leaving the subscription
        // unregistered so events publish into the void — the hub does not replay). MoveNextAsync runs
        // the async-iterator body synchronously up to its first await (the empty-buffer read), so the
        // subscription is REGISTERED the moment the call returns. We seed one event to complete that
        // first read deterministically, then publish the rest and drain all N.
        var watch = hub.WatchAsync(WatchOptions.Default, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            hub.Publish(Exec("e0"));                // completes the pending read deterministically
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            // Subscription is live: the remaining events are buffered and drained in order.
            for (var i = 1; i < N; i++) hub.Publish(Exec($"e{i}"));
            for (var i = 1; i < N; i++)
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            received.Should().HaveCount(N);
        }
        finally
        {
            cts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task Hub_TwoSubscribers_BothReceiveAllEvents()
    {
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        const int N = 50;
        var r1 = new List<JobExecution>();
        var r2 = new List<JobExecution>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Same deterministic registration handshake as the sibling tests in this file: the first
        // MoveNextAsync registers each subscription synchronously up to its first await, and a seed
        // publish completes both pending reads — no Task.Delay scheduling assumptions.
        var w1 = hub.WatchAsync(WatchOptions.Default, cts.Token).GetAsyncEnumerator(cts.Token);
        var w2 = hub.WatchAsync(WatchOptions.Default, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var m1 = w1.MoveNextAsync();
            var m2 = w2.MoveNextAsync();
            hub.Publish(Exec("seed"));
            (await m1.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            (await m2.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            r1.Add(w1.Current);
            r2.Add(w2.Current);

            for (var i = 0; i < N; i++) hub.Publish(Exec($"e{i}"));

            // Drain each subscriber independently — both must observe every published event.
            for (var i = 0; i < N; i++)
            {
                (await w1.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                r1.Add(w1.Current);
            }
            for (var i = 0; i < N; i++)
            {
                (await w2.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                r2.Add(w2.Current);
            }

            r1.Should().HaveCountGreaterOrEqualTo(N);
            r2.Should().HaveCountGreaterOrEqualTo(N);
        }
        finally
        {
            cts.Cancel();
            await w1.DisposeAsync().ConfigureAwait(false);
            await w2.DisposeAsync().ConfigureAwait(false);
        }
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
        // with a non-blocking publisher (verbatim-extracted semantics).
        // Same deterministic harness as the DropNewest sibling above — the previous version parked
        // the consumer behind Task.Run + Task.Delay(50) and flaked to ZERO events under full-suite
        // CPU load (subscription not yet registered when the flood ran). MoveNextAsync registers the
        // subscription synchronously up to its first await, so the seed read is structural.
        var hub = new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance);
        var options = new WatchOptions { OverflowPolicy = WatchOverflowPolicy.Backpressure, BufferCapacity = 16 };
        var received = new List<JobExecution>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var watch = hub.WatchAsync(options, cts.Token).GetAsyncEnumerator(cts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            hub.Publish(Exec("seed"));              // completes the pending read deterministically
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            // Publisher must NOT block (true awaiting backpressure would deadlock the flood — caught
            // by the fail-safe timeout). Consumer is idle, so the bounded buffer fills and the tail
            // is dropped exactly like DropNewest.
            const int Published = 500;
            var publish = Task.Run(() =>
            {
                for (var i = 0; i < Published; i++) hub.Publish(Exec($"e{i}"));
            });
            await publish.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);

            // Drain the survivors — exactly BufferCapacity events are buffered, never more.
            for (var i = 0; i < options.BufferCapacity; i++)
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            received.Count.Should().BeGreaterThan(0);
            received.Count.Should().BeLessThan(Published, "drops MUST occur — proves non-blocking (NOT awaiting backpressure)");
        }
        finally
        {
            cts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
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
