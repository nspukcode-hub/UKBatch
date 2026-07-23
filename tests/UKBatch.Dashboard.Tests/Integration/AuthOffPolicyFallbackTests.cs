using System.Security.Claims;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using UKBatch.AspNetCore;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// Locks the fallback semantics of the auth-off viewer/operator policies. They exist so the UI's
/// authorization views render under the open default — but they must NEVER overwrite a same-named
/// policy the host has already defined: the policy names are the cross-package contract a host uses to
/// role-gate its API, and clobbering them into always-true would open the gated surface to callers the
/// host's own policy rejects.
/// </summary>
public sealed class AuthOffPolicyFallbackTests
{
    private static ServiceCollection BaseServices()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddLogging();
        return services;
    }

    private static void ConfigureOneService(DashboardOptions options) =>
        options.Services.Add(new UKBatchServiceDescriptor
        {
            Name = "svc",
            BaseUrl = new Uri("http://svc.local:5000/api/"),
        });

    private static ClaimsPrincipal AuthenticatedUserWithoutRoles() =>
        new(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "user") }, "test"));

    [Fact]
    public async Task HostPolicyRegisteredBeforeDashboard_IsNotOverwritten()
    {
        var services = BaseServices();
        services.AddAuthorization(o =>
            o.AddPolicy("UKBatch:Operator", p => p.RequireRole("real-operators")));
        services.AddUKBatchDashboard(ConfigureOneService);

        await using var provider = services.BuildServiceProvider();

        // The host's requirement must still be in the policy — the fallback must not have replaced it
        // with an always-true assertion.
        var options = provider.GetRequiredService<IOptions<AuthorizationOptions>>().Value;
        var policy = options.GetPolicy("UKBatch:Operator");
        policy.Should().NotBeNull();
        policy!.Requirements.OfType<RolesAuthorizationRequirement>()
            .Should().ContainSingle(r => r.AllowedRoles.Contains("real-operators"),
                "the host's own operator policy must survive the dashboard registration");

        // And it must still enforce: an authenticated user without the role is denied.
        var authz = provider.GetRequiredService<IAuthorizationService>();
        var result = await authz.AuthorizeAsync(AuthenticatedUserWithoutRoles(), "UKBatch:Operator");
        result.Succeeded.Should().BeFalse(
            "a role-gated API relying on this policy must keep rejecting non-operators");
    }

    [Fact]
    public async Task NoHostPolicies_FallbackPoliciesRenderEverything()
    {
        var services = BaseServices();
        services.AddUKBatchDashboard(ConfigureOneService);

        await using var provider = services.BuildServiceProvider();

        // The open default: with no authentication integration and no host policies, both policies
        // exist and succeed so every UI control renders.
        var authz = provider.GetRequiredService<IAuthorizationService>();
        (await authz.AuthorizeAsync(AuthenticatedUserWithoutRoles(), "UKBatch:Viewer"))
            .Succeeded.Should().BeTrue();
        (await authz.AuthorizeAsync(AuthenticatedUserWithoutRoles(), "UKBatch:Operator"))
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task HostPolicyRegisteredAfterDashboard_WinsByLastWriter()
    {
        // Configure callbacks run in registration order, so a host registering after the dashboard
        // overwrites the fallback — the other safe ordering.
        var services = BaseServices();
        services.AddUKBatchDashboard(ConfigureOneService);
        services.AddAuthorization(o =>
            o.AddPolicy("UKBatch:Operator", p => p.RequireRole("real-operators")));

        await using var provider = services.BuildServiceProvider();

        var authz = provider.GetRequiredService<IAuthorizationService>();
        var result = await authz.AuthorizeAsync(AuthenticatedUserWithoutRoles(), "UKBatch:Operator");
        result.Succeeded.Should().BeFalse("the host's later registration overwrites the fallback");
    }
}
