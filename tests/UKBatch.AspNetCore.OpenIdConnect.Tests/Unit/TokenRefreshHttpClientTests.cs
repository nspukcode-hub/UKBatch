using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Locks the transport posture of the token-refresh HTTP client: it must never follow redirects. The
/// refresh POST carries the client secret and the refresh token in its body, so honouring a 307/308
/// would re-send both to whatever host the redirect names.
/// </summary>
public sealed class TokenRefreshHttpClientTests
{
    [Fact]
    public void RefreshClient_PrimaryHandler_DoesNotFollowRedirects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatchOpenIdConnect(o =>
        {
            o.Authority = "https://idp.example/realms/demo";
            o.ClientId = "dashboard";
            o.OperatorRoles = new List<string> { "batch-operator" };
        });

        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(UKBatchUserTokenStore.RefreshHttpClientName);

        // Walk the delegating chain (lifetime-tracking wrapper + any user handlers) to the primary.
        HttpMessageHandler current = handler;
        while (current is DelegatingHandler { InnerHandler: not null } delegating)
        {
            current = delegating.InnerHandler;
        }

        current.Should().BeOfType<SocketsHttpHandler>()
            .Which.AllowAutoRedirect.Should().BeFalse(
                "a redirected token endpoint must never receive the client secret and refresh token");
    }
}
