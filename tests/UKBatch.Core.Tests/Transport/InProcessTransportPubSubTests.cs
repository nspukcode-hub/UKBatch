using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport;
using Xunit;

namespace UKBatch.Core.Tests.Transport;

/// <summary>
/// InProcessTransport is pub/sub (every subscriber sees
/// every message), NOT competing-consumer.
/// </summary>
public class InProcessTransportPubSubTests
{
    private static JobMessage NewMessage(string id, string jobName = "topic1") => new()
    {
        MessageId = id,
        JobName = jobName,
        SourceService = "src",
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    [Fact]
    public async Task PublishAsync_With5Subscribers_EachReceivesAll1000Messages()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        const int N = 1000;
        const int Subs = 5;

        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var counters = Enumerable.Range(0, Subs).Select(_ => new List<string>()).ToArray();
        var subscribers = new List<Task>();
        var readyTasks = new List<TaskCompletionSource>();

        for (var i = 0; i < Subs; i++)
        {
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            readyTasks.Add(ready);
            var idx = i;
            subscribers.Add(Task.Run(async () =>
            {
                ready.TrySetResult();
                try
                {
                    await foreach (var msg in transport.SubscribeAsync("topic1", subCts.Token).ConfigureAwait(false))
                    {
                        counters[idx].Add(msg.MessageId);
                        if (counters[idx].Count == N)
                        {
                            // each subscriber breaks out when complete
                            return;
                        }
                    }
                }
                catch (OperationCanceledException) { }
            }));
        }

        // Wait for all subscriptions to start.
        await Task.WhenAll(readyTasks.Select(r => r.Task)).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false); // ensure SubscribeAsync's GetOrAdd path executed

        // Publish N messages.
        for (var i = 0; i < N; i++)
        {
            await transport.PublishAsync(NewMessage($"m{i}"), default).ConfigureAwait(false);
        }

        await Task.WhenAll(subscribers).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        // EVERY subscriber must have received EVERY message (invariant).
        for (var i = 0; i < Subs; i++)
        {
            counters[i].Should().HaveCount(N, $"subscriber {i} should receive all {N} messages (pub/sub, not competing-consumer)");
        }
    }

    [Fact]
    public async Task PublishAsync_PreservesOrderPerSubscriber()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        const int N = 100;

        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new List<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var subscriber = Task.Run(async () =>
        {
            ready.TrySetResult();
            try
            {
                await foreach (var msg in transport.SubscribeAsync("orderly", subCts.Token).ConfigureAwait(false))
                {
                    received.Add(msg.MessageId);
                    if (received.Count == N) return;
                }
            }
            catch (OperationCanceledException) { }
        });

        await ready.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        await Task.Delay(100).ConfigureAwait(false);

        for (var i = 0; i < N; i++)
        {
            await transport.PublishAsync(NewMessage($"m{i:D3}", "orderly"), default).ConfigureAwait(false);
        }

        await subscriber.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        var expected = Enumerable.Range(0, N).Select(i => $"m{i:D3}").ToList();
        received.Should().BeEquivalentTo(expected, opts => opts.WithStrictOrdering());
    }

    [Fact]
    public async Task SubscribeAsync_CancellationToken_StopsSubscription()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        using var subCts = new CancellationTokenSource();
        var caught = false;
        var subscriber = Task.Run(async () =>
        {
            try
            {
                await foreach (var _ in transport.SubscribeAsync("topic-x", subCts.Token).ConfigureAwait(false))
                {
                    // drain
                }
            }
            catch (OperationCanceledException) { caught = true; }
        });

        await Task.Delay(100).ConfigureAwait(false);
        subCts.Cancel();
        await subscriber.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

        caught.Should().BeTrue();
    }

    [Fact]
    public void Name_Returns_InProcess()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        transport.Name.Should().Be("InProcess");
    }
}
