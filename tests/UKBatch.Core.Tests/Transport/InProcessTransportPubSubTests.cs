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

        // Register every subscriber channel synchronously BEFORE publishing. SubscribeAsync's iterator
        // runs up to its first await on the first MoveNextAsync, and AddSubscriber happens before that
        // await — so the channel provably exists when the call returns. (PublishToAll only fans out to
        // channels present at publish time; a fixed delay here flaked when registration slipped past it.)
        var enumerators = new IAsyncEnumerator<JobMessage>[Subs];
        var firstMoves = new Task<bool>[Subs];
        for (var i = 0; i < Subs; i++)
        {
            enumerators[i] = transport.SubscribeAsync("topic1", subCts.Token).GetAsyncEnumerator(subCts.Token);
            firstMoves[i] = enumerators[i].MoveNextAsync().AsTask();  // registers the channel, then suspends on the empty channel
        }
        try
        {
            var drains = new Task[Subs];
            for (var i = 0; i < Subs; i++)
            {
                var idx = i;
                drains[idx] = Task.Run(async () =>
                {
                    try
                    {
                        if (await firstMoves[idx].ConfigureAwait(false))
                        {
                            counters[idx].Add(enumerators[idx].Current.MessageId);
                            while (counters[idx].Count < N && await enumerators[idx].MoveNextAsync().ConfigureAwait(false))
                            {
                                counters[idx].Add(enumerators[idx].Current.MessageId);
                            }
                        }
                    }
                    catch (OperationCanceledException) { }
                });
            }

            for (var i = 0; i < N; i++)
            {
                await transport.PublishAsync(NewMessage($"m{i}"), default).ConfigureAwait(false);
            }

            await Task.WhenAll(drains).WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            // EVERY subscriber must have received EVERY message (invariant).
            for (var i = 0; i < Subs; i++)
            {
                counters[i].Should().HaveCount(N, $"subscriber {i} should receive all {N} messages (pub/sub, not competing-consumer)");
            }
        }
        finally
        {
            subCts.Cancel();
            foreach (var e in enumerators)
            {
                await e.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    [Fact]
    public async Task PublishAsync_PreservesOrderPerSubscriber()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        const int N = 100;

        using var subCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var received = new List<string>();

        // Register the subscriber channel synchronously before publishing (AddSubscriber runs before the
        // iterator's first await), so no ordered message is dropped regardless of CPU load.
        var e = transport.SubscribeAsync("orderly", subCts.Token).GetAsyncEnumerator(subCts.Token);
        var firstMove = e.MoveNextAsync().AsTask();  // registers the channel, then suspends on the empty channel
        try
        {
            var drain = Task.Run(async () =>
            {
                try
                {
                    if (await firstMove.ConfigureAwait(false))
                    {
                        received.Add(e.Current.MessageId);
                        while (received.Count < N && await e.MoveNextAsync().ConfigureAwait(false))
                        {
                            received.Add(e.Current.MessageId);
                        }
                    }
                }
                catch (OperationCanceledException) { }
            });

            for (var i = 0; i < N; i++)
            {
                await transport.PublishAsync(NewMessage($"m{i:D3}", "orderly"), default).ConfigureAwait(false);
            }

            await drain.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);

            var expected = Enumerable.Range(0, N).Select(i => $"m{i:D3}").ToList();
            received.Should().BeEquivalentTo(expected, opts => opts.WithStrictOrdering());
        }
        finally
        {
            subCts.Cancel();
            await e.DisposeAsync().ConfigureAwait(false);
        }
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
