using FluentAssertions;
using Microsoft.Extensions.Options;
using UKBatch.AspNetCore.OpenIdConnect;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Unit tests for the options validator. A misconfigured authority, missing client id, or empty/dirty
/// operator-role list must fail fast at host start rather than silently granting or denying everyone.
/// Mirrors the shape of the core approval-role-claim-types validator tests.
/// </summary>
public sealed class UKBatchOpenIdConnectOptionsValidatorTests
{
    private static ValidateOptionsResult Validate(Action<UKBatchOpenIdConnectOptions> mutate)
    {
        // Start from a valid baseline, then mutate a single field per case.
        var options = new UKBatchOpenIdConnectOptions
        {
            Authority = "https://idp.example.com/realms/ukbatch",
            ClientId = "ukbatch-dashboard",
            OperatorRoles = new List<string> { "batch-operator" },
        };
        mutate(options);
        return new UKBatchOpenIdConnectOptionsValidator().Validate(name: null, options);
    }

    [Fact]
    public void ValidConfiguration_Succeeds()
    {
        Validate(_ => { }).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void EmptyOperatorRoles_Fails()
    {
        Validate(o => o.OperatorRoles = new List<string>()).Failed.Should().BeTrue();
    }

    [Fact]
    public void WhitespaceOperatorRole_Fails()
    {
        Validate(o => o.OperatorRoles = new List<string> { "   " }).Failed.Should().BeTrue();
    }

    [Fact]
    public void DuplicateOperatorRoles_Fails()
    {
        Validate(o => o.OperatorRoles = new List<string> { "ops", "ops" }).Failed.Should().BeTrue();
    }

    [Fact]
    public void NullAuthority_Fails()
    {
        Validate(o => o.Authority = null).Failed.Should().BeTrue();
    }

    [Fact]
    public void WhitespaceAuthority_Fails()
    {
        Validate(o => o.Authority = "   ").Failed.Should().BeTrue();
    }

    [Fact]
    public void NonAbsoluteAuthority_Fails()
    {
        Validate(o => o.Authority = "realms/ukbatch").Failed.Should().BeTrue();
    }

    [Fact]
    public void NonHttpAuthorityScheme_Fails()
    {
        Validate(o => o.Authority = "ftp://idp.example.com/realms/ukbatch").Failed.Should().BeTrue();
    }

    [Fact]
    public void MissingClientId_Fails()
    {
        Validate(o => o.ClientId = null).Failed.Should().BeTrue();
    }

    [Fact]
    public void DuplicateRoleClaimPaths_Fails()
    {
        Validate(o => o.RoleClaimPaths = new List<string> { "realm_access.roles", "realm_access.roles" })
            .Failed.Should().BeTrue();
    }

    [Fact]
    public void WhitespaceRoleClaimPath_Fails()
    {
        Validate(o => o.RoleClaimPaths = new List<string> { "  " }).Failed.Should().BeTrue();
    }
}
