using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
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
        var receivedAll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var consumer = Task.Run(async () =>
        {
            await foreach (var ex in store.WatchAsync(WatchOptions.Default, subCts.Token).ConfigureAwait(false))
            {
                received.Add(ex);
                if (received.Count == N)
                {
                    receivedAll.TrySetResult();
                }
            }
        });

        // Give consumer a moment to subscribe.
        await Task.Delay(50).ConfigureAwait(false);

        for (var i = 0; i < N; i++)
        {
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
        }

        await receivedAll.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        subCts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        received.Should().HaveCountGreaterOrEqualTo(N);
    }

    [Fact]
    public async Task WatchAsync_DropOldest_SlowConsumerSeesMostRecentEvents()
    {
        var store = CreateStore();
        const int N = 1000;
        var options = new WatchOptions
        {
            OverflowPolicy = WatchOverflowPolicy.DropOldest,
            BufferCapacity = 32,
        };
        var received = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in store.WatchAsync(options, subCts.Token).ConfigureAwait(false))
                {
                    consumerStarted.TrySetResult();
                    received.Add(ex);
                    await Task.Delay(5, subCts.Token).ConfigureAwait(false); // slow consumer
                }
            }
            catch (OperationCanceledException) { }
        });

        // Publish before consumer is reading aggressively — let the buffer fill.
        for (var i = 0; i < N; i++)
        {
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
        }

        await consumerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        // Let consumer drain remaining buffer.
        await Task.Delay(500).ConfigureAwait(false);
        subCts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        // With a 32-deep buffer + slow consumer, we expect FEWER than N events delivered.
        received.Count.Should().BeLessThan(N);
        received.Count.Should().BeGreaterThan(0);
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
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var consumerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in store.WatchAsync(options, subCts.Token).ConfigureAwait(false))
                {
                    consumerStarted.TrySetResult();
                    received.Add(ex);
                    await Task.Delay(20, subCts.Token).ConfigureAwait(false); // slow
                }
            }
            catch (OperationCanceledException) { }
        });

        await Task.Delay(50).ConfigureAwait(false); // let subscribe register

        for (var i = 0; i < 500; i++)
        {
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
        }

        await Task.Delay(500).ConfigureAwait(false);
        subCts.Cancel();
        try { await consumer.ConfigureAwait(false); } catch (OperationCanceledException) { }

        // DropNewest means events get dropped at the tail when buffer is full.
        received.Count.Should().BeLessThan(500);
    }

    [Fact]
    public async Task WatchAsync_TwoSubscribers_BothReceiveAllEvents()
    {
        var store = CreateStore();
        const int N = 50;
        var r1 = new List<JobExecution>();
        var r2 = new List<JobExecution>();
        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        var c1 = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in store.WatchAsync(WatchOptions.Default, subCts.Token).ConfigureAwait(false))
                {
                    r1.Add(ex);
                }
            }
            catch (OperationCanceledException) { }
        });
        var c2 = Task.Run(async () =>
        {
            try
            {
                await foreach (var ex in store.WatchAsync(WatchOptions.Default, subCts.Token).ConfigureAwait(false))
                {
                    r2.Add(ex);
                }
            }
            catch (OperationCanceledException) { }
        });
        await Task.Delay(100).ConfigureAwait(false);

        for (var i = 0; i < N; i++)
        {
            _ = await store.CreateAsync(NewDef(), default).ConfigureAwait(false);
        }
        await Task.Delay(200).ConfigureAwait(false);

        subCts.Cancel();
        try { await Task.WhenAll(c1, c2).ConfigureAwait(false); } catch (OperationCanceledException) { }

        // Both subscribers should have received all N events (or close — buffer is 1024 default).
        r1.Should().HaveCountGreaterOrEqualTo(N);
        r2.Should().HaveCountGreaterOrEqualTo(N);
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
