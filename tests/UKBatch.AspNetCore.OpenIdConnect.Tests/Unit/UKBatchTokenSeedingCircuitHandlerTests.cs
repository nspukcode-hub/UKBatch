using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.AspNetCore.OpenIdConnect.Tests.Support;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Locks the construction-time snapshot contract of the circuit seeding handler: BOTH the user key and
/// the tokens are captured while the connection request context is provably still the user's own. On a
/// non-WebSocket transport the pooled context can be recycled to a DIFFERENT user once the connect
/// request completes — a handler that read the retained context at circuit-open time could then pair
/// user A's key with user B's tokens (cross-user impersonation).
/// </summary>
public sealed class UKBatchTokenSeedingCircuitHandlerTests
{
    [Fact]
    public async Task Seed_UsesConstructionTimeSnapshot_NotLaterContextState()
    {
        var authService = new MutableAuthService();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection()
                .AddSingleton<IAuthenticationService>(authService)
                .BuildServiceProvider(),
            User = PrincipalFor("alice", sessionId: "s1"),
        };
        authService.AccessToken = "alice-access";

        using var store = CreateStore();
        var handler = new UKBatchTokenSeedingCircuitHandler(
            new HttpContextAccessor { HttpContext = context }, store);

        // Simulate the pooled connection context being recycled to a different user's request between
        // scope construction and circuit open.
        context.User = PrincipalFor("bob", sessionId: "s2");
        authService.AccessToken = "bob-access";

        await handler.OnCircuitOpenedAsync(circuit: null!, CancellationToken.None);

        var aliceKey = UKBatchUserTokenStore.BuildKey(PrincipalFor("alice", "s1"))!;
        var bobKey = UKBatchUserTokenStore.BuildKey(PrincipalFor("bob", "s2"))!;
        Assert.Equal("alice-access", await store.GetAccessTokenAsync(aliceKey, CancellationToken.None));
        Assert.Null(await store.GetAccessTokenAsync(bobKey, CancellationToken.None));
    }

    [Fact]
    public async Task NoHttpContext_SeedsNothing_AndDoesNotThrow()
    {
        using var store = CreateStore();
        var handler = new UKBatchTokenSeedingCircuitHandler(new HttpContextAccessor(), store);

        await handler.OnCircuitOpenedAsync(circuit: null!, CancellationToken.None);
    }

    [Fact]
    public async Task UnauthenticatedContext_SeedsNothing()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity()),
        };

        using var store = CreateStore();
        var handler = new UKBatchTokenSeedingCircuitHandler(
            new HttpContextAccessor { HttpContext = context }, store);

        await handler.OnCircuitOpenedAsync(circuit: null!, CancellationToken.None);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static UKBatchUserTokenStore CreateStore() => new(
        new NoopHttpClientFactory(),
        new StaticOptionsMonitor<OpenIdConnectOptions>(new OpenIdConnectOptions()),
        NullLogger<UKBatchUserTokenStore>.Instance);

    private static ClaimsPrincipal PrincipalFor(string subject, string sessionId) =>
        new(new ClaimsIdentity(
            new[] { new Claim("sub", subject), new Claim("sid", sessionId) },
            "test"));

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    /// <summary>
    /// Authentication stub whose issued access token can be swapped mid-test, standing in for the
    /// pooled context being reused by another user's authenticated request.
    /// </summary>
    private sealed class MutableAuthService : IAuthenticationService
    {
        public string? AccessToken { get; set; }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var properties = new AuthenticationProperties();
            if (AccessToken is not null)
            {
                properties.StoreTokens(new[]
                {
                    new AuthenticationToken { Name = "access_token", Value = AccessToken },
                });
            }

            return Task.FromResult(
                AuthenticateResult.Success(new AuthenticationTicket(context.User, properties, "Cookies")));
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) =>
            throw new NotSupportedException();

        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) =>
            throw new NotSupportedException();
    }
}
