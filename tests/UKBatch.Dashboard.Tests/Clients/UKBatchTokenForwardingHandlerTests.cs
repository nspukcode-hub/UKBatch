using System.Net;
using FluentAssertions;
using UKBatch.AspNetCore;
using UKBatch.Dashboard.Clients;
using Xunit;

namespace UKBatch.Dashboard.Tests.Clients;

/// <summary>
/// The token-forwarding delegating handler attaches the signed-in user's bearer to outbound REST calls
/// when the accessor yields a token, and sends the request unchanged when it does not (the auth-off /
/// no-session path). The bearer only travels over a channel that cannot leak it in cleartext: HTTPS, or
/// plain HTTP to a loopback address — any other plain-HTTP target gets the request without the token.
/// </summary>
public sealed class UKBatchTokenForwardingHandlerTests
{
    private sealed class StubAccessor : IUKBatchUserTokenAccessor
    {
        private readonly string? _token;
        public StubAccessor(string? token) => _token = token;
        public ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken) => new(_token);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static async Task<HttpRequestMessage> SendThroughAsync(string? token, string requestUri)
    {
        var capturing = new CapturingHandler();
        using var handler = new UKBatchTokenForwardingHandler(new StubAccessor(token)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(requestUri));

        using var _ = await invoker.SendAsync(request, CancellationToken.None);
        return capturing.LastRequest!;
    }

    [Fact]
    public async Task TokenPresent_Https_AttachesBearerHeader()
    {
        var forwarded = await SendThroughAsync("access-token-123", "https://svc.local/api/jobs");

        forwarded.Headers.Authorization.Should().NotBeNull();
        forwarded.Headers.Authorization!.Scheme.Should().Be("Bearer");
        forwarded.Headers.Authorization.Parameter.Should().Be("access-token-123");
    }

    [Fact]
    public async Task TokenPresent_PlainHttpLoopback_AttachesBearerHeader()
    {
        // The embedded topology: the dashboard calls its own host over plain-HTTP loopback. The token
        // never crosses a network there, so it is still forwarded.
        var forwarded = await SendThroughAsync("access-token-123", "http://localhost:5000/api/jobs");

        forwarded.Headers.Authorization.Should().NotBeNull("loopback plain HTTP never leaves the machine");
        forwarded.Headers.Authorization!.Parameter.Should().Be("access-token-123");
    }

    [Fact]
    public async Task TokenPresent_PlainHttpNonLoopback_DoesNotAttachBearer()
    {
        // The channel guard: a plain-HTTP non-loopback target would carry the user's token in cleartext
        // across the network. The request is sent WITHOUT the token so the API answers 401 instead.
        var forwarded = await SendThroughAsync("access-token-123", "http://svc.local/api/jobs");

        forwarded.Headers.Authorization.Should().BeNull(
            "the user's bearer must never travel plain HTTP to a non-loopback host");
    }

    [Fact]
    public async Task TokenNull_SendsWithoutAuthorizationHeader()
    {
        var forwarded = await SendThroughAsync(token: null, "https://svc.local/api/jobs");

        forwarded.Headers.Authorization.Should().BeNull("no session ⇒ the request is forwarded unchanged");
    }

    [Fact]
    public async Task TokenEmpty_SendsWithoutAuthorizationHeader()
    {
        var forwarded = await SendThroughAsync(token: string.Empty, "https://svc.local/api/jobs");

        forwarded.Headers.Authorization.Should().BeNull("an empty token is treated as no token");
    }
}
