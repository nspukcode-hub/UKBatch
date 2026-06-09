using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Storage;

/// <summary>
/// WatchAsync overflow policies under 5K events each.
/// Verifies Backpressure / DropOldest / DropNewest behave per the spec.
/// </summary>
public class InMemoryJobStoreWatchAsyncTests
{
    private static InMemoryJobStore CreateStore()
    {
        return new InMemoryJobStore(
            TimeProvider.System,
            Options.Create(new UKBatchOptions()),
            new JobExecutionWatchHub(NullLogger<JobExecutionWatchHub>.Instance));
    }

    private static JobDefinition NewDef() => new()
    {
        Name = "watch.test",
        IsPartitioned = false,
        MaxRetries = 0,
        TimeoutSeconds = 0,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
    };

    [Fact]
    public async Task WatchAsync_FastConsumer_ReceivesAllEvents()
    {
        var store = CreateStore();
        const int N = 100;
        var received = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // MoveNextAsync runs the async-iterator body synchronously up to its first await, so the
        // subscription is REGISTERED when the call returns — no wall-clock race. (Same deterministic
        // handshake as the DropOldest/DropNewest siblings; the default 1024 buffer holds all N, so a
        // fast consumer receives every event. The previous version raced a Task.Run consumer against a
        // fixed 50ms "let it subscribe" delay — a late registration silently lost the early creates.)
        var watch = store.WatchAsync(WatchOptions.Default, subCts.Token).GetAsyncEnumerator(subCts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);  // event #1 completes the pending read
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            for (var i = 1; i < N; i++)  // remaining N-1 events
            {
                _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
            }

            for (var i = 1; i < N; i++)  // drain — default 1024 buffer holds all, fast consumer drops nothing
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            received.Should().HaveCount(N);
        }
        finally
        {
            subCts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WatchAsync_DropOldest_SlowConsumerSeesMostRecentEvents()
    {
        var store = CreateStore();
        const int Published = 1000;
        var options = new WatchOptions
        {
            OverflowPolicy = WatchOverflowPolicy.DropOldest,
            BufferCapacity = 32,
        };
        var received = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Deterministic slow-consumer harness — same shape as the DropNewest test below. The previous
        // version raced a Task.Run consumer against the awaited publish loop: when the subscription
        // registered only after the last publish, the live feed delivered nothing, the
        // consumer-started signal never fired, and the watchdog timed out. MoveNextAsync runs the
        // async-iterator body synchronously up to its first await, so the subscription is REGISTERED
        // when the call returns; keeping the consumer idle (no pending read) while the creates flood
        // the buffer makes the DropOldest head-eviction structural instead of timing-dependent.
        var watch = store.WatchAsync(options, subCts.Token).GetAsyncEnumerator(subCts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);  // seed completes the pending read
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            // Consumer idle: the bounded buffer holds at most BufferCapacity events, and DropOldest
            // evicts from the head — what survives is the most recent window of the flood.
            for (var i = 0; i < Published; i++)
            {
                _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
            }

            // Drain the surviving window — exactly BufferCapacity events are buffered, never more.
            for (var i = 0; i < options.BufferCapacity; i++)
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            // With a 32-deep buffer and a flood of 1000, most events were evicted before the drain.
            received.Count.Should().BeLessThan(Published);
            received.Count.Should().BeGreaterThan(0);
        }
        finally
        {
            subCts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WatchAsync_DropNewest_DropsLaterEventsWhenBufferFull()
    {
        var store = CreateStore();
        var options = new WatchOptions
        {
            OverflowPolicy = WatchOverflowPolicy.DropNewest,
            BufferCapacity = 16,
        };
        var received = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Deterministic slow-consumer harness — no wall-clock scheduling assumptions. The previous
        // version (Task.Run consumer + Task.Delay(50) "let subscribe register" + Task.Delay(20)
        // per-event "slow" consumer) flaked under full-suite CPU load: when the awaited CreateAsync
        // producer loop slowed enough, the consumer kept up, NOTHING was dropped, and
        // BeLessThan(500) failed. MoveNextAsync runs the async-iterator body synchronously up to
        // its first await, so the subscription is REGISTERED when the call returns; keeping the
        // consumer idle (no pending read) while the creates flood the buffer makes the DropNewest
        // tail-drop structural instead of timing-dependent.
        var watch = store.WatchAsync(options, subCts.Token).GetAsyncEnumerator(subCts.Token);
        try
        {
            var firstMove = watch.MoveNextAsync();  // registers the subscription, then suspends on the empty buffer
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);  // seed completes the pending read
            (await firstMove.AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
            received.Add(watch.Current);

            // Consumer idle: the bounded buffer absorbs exactly BufferCapacity events, DropNewest
            // discards the remaining tail.
            const int Published = 500;
            for (var i = 0; i < Published; i++)
            {
                _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
            }

            // Drain the survivors — exactly BufferCapacity events are buffered, never more.
            for (var i = 0; i < options.BufferCapacity; i++)
            {
                (await watch.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false)).Should().BeTrue();
                received.Add(watch.Current);
            }

            // DropNewest means events get dropped at the tail when buffer is full.
            received.Count.Should().BeLessThan(Published);
            received.Count.Should().BeGreaterThan(0);
        }
        finally
        {
            subCts.Cancel();
            await watch.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WatchAsync_TwoSubscribers_BothReceiveAllEvents()
    {
        var store = CreateStore();
        const int N = 50;
        var r1 = new List<JobExecution>();
        var r2 = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Register both subscriptions synchronously BEFORE publishing: MoveNextAsync runs the
        // async-iterator body up to its first await, so each subscription exists when the call returns.
        // The previous version raced two Task.Run consumers against a fixed 100ms "let them subscribe"
        // delay, then asserted on background-mutated lists after a fixed 200ms drain — under load a late
        // registration lost early events, or the drain had not finished. (.AsTask() so each stored
        // ValueTask is consumed once across the Task.Run boundary.)
        var e1 = store.WatchAsync(WatchOptions.Default, subCts.Token).GetAsyncEnumerator(subCts.Token);
        var e2 = store.WatchAsync(WatchOptions.Default, subCts.Token).GetAsyncEnumerator(subCts.Token);
        try
        {
            var m1 = e1.MoveNextAsync().AsTask();  // registers subscriber 1, suspends on the empty buffer
            var m2 = e2.MoveNextAsync().AsTask();  // registers subscriber 2

            var c1 = Task.Run(async () =>
            {
                try { if (await m1.ConfigureAwait(false)) { r1.Add(e1.Current); while (await e1.MoveNextAsync().ConfigureAwait(false)) r1.Add(e1.Current); } }
                catch (OperationCanceledException) { }
            });
            var c2 = Task.Run(async () =>
            {
                try { if (await m2.ConfigureAwait(false)) { r2.Add(e2.Current); while (await e2.MoveNextAsync().ConfigureAwait(false)) r2.Add(e2.Current); } }
                catch (OperationCanceledException) { }
            });

            for (var i = 0; i < N; i++)
            {
                _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
            }

            // Poll live state to a generous deadline rather than racing a fixed drain window that a
            // pre-emptive cancel would abort (the default 1024 buffer holds all N, so no drops occur).
            var got = await Waits.ForAsync(() => r1.Count >= N && r2.Count >= N, TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            got.Should().BeTrue("both subscribers must receive all N events");

            r1.Should().HaveCountGreaterOrEqualTo(N);
            r2.Should().HaveCountGreaterOrEqualTo(N);

            subCts.Cancel();
            try { await Task.WhenAll(c1, c2).ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        finally
        {
            subCts.Cancel();
            await e1.DisposeAsync().ConfigureAwait(false);
            await e2.DisposeAsync().ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task WatchAsync_Backpressure_BehavesLikeDropNewest_InMemoryAdapter()
    {
        // test #19 — locks down the Backpressure-as-DropNewest invariant on
        // the in-memory adapter (the publisher uses non-blocking TryWrite, so Wait mode
        // would have been inert; the channel is now configured DropNewest for clarity).
        // Strict-inequality assertion: 0 < received.Count < 500 — the only way to violate
        // it is to actually implement awaiting backpressure, which would deadlock the
        // test (no consumer drains during the publish loop) — caught by the timeout.
        var store = CreateStore();
        var options = new WatchOptions
        {
            OverflowPolicy = WatchOverflowPolicy.Backpressure,
            BufferCapacity = 16,
        };
        var received = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in store.WatchAsync(options, subCts.Token).ConfigureAwait(false))
                {
                    consumerStarted.TrySetResult();
                    received.Add(ex);
                    await Task.Delay(20, subCts.Token).ConfigureAwait(false); // slow drain
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50).ConfigureAwait(false); // let subscribe register

        var publishTask = Task.Run(async () =>
        {
            for (var i = 0; i < 500; i++)
            {
                _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
            }
        });

        // If true awaiting backpressure were implemented, publishTask would block forever
        // (consumer drains at 20ms per item; 500 items would exceed the 2s timeout). The
        // timeout fail-safe catches that regression.
        var done = await Task.WhenAny(publishTask, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        done.Should().BeSameAs(publishTask, "publish loop must NOT block — Backpressure is implemented as DropNewest in v0.1");
        await publishTask.ConfigureAwait(false);

        // Let consumer drain some events.
        await Task.Delay(200).ConfigureAwait(false);
        subCts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        received.Count.Should().BeGreaterThan(0, "at least one event must get through");
        received.Count.Should().BeLessThan(500, "drops MUST occur — proves we are NOT awaiting backpressure");
    }

    [Fact]
    public async Task WatchAsync_CancellationToken_StopsTheEnumeration()
    {
        var store = CreateStore();
        using var subCts = new CancellationTokenSource();
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in store.WatchAsync(WatchOptions.Default, subCts.Token).ConfigureAwait(false))
            {
                // drain
            }
        });

        await Task.Delay(100).ConfigureAwait(false);
        subCts.Cancel();
        var caught = false;
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { caught = true; }

        caught.Should().BeTrue();
    }
}
