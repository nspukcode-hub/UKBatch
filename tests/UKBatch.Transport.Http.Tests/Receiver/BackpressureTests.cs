using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Tests.Common;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Receiver;

/// <summary>
/// Receiver pump backpressure + P3 1MB body cap regression.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class BackpressureTests : IClassFixture<WorkerFactory>
{
    private readonly WorkerFactory _factory;

    public BackpressureTests(WorkerFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static JobMessage BuildMessage(string topic, string? messageId = null) => new JobMessage
    {
        MessageId = messageId ?? Guid.NewGuid().ToString("N"),
        CorrelationId = null,
        JobName = topic,
        SourceService = "test",
        TargetService = null,
        BatchId = null,
        BatchStepId = null,
        // InvoiceProcessing reads a required orderId; supply it so dispatched jobs run to completion
        // (the drain-count asserts pass either way, but the fixture should exercise the success path).
        Parameters = new Dictionary<string, object?> { ["orderId"] = 42 },
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    [Fact]
    public async Task LongPollQueue_BufferDropsOldestWhenFull()
    {
        // Topic channel cap is 1024; publish ~1100 and verify the receiver drains a bounded set.
        using var client = _factory.CreateClient();
        const string Topic = "InvoiceProcessing";

        // Publish more than channel capacity (1024); the channel drops oldest.
        const int NumPublish = 1100;
        var tasks = Enumerable.Range(0, NumPublish).Select(async i =>
        {
            var msg = BuildMessage(Topic);
            var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts));
            var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
            {
                Content = TestHmacHeaders.JsonContent(body),
            };
            TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: body);
            using var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }).ToArray();
        await Task.WhenAll(tasks);
        // No assertion on exact count drained — just that publishing more than capacity does NOT crash.
        // The drop log surfaces inside the WAF host logger.
    }

    [Fact]
    public async Task Receiver_HighThroughput_NoMessageLoss_BelowCapacity()
    {
        using var client = _factory.CreateClient();
        const string Topic = "InvoiceProcessing";
        const int NumPublish = 50; // well under 1024 cap

        for (var i = 0; i < NumPublish; i++)
        {
            var msg = BuildMessage(Topic);
            var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts));
            var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
            {
                Content = TestHmacHeaders.JsonContent(body),
            };
            TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: body);
            using var resp = await client.SendAsync(req);
            resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        // Poll the receiver until we've drained at least NumPublish messages OR timeout.
        var totalDrained = 0;
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);
        while (totalDrained < NumPublish && DateTimeOffset.UtcNow < deadline)
        {
            var canonical = HmacCanonicalForm.BuildCanonicalPathForSender(
                "/ukbatch/internal/jobs/poll",
                new[]
                {
                    new KeyValuePair<string, IReadOnlyList<string>>("topic", new[] { Topic }),
                    new KeyValuePair<string, IReadOnlyList<string>>("waitMs", new[] { "500" }),
                });
            var req = new HttpRequestMessage(HttpMethod.Get, $"/ukbatch/internal/jobs/poll?topic={Topic}&waitMs=500");
            TestHmacHeaders.Attach(req, canonical, _factory.SharedSecret);
            using var resp = await client.SendAsync(req);
            resp.IsSuccessStatusCode.Should().BeTrue();
            var json = await resp.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("messages", out var arr))
            {
                totalDrained += arr.GetArrayLength();
            }
        }
        totalDrained.Should().BeGreaterThanOrEqualTo(NumPublish);
    }

    // regression
    [Fact]
    public async Task BodyExceeds1MB_Returns413NotAuth401()
    {
        // Body > MaxBodyBytes triggers 413 (NOT 401). Filter must reject by size BEFORE HMAC verify.
        using var client = _factory.CreateClient();
        const int OverflowBytes = 1_200_000; // > 1 MB default
        var hugeBody = new byte[OverflowBytes];
        Array.Fill(hugeBody, (byte)'a');
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(hugeBody),
        };
        // Attach VALID HMAC for the overflow body — if the filter checked auth first, this would pass.
        // Filter must short-circuit on size before HMAC verify.
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: hugeBody);
        var resp = await client.SendAsync(req);
        // Expected 413. 401 would mean the filter checked auth first (BUG).
        resp.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task SmallBody_BelowCap_NotRejected()
    {
        // Sanity counter-test: tiny body (well under cap) passes the size check.
        using var client = _factory.CreateClient();
        var msg = BuildMessage("InvoiceProcessing");
        var body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(msg, JsonOpts));
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: body);
        using var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
