using System.Security.Claims;
using FluentAssertions;
using UKBatch.AspNetCore.OpenIdConnect;
using UKBatch.AspNetCore.OpenIdConnect.Tests.Support;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Unit tests for the nested-role flattening: identity-provider realm/resource role arrays become
/// standard <see cref="ClaimTypes.Role"/> claims that drive both the operator policy and the approval
/// gate. Covers the wildcard client path, idempotency, robustness to bad input, and — critically — that
/// the transformation keeps the principal authenticated (a bare-claims clone would break
/// RequireAuthenticatedUser and the approver harvest).
/// </summary>
public sealed class KeycloakRoleFlatteningTransformationTests
{
    private const string RealmClaim = "realm_access";
    private const string ResourceClaim = "resource_access";
    private const string Sentinel = KeycloakRoleFlatteningTransformation.FlattenedSentinelClaimType;

    private static KeycloakRoleFlatteningTransformation Build(params string[] roleClaimPaths)
    {
        var options = new UKBatchOpenIdConnectOptions();
        if (roleClaimPaths.Length > 0)
        {
            options.RoleClaimPaths = roleClaimPaths.ToList();
        }

        return new KeycloakRoleFlatteningTransformation(
            new StaticOptionsMonitor<UKBatchOpenIdConnectOptions>(options));
    }

    private static ClaimsPrincipal AuthenticatedPrincipal(params Claim[] claims)
    {
        var identity = new ClaimsIdentity(
            claims,
            authenticationType: "TestAuth",
            nameType: "preferred_username",
            roleType: ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    [Fact]
    public async Task RealmRoles_FlattenedToRoleClaims()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "{\"roles\":[\"batch-operator\",\"x\"]}"));

        var result = await Build().TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo("batch-operator", "x");
    }

    [Fact]
    public async Task ResourceAccessRoles_FlattenedAcrossEveryClientViaWildcard()
    {
        var principal = AuthenticatedPrincipal(new Claim(
            ResourceClaim,
            "{\"ukbatch-api\":{\"roles\":[\"y\"]},\"account\":{\"roles\":[\"z\"]}}"));

        var result = await Build().TransformAsync(principal);

        // The default resource_access.*.roles path fans out over every client object.
        result.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo("y", "z");
    }

    [Fact]
    public async Task RealmAndResource_BothFlattened()
    {
        var principal = AuthenticatedPrincipal(
            new Claim(RealmClaim, "{\"roles\":[\"batch-operator\"]}"),
            new Claim(ResourceClaim, "{\"ukbatch-api\":{\"roles\":[\"y\"]}}"));

        var result = await Build().TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value)
            .Should().BeEquivalentTo("batch-operator", "y");
    }

    [Fact]
    public async Task Transform_IsIdempotent_SentinelOnce_NoDuplicateRoles()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "{\"roles\":[\"batch-operator\"]}"));
        var sut = Build();

        await sut.TransformAsync(principal);
        var result = await sut.TransformAsync(principal); // second pass — must be a no-op

        result.FindAll(Sentinel).Should().HaveCount(1, "the flattened marker is added exactly once");
        result.FindAll(ClaimTypes.Role).Count(c => c.Value == "batch-operator")
            .Should().Be(1, "a repeated pass must not duplicate role claims");
    }

    [Fact]
    public async Task MalformedJson_DoesNotThrow_AddsNoRoles()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "not-json {{{"));
        var sut = Build();

        var act = async () => await sut.TransformAsync(principal);

        await act.Should().NotThrowAsync();
        principal.FindAll(ClaimTypes.Role).Should().BeEmpty();
    }

    [Fact]
    public async Task MissingSourceClaim_DoesNotThrow_AddsNoRoles_ButStampsSentinel()
    {
        var principal = AuthenticatedPrincipal(new Claim("preferred_username", "alice"));

        var result = await Build().TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty();
        result.HasClaim(c => c.Type == Sentinel).Should().BeTrue();
    }

    [Fact]
    public async Task Transform_KeepsIdentityAuthenticated_SoRequireAuthenticatedUserStillPasses()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "{\"roles\":[\"batch-operator\"]}"));

        var result = await Build().TransformAsync(principal);

        result.Identity!.IsAuthenticated.Should().BeTrue();
        result.Identity.AuthenticationType.Should().Be("TestAuth", "the primary identity is amended in place");
        // This is exactly what RequireAuthenticatedUser() evaluates.
        result.Identities.Any(i => i.IsAuthenticated).Should().BeTrue();
    }

    [Fact]
    public async Task EmptyRoleClaimPaths_DisablesFlattening()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "{\"roles\":[\"batch-operator\"]}"));
        var options = new UKBatchOpenIdConnectOptions { RoleClaimPaths = new List<string>() };
        var sut = new KeycloakRoleFlatteningTransformation(
            new StaticOptionsMonitor<UKBatchOpenIdConnectOptions>(options));

        var result = await sut.TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Should().BeEmpty("an empty path list disables flattening");
    }

    [Fact]
    public async Task NonStringRoleValues_AreSkipped()
    {
        var principal = AuthenticatedPrincipal(new Claim(RealmClaim, "{\"roles\":[\"ok\",123,null,true]}"));

        var result = await Build().TransformAsync(principal);

        result.FindAll(ClaimTypes.Role).Select(c => c.Value).Should().BeEquivalentTo("ok");
    }

    [Fact]
    public async Task UnauthenticatedPrincipal_ReturnedUnchanged_NoSentinel()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity()); // no auth type → not authenticated

        var result = await Build().TransformAsync(principal);

        result.Should().BeSameAs(principal);
        result.HasClaim(c => c.Type == Sentinel).Should().BeFalse();
    }
}
