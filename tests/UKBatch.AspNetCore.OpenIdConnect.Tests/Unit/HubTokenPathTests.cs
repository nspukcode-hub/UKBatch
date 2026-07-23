using UKBatch.AspNetCore.OpenIdConnect;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Locks the segment-boundary contract for query-string bearer acceptance: only the hub path itself
/// (at any mount depth) and its sub-paths may carry an <c>access_token</c> query parameter — a
/// lookalike route must never widen where a bearer rides in a loggable URL.
/// </summary>
public sealed class HubTokenPathTests
{
    [Theory]
    // The hub itself, bare and under a mount prefix.
    [InlineData("/hubs/jobs", "/hubs/jobs", true)]
    [InlineData("/api/hubs/jobs", "/hubs/jobs", true)]
    // SignalR sub-paths (negotiate / transport).
    [InlineData("/api/hubs/jobs/negotiate", "/hubs/jobs", true)]
    [InlineData("/hubs/jobs/negotiate", "/hubs/jobs", true)]
    // Case-insensitive routing.
    [InlineData("/API/HUBS/JOBS", "/hubs/jobs", true)]
    // Lookalike suffix in the same segment must NOT match.
    [InlineData("/api/hubs/jobs-exfil", "/hubs/jobs", false)]
    [InlineData("/hubs/jobsx", "/hubs/jobs", false)]
    // Missing leading segment boundary must NOT match.
    [InlineData("/apihubs/jobs", "/hubs/jobs", false)]
    [InlineData("/api/not-hubs/jobs", "/hubs/jobs", false)]
    // Unrelated paths.
    [InlineData("/api/jobs", "/hubs/jobs", false)]
    [InlineData("/", "/hubs/jobs", false)]
    // A later occurrence may still match when an earlier one fails the boundary.
    [InlineData("/hubs/jobsx/hubs/jobs", "/hubs/jobs", true)]
    // A configured hub path without a leading slash is normalized before matching.
    [InlineData("/api/hubs/jobs", "hubs/jobs", true)]
    [InlineData("/apihubs/jobs", "hubs/jobs", false)]
    public void IsHubTokenRequest_MatchesOnlyWholeSegments(string requestPath, string hubPath, bool expected)
        => Assert.Equal(expected, OpenIdConnectServiceCollectionExtensions.IsHubTokenRequest(requestPath, hubPath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void IsHubTokenRequest_EmptyPath_NeverMatches(string? requestPath)
        => Assert.False(OpenIdConnectServiceCollectionExtensions.IsHubTokenRequest(requestPath, "/hubs/jobs"));
}
