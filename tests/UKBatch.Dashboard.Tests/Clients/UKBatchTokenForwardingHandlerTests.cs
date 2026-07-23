using System.Net;
using FluentAssertions;
using UKBatch.AspNetCore;
using UKBatch.Dashboard.Clients;
using Xunit;

namespace UKBatch.Dashboard.Tests.Clients;

/// <summary>
/// The token-forwarding delegating handler attaches the signed-in user's bearer to outbound REST calls
/// when the accessor yields a token, and sends the request unchanged when it does not (the auth-off /
/// no-session path).
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

    private static async Task<HttpRequestMessage> SendThroughAsync(string? token)
    {
        var capturing = new CapturingHandler();
        using var handler = new UKBatchTokenForwardingHandler(new StubAccessor(token)) { InnerHandler = capturing };
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("http://svc.local/api/jobs"));

        using var _ = await invoker.SendAsync(request, CancellationToken.None);
        return capturing.LastRequest!;
    }

    [Fact]
    public async Task TokenPresent_AttachesBearerHeader()
    {
        var forwarded = await SendThroughAsync("access-token-123");

        forwarded.Headers.Authorization.Should().NotBeNull();
        forwarded.Headers.Authorization!.Scheme.Should().Be("Bearer");
        forwarded.Headers.Authorization.Parameter.Should().Be("access-token-123");
    }

    [Fact]
    public async Task TokenNull_SendsWithoutAuthorizationHeader()
    {
        var forwarded = await SendThroughAsync(token: null);

        forwarded.Headers.Authorization.Should().BeNull("no session ⇒ the request is forwarded unchanged");
    }

    [Fact]
    public async Task TokenEmpty_SendsWithoutAuthorizationHeader()
    {
        var forwarded = await SendThroughAsync(token: string.Empty);

        forwarded.Headers.Authorization.Should().BeNull("an empty token is treated as no token");
    }
}
