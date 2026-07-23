using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using UKBatch.AspNetCore.OpenIdConnect.Tests.Support;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Locks the sign-out eviction contract of the per-user token store: a removed key reads back null,
/// so a signed-out session (including a still-open circuit resolving from the store) can no longer
/// obtain the cached token — even when a token refresh was already in flight when sign-out happened.
/// Also locks the injectivity of the per-user key.
/// </summary>
public sealed class UKBatchUserTokenStoreTests
{
    private static UKBatchUserTokenStore CreateStore() => new(
        new NoopHttpClientFactory(),
        new StaticOptionsMonitor<OpenIdConnectOptions>(new OpenIdConnectOptions()),
        NullLogger<UKBatchUserTokenStore>.Instance);

    [Fact]
    public async Task Remove_EvictsTheSeededToken()
    {
        using var store = CreateStore();
        var tokens = new TokenSet("access-1", DateTimeOffset.UtcNow.AddHours(1), RefreshToken: null);
        store.Seed("sub|sid", tokens);
        Assert.Equal("access-1", await store.GetAccessTokenAsync("sub|sid", CancellationToken.None));

        store.Remove("sub|sid");

        Assert.Null(await store.GetAccessTokenAsync("sub|sid", CancellationToken.None));
    }

    [Fact]
    public async Task Remove_UnknownOrEmptyKey_IsANoOp()
    {
        using var store = CreateStore();
        store.Seed("kept", new TokenSet("access-kept", DateTimeOffset.UtcNow.AddHours(1), RefreshToken: null));

        store.Remove("unknown");
        store.Remove("");

        Assert.Equal("access-kept", await store.GetAccessTokenAsync("kept", CancellationToken.None));
    }

    // ---- Sign-out racing an in-flight refresh --------------------------------------------------

    [Fact]
    public async Task Remove_DuringInFlightRefresh_DoesNotResurrectTheTokens()
    {
        // The refresh completes AFTER sign-out evicted the key. Its write-back must not re-insert the
        // entry, or a still-open circuit would keep calling the API on a signed-out session.
        var handler = new BlockingRefreshHandler();
        using var store = new UKBatchUserTokenStore(
            new StubHttpClientFactory(handler),
            new StaticOptionsMonitor<OpenIdConnectOptions>(OptionsWithTokenEndpoint()),
            NullLogger<UKBatchUserTokenStore>.Instance);

        // Within the refresh skew and holding a refresh token → the read takes the refresh path.
        store.Seed("user", new TokenSet("stale", DateTimeOffset.UtcNow.AddSeconds(5), "refresh-1"));

        var inFlight = store.GetAccessTokenAsync("user", CancellationToken.None);
        await handler.RequestStarted;   // the refresh HTTP call is provably in flight

        store.Remove("user");           // sign-out
        handler.Release();

        Assert.Null(await inFlight);
        Assert.Null(await store.GetAccessTokenAsync("user", CancellationToken.None));
    }

    // ---- Key injectivity -----------------------------------------------------------------------

    [Fact]
    public void BuildKey_AmbiguousSubjectSessionSplits_ProduceDistinctKeys()
    {
        // Under a naive "{subject}|{session}" join these two principals render the same string, so one
        // user's circuit could read the other's tokens. The length-prefixed key keeps them distinct.
        var a = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "alice|s1", sessionId: "x"));
        var b = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "alice", sessionId: "s1|x"));

        Assert.NotNull(a);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void BuildKey_SubjectOnly_NeverCollidesWithACompositeKey()
    {
        // A subject that happens to CONTAIN the separator must not render the same key as a different
        // (subject, session) pair.
        var subjectOnly = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "a|b", sessionId: null));
        var composite = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "a", sessionId: "b"));

        Assert.NotNull(subjectOnly);
        Assert.NotEqual(subjectOnly, composite);
    }

    [Fact]
    public void BuildKey_SamePrincipalShape_IsStable()
    {
        // The seeding path (request scope) and the read path (circuit) build the key independently;
        // they must agree.
        var first = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "alice", sessionId: "s1"));
        var second = UKBatchUserTokenStore.BuildKey(PrincipalWith(subject: "alice", sessionId: "s1"));

        Assert.Equal(first, second);
    }

    // ---- helpers -------------------------------------------------------------------------------

    private static ClaimsPrincipal PrincipalWith(string subject, string? sessionId)
    {
        var claims = new List<Claim> { new("sub", subject) };
        if (sessionId is not null)
        {
            claims.Add(new Claim("sid", sessionId));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static OpenIdConnectOptions OptionsWithTokenEndpoint() => new()
    {
        ClientId = "dashboard",
        ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(
            new OpenIdConnectConfiguration { TokenEndpoint = "http://idp.local/token" }),
    };

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;
        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }

    /// <summary>
    /// Token-endpoint stub that signals when the refresh request has started and holds the response
    /// until released, so the test can interleave a sign-out deterministically mid-refresh.
    /// </summary>
    private sealed class BlockingRefreshHandler : HttpMessageHandler
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task RequestStarted => _started.Task;

        public void Release() => _release.TrySetResult();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await _release.Task.WaitAsync(TimeSpan.FromSeconds(60), cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"access_token\":\"fresh\",\"expires_in\":300,\"refresh_token\":\"refresh-2\"}",
                    Encoding.UTF8,
                    "application/json"),
            };
        }
    }
}
