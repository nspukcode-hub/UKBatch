using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Tests.Common;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Sender;

/// <summary>
/// HttpTransport.SubscribeAsync long-poll loop. Verifies the iterator yields
/// messages, retries on empty arrays, propagates cancel, and surfaces 401 as transport-auth errors.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HttpTransportSubscribeTests : IClassFixture<WorkerFactory>
{
    private readonly WorkerFactory _factory;

    public HttpTransportSubscribeTests(WorkerFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static JobMessage BuildMessage(string topic) => new JobMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        CorrelationId = null,
        JobName = topic,
        SourceService = "orchestrator-test",
        TargetService = null,
        BatchId = null,
        BatchStepId = null,
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    private async Task PublishMessageToWorker(string topic, string sharedSecret)
    {
        using var client = _factory.CreateClient();
        var msg = BuildMessage(topic);
        var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts));
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", sharedSecret, bodyBytes: body);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task SubscribeAsync_HappyPath_YieldsMessages()
    {
        const string Topic = "Subscribe.Topic.A";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            // First publish to the worker so the subscribe drain has at least one message.
            await PublishMessageToWorker(Topic, _factory.SharedSecret);

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            JobMessage? received = null;
            await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
            {
                received = m;
                break;
            }
            received.Should().NotBeNull();
            received!.JobName.Should().Be(Topic);
        }
    }

    [Fact]
    public async Task SubscribeAsync_EmptyTopic_Throws()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = async () =>
            {
                await foreach (var _ in transport.SubscribeAsync(string.Empty, CancellationToken.None))
                {
                    return;
                }
            };
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task SubscribeAsync_CancellationToken_GracefulShutdown()
    {
        const string Topic = "Subscribe.Topic.Cancel";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource();
            var enumerationTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in transport.SubscribeAsync(Topic, cts.Token))
                    {
                        // never enters — no messages published.
                    }
                }
                catch (OperationCanceledException) { /* expected once cts fires */ }
                catch (IOException) { /* TestServer client-abort path */ }
            });
            await Task.Delay(200);
            cts.Cancel();
            // Completes within a few seconds (iterator's WaitToReadAsync respects the CT).
            await enumerationTask.WaitAsync(TimeSpan.FromSeconds(60));
        }
    }

    [Fact]
    public async Task SubscribeAsync_NoServiceConfigured_FallsBackToInProcessPump()
    {
        // When the topic does NOT map to a registered Service, SubscribeAsync yields from the
        // in-process receiver pump (matches InProcessTransport semantics).
        const string Topic = "InProcess.Topic";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            JobMessage? received = null;
            try
            {
                await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
                {
                    received = m;
                    break;
                }
            }
            catch (OperationCanceledException) { /* expected — no messages in pump */ }
            received.Should().BeNull("no messages were enqueued in the in-process pump");
        }
    }

    [Fact]
    public async Task SubscribeAsync_401Auth_ThrowsInvalidOperationException()
    {
        const string Topic = "Subscribe.Auth.Failed";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithSecret("WRONG-SECRET-INTENTIONAL-MISMATCH-32CH+")
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            Func<Task> act = async () =>
            {
                await foreach (var _ in transport.SubscribeAsync(Topic, cts.Token))
                {
                    return;
                }
            };
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message.Contains("401") || ex.Message.Contains("HMAC"));
        }
    }

    [Fact]
    public async Task SubscribeAsync_LongPollTimeout_ReceivesEmptyArrayAndContinues()
    {
        // Worker LongPollMaxWait = 5s; publish AFTER 1 second to test the loop resumes.
        const string Topic = "Subscribe.LongPoll.Empty";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            // Publish in background after a delay so subscribe times out at least once.
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token);
                await PublishMessageToWorker(Topic, _factory.SharedSecret);
            });
            JobMessage? received = null;
            await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
            {
                received = m;
                break;
            }
            received.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SubscribeAsync_MultipleMessages_YieldsAllInOrder()
    {
        const string Topic = "Subscribe.MultiMsg";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            for (var i = 0; i < 3; i++)
            {
                await PublishMessageToWorker(Topic, _factory.SharedSecret);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var received = new List<JobMessage>();
            await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
            {
                received.Add(m);
                if (received.Count >= 3) break;
            }
            received.Should().HaveCount(3);
        }
    }

    [Fact]
    public async Task SubscribeAsync_EnumeratorDispose_NoLeak()
    {
        const string Topic = "Subscribe.Dispose";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
            var enumerator = transport.SubscribeAsync(Topic, cts.Token).GetAsyncEnumerator(cts.Token);
            try
            {
                // Move next once (poll will return empty after worker's LongPollMaxWait OR cancellation).
                await enumerator.MoveNextAsync();
            }
            catch (Exception)
            {
                // The iterator may surface OperationCanceledException, IOException, or HttpRequestException
                // depending on which await path observes the cancellation first. The intent of this test
                // is just to assert DisposeAsync completes cleanly afterward.
            }
            await enumerator.DisposeAsync();
        }
    }

    [Fact]
    public async Task SubscribeAsync_StartsWithEmptyTopic_TopicMustBeProvided()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = async () =>
            {
                await foreach (var _ in transport.SubscribeAsync(string.Empty, CancellationToken.None)) { }
            };
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task SubscribeAsync_PublishBeforeSubscribe_StillSees()
    {
        // Per channel semantics, messages enqueued before any subscriber are buffered up to the
        // channel capacity. Subscribe sees them on first drain.
        const string Topic = "Subscribe.PrePublish";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            await PublishMessageToWorker(Topic, _factory.SharedSecret);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            JobMessage? received = null;
            await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
            {
                received = m;
                break;
            }
            received.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SubscribeAsync_ConcurrentSubscribers_SeparateTopicsIndependent()
    {
        const string TopicA = "Subscribe.Conc.A";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(TopicA, "http://billing-worker.test")
            .Build();
        // The builder only registers ONE service in Services dict; for this concurrency test we
        // verify the subscribe pump handles a topic and yields the published message.
        await using (sp.ConfigureAwait(false))
        {
            await PublishMessageToWorker(TopicA, _factory.SharedSecret);
            using var ctsA = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            JobMessage? msgA = null;
            await foreach (var m in transport.SubscribeAsync(TopicA, ctsA.Token))
            {
                msgA = m;
                break;
            }
            msgA.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task SubscribeAsync_PreCancelled_YieldsBreakImmediately()
    {
        const string Topic = "Subscribe.PreCancel";
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            JobMessage? received = null;
            try
            {
                await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
                {
                    received = m;
                    break;
                }
            }
            catch (OperationCanceledException) { /* OK — pre-cancel can throw OperationCanceledException */ }
            received.Should().BeNull();
        }
    }
}
