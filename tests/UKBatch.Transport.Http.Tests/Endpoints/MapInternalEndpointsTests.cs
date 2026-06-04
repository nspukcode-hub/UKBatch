using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Tests.Common;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Endpoints;

/// <summary>
/// Receiver endpoint route map + HMAC filter + Cache-Control filter regression locks.
/// + #4.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class MapInternalEndpointsTests : IClassFixture<WorkerFactory>
{
    private readonly WorkerFactory _factory;

    public MapInternalEndpointsTests(WorkerFactory factory)
    {
        _factory = factory;
    }

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static string SerializeMessageString(JobMessage msg) =>
        JsonSerializer.Serialize(msg, JsonOpts);

    private static byte[] SerializeMessage(JobMessage msg) =>
        System.Text.Encoding.UTF8.GetBytes(SerializeMessageString(msg));

    private static JobMessage BuildValidMessage(string jobName = "InvoiceProcessing") => new JobMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        CorrelationId = null,
        JobName = jobName,
        SourceService = "orchestrator-test",
        TargetService = null,
        BatchId = null,
        BatchStepId = null,
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    // === Route map: 3 endpoints mounted ===

    [Fact]
    public async Task Map_PublishRoute_ResolvedToHandler()
    {
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage();
        var body = SerializeMessage(msg);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: body);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task Map_PollRoute_ResolvedToHandler()
    {
        using var client = _factory.CreateClient();
        var canonical = HmacCanonicalForm.BuildCanonicalPathForSender(
            "/ukbatch/internal/jobs/poll",
            new[]
            {
                new KeyValuePair<string, IReadOnlyList<string>>("topic", new[] { "noopic" }),
                new KeyValuePair<string, IReadOnlyList<string>>("waitMs", new[] { "100" }),
            });
        var req = new HttpRequestMessage(HttpMethod.Get, "/ukbatch/internal/jobs/poll?topic=noopic&waitMs=100");
        TestHmacHeaders.Attach(req, canonical, _factory.SharedSecret);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await resp.Content.ReadAsStringAsync();
        json.Should().Contain("messages");
    }

    [Fact]
    public async Task Map_InvokeRoute_ResolvedToHandler()
    {
        // Invoke with a registered job — should accept the request envelope; the actual exec returns
        // 200 (or 408 if it times out before completing). We just need to prove the route was matched
        // (NOT 404 — the canonical path concerns).
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage();
        var body = SerializeMessage(msg);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body);
        var resp = await client.SendAsync(req);
        // 200 = completed in time; 408 = invoke timed out; both prove the route is mounted.
        resp.StatusCode.Should().Match(s => s == HttpStatusCode.OK || s == HttpStatusCode.RequestTimeout);
    }

    // === HMAC filter applied ===

    [Fact]
    public async Task Endpoints_WithoutHMACHeaders_Return401()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            new Uri("/ukbatch/internal/jobs/publish", UriKind.Relative),
            TestHmacHeaders.JsonContent("{}"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Endpoints_WithBadSignature_Return401()
    {
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage();
        var body = SerializeMessage(msg);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        // Sign with WRONG secret.
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", "WRONG-SECRET", bodyBytes: body);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // === Cache-Control filter (all paths — including failures) ===

    [Fact]
    public async Task Endpoints_AllResponses_HaveCacheControlNoStore()
    {
        // invariant: Cache-Control: no-store applied unconditionally regardless of HMAC outcome.
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage();
        var body = SerializeMessage(msg);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/publish")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/publish", _factory.SharedSecret, bodyBytes: body);
        var resp = await client.SendAsync(req);
        resp.IsSuccessStatusCode.Should().BeTrue();
        AssertCacheControlNoStore(resp);
    }

    // regression
    [Fact]
    public async Task Subscribe_AuthFailed401_StillHasCacheControlNoStoreHeader()
    {
        // Cache-Control filter is registered BEFORE the HMAC filter so even auth-rejected responses
        // carry no-store / Pragma headers. Required so proxies (nginx / CDN) do not cache a 401 body.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/ukbatch/internal/jobs/poll?topic=x", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        AssertCacheControlNoStore(resp);
    }

    // === MessageId dedupe (Invoke) ===

    [Fact]
    public async Task InvokeEndpoint_DuplicateMessageId_ReturnsCachedResult()
    {
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage();
        var body = SerializeMessage(msg);

        // First call — synchronous, returns 200 with JobResult.
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req1, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body);
        var resp1 = await client.SendAsync(req1);
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var body1 = await resp1.Content.ReadAsStringAsync();

        // Second call — same MessageId, fresh nonce/timestamp (new req).
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req2, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body);
        var resp2 = await client.SendAsync(req2);
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var body2 = await resp2.Content.ReadAsStringAsync();

        // Cached replay returns the same executionId.
        using var d1 = JsonDocument.Parse(body1);
        using var d2 = JsonDocument.Parse(body2);
        d1.RootElement.GetProperty("executionId").GetString()
            .Should().Be(d2.RootElement.GetProperty("executionId").GetString());
    }

    [Fact]
    public async Task InvokeEndpoint_DifferentMessageId_ProcessesIndependently()
    {
        using var client = _factory.CreateClient();
        var msg1 = BuildValidMessage();
        var msg2 = BuildValidMessage();
        msg2.MessageId.Should().NotBe(msg1.MessageId);

        var body1 = SerializeMessage(msg1);
        var body2 = SerializeMessage(msg2);
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body1),
        };
        TestHmacHeaders.Attach(req1, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body1);
        var resp1 = await client.SendAsync(req1);
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body2),
        };
        TestHmacHeaders.Attach(req2, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body2);
        var resp2 = await client.SendAsync(req2);

        var b1 = await resp1.Content.ReadAsStringAsync();
        var b2 = await resp2.Content.ReadAsStringAsync();
        using var d1 = JsonDocument.Parse(b1);
        using var d2 = JsonDocument.Parse(b2);
        d1.RootElement.GetProperty("executionId").GetString()
            .Should().NotBe(d2.RootElement.GetProperty("executionId").GetString());
    }

    // regression
    [Fact]
    public async Task InvokeEndpoint_JobNotRegistered_Returns404TypedProblem()
    {
        using var client = _factory.CreateClient();
        var msg = BuildValidMessage(jobName: "NonExistent.Job");
        var body = SerializeMessage(msg);
        var req = new HttpRequestMessage(HttpMethod.Post, "/ukbatch/internal/jobs/invoke")
        {
            Content = TestHmacHeaders.JsonContent(body),
        };
        TestHmacHeaders.Attach(req, "/ukbatch/internal/jobs/invoke", _factory.SharedSecret, bodyBytes: body);
        var resp = await client.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var content = await resp.Content.ReadAsStringAsync();
        content.Should().Contain("ukbatch:job-not-registered");
    }

    // regression
    [Fact]
    public async Task PollEndpoint_ClientWaitMs_ClampedToServerMax()
    {
        // Server LongPollMaxWait is 5s; ask for 60 000ms — should return ≤ 5s.
        using var client = _factory.CreateClient();
        var canonical = HmacCanonicalForm.BuildCanonicalPathForSender(
            "/ukbatch/internal/jobs/poll",
            new[]
            {
                new KeyValuePair<string, IReadOnlyList<string>>("topic", new[] { "no-msg-topic" }),
                new KeyValuePair<string, IReadOnlyList<string>>("waitMs", new[] { "60000" }),
            });
        var req = new HttpRequestMessage(HttpMethod.Get, "/ukbatch/internal/jobs/poll?topic=no-msg-topic&waitMs=60000");
        TestHmacHeaders.Attach(req, canonical, _factory.SharedSecret);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var resp = await client.SendAsync(req);
        sw.Stop();
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        // ≤ server cap (5s) + generous slack for CI variability.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }

    private static void AssertCacheControlNoStore(HttpResponseMessage resp)
    {
        // Cache-Control may be stored on Headers or Content.Headers depending on response shape.
        var allHeaders = resp.Headers.Concat(resp.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(kv => kv.Key, kv => string.Join(",", kv.Value), StringComparer.OrdinalIgnoreCase);
        allHeaders.Should().ContainKey("Cache-Control");
        allHeaders["Cache-Control"].ToLowerInvariant().Should().Contain("no-store");
    }
}
