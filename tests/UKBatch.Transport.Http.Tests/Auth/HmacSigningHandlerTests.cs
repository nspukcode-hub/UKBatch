using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using UKBatch.Transport.Http;
using UKBatch.Transport.Http.Auth;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Auth;

/// <summary>
/// P3 unit-level lock: <see cref="HmacSigningHandler"/> attaches HMAC
/// headers on every <see cref="HttpRequestMessage"/> dispatch, rotating nonce + timestamp per call.
/// Polly retry-level integration is exercised in <c>PollyRetryTests</c>.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HmacSigningHandlerTests
{
    private static (HmacSigningHandler handler, RecorderHandler inner, FakeTimeProvider clock) BuildPipeline()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var signer = new HmacSignatureService(Microsoft.Extensions.Options.Options.Create(new HttpTransportOptions
        {
            SharedSecret = TestSecret,
        }));
        var inner = new RecorderHandler();
        var handler = new HmacSigningHandler(signer, clock)
        {
            InnerHandler = inner,
        };
        return (handler, inner, clock);
    }

    private const string TestSecret = "TEST-SECRET-32B+";

    [Fact]
    public async Task SendAsync_AttachesHMACHeaders()
    {
        var (handler, inner, _) = BuildPipeline();
        var request = new HttpRequestMessage(HttpMethod.Post, "http://example/jobs/publish");
        HmacSigningHandler.AttachCanonicalPath(request, "/jobs/publish");
        request.Content = new StringContent("body");
        using var invoker = new HttpMessageInvoker(handler);
        var resp = await invoker.SendAsync(request, CancellationToken.None);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        inner.LastRequest!.Headers.GetValues("X-UKBatch-Signature").Single().Should().NotBeNullOrWhiteSpace();
        inner.LastRequest.Headers.GetValues("X-UKBatch-Timestamp").Single().Should().NotBeNullOrWhiteSpace();
        inner.LastRequest.Headers.GetValues("X-UKBatch-Nonce").Single().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task SendAsync_GeneratesUniqueNoncePerCall()
    {
        var (handler, inner, _) = BuildPipeline();
        var nonces = new List<string>();
        using var invoker = new HttpMessageInvoker(handler);
        for (var i = 0; i < 5; i++)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "http://example/jobs/publish");
            HmacSigningHandler.AttachCanonicalPath(req, "/jobs/publish");
            req.Content = new StringContent("body");
            await invoker.SendAsync(req, CancellationToken.None);
            nonces.Add(inner.LastRequest!.Headers.GetValues("X-UKBatch-Nonce").Single());
        }
        nonces.Distinct().Count().Should().Be(5);
    }

    [Fact]
    public async Task SendAsync_GeneratesFreshTimestampPerCall()
    {
        var (handler, inner, clock) = BuildPipeline();
        var timestamps = new List<long>();
        using var invoker = new HttpMessageInvoker(handler);
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            var req = new HttpRequestMessage(HttpMethod.Post, "http://example/jobs/publish");
            HmacSigningHandler.AttachCanonicalPath(req, "/jobs/publish");
            req.Content = new StringContent("body");
            await invoker.SendAsync(req, CancellationToken.None);
            timestamps.Add(long.Parse(inner.LastRequest!.Headers.GetValues("X-UKBatch-Timestamp").Single()));
        }
        timestamps.Should().BeInAscendingOrder();
        timestamps.Distinct().Count().Should().Be(3);
    }

    [Fact]
    public async Task SendAsync_PreservesInnerHandlerResponse()
    {
        var (handler, inner, _) = BuildPipeline();
        inner.NextResponse = new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = new StringContent("body content"),
        };
        var request = new HttpRequestMessage(HttpMethod.Post, "http://example/jobs/publish");
        HmacSigningHandler.AttachCanonicalPath(request, "/jobs/publish");
        using var invoker = new HttpMessageInvoker(handler);
        var resp = await invoker.SendAsync(request, CancellationToken.None);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await resp.Content.ReadAsStringAsync()).Should().Be("body content");
    }

    [Fact]
    public async Task SendAsync_MissingCanonicalPath_ThrowsInvalidOperation()
    {
        var (handler, _, _) = BuildPipeline();
        var request = new HttpRequestMessage(HttpMethod.Post, "http://example/jobs/publish");
        // Did NOT call AttachCanonicalPath — handler must refuse to sign with the wrong canonical.
        using var invoker = new HttpMessageInvoker(handler);
        Func<Task> act = async () => await invoker.SendAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    private sealed class RecorderHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage NextResponse { get; set; } = new HttpResponseMessage(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Clone the request headers so the test sees them even after the dispatcher disposes.
            LastRequest = new HttpRequestMessage(request.Method, request.RequestUri);
            foreach (var h in request.Headers)
            {
                LastRequest.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return Task.FromResult(NextResponse);
        }
    }
}
