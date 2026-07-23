using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.AspNetCore.OpenIdConnect.Tests.Support;
using UKBatch.AspNetCore.OpenIdConnect.Tokens;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Locks the sign-out eviction contract of the per-user token store: a removed key reads back null,
/// so a signed-out session (including a still-open circuit resolving from the store) can no longer
/// obtain the cached token.
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

    private sealed class NoopHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
