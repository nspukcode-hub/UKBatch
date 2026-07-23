using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Unit;

/// <summary>
/// Evaluates the REAL registered "UKBatch:Viewer" / "UKBatch:Operator" policies through
/// <see cref="IAuthorizationService"/>. The load-bearing cases lock the viewer-role contract:
/// with <c>ViewerRoles</c> configured, read access narrows to those roles (operators always read);
/// with none configured, any authenticated user reads — exactly as the option documents.
/// </summary>
public sealed class ViewerRolePolicyTests
{
    private const string ViewerPolicy = "UKBatch:Viewer";
    private const string OperatorPolicy = "UKBatch:Operator";

    private static ServiceProvider BuildProvider(List<string> viewerRoles)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddUKBatchOpenIdConnect(o =>
        {
            o.Authority = "https://idp.example/realms/demo";
            o.ClientId = "dashboard";
            o.OperatorRoles = new List<string> { "batch-operator" };
            o.ViewerRoles = viewerRoles;
        });
        return services.BuildServiceProvider();
    }

    private static ClaimsPrincipal UserWithRoles(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "user-1") };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    [Fact]
    public async Task ViewerRolesConfigured_UserWithViewerRole_Reads()
    {
        await using var provider = BuildProvider(new List<string> { "batch-viewer" });
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(UserWithRoles("batch-viewer"), ViewerPolicy);

        result.Succeeded.Should().BeTrue("the user holds a configured viewer role");
    }

    [Fact]
    public async Task ViewerRolesConfigured_AuthenticatedUserWithoutViewerRole_IsDenied()
    {
        // The regression this pins: ViewerRoles used to be documented but never enforced, so any
        // authenticated user could read regardless of configuration.
        await using var provider = BuildProvider(new List<string> { "batch-viewer" });
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(UserWithRoles("unrelated-role"), ViewerPolicy);

        result.Succeeded.Should().BeFalse("with viewer roles configured, read access narrows to them");
    }

    [Fact]
    public async Task ViewerRolesConfigured_OperatorWithoutViewerRole_StillReads()
    {
        await using var provider = BuildProvider(new List<string> { "batch-viewer" });
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(UserWithRoles("batch-operator"), ViewerPolicy);

        result.Succeeded.Should().BeTrue("operators are always viewers");
    }

    [Fact]
    public async Task ViewerRolesEmpty_AnyAuthenticatedUser_Reads()
    {
        await using var provider = BuildProvider(new List<string>());
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(UserWithRoles(), ViewerPolicy);

        result.Succeeded.Should().BeTrue("with no viewer roles configured, any authenticated user is a viewer");
    }

    [Fact]
    public async Task ViewerRolesEmpty_AnonymousUser_IsDenied()
    {
        await using var provider = BuildProvider(new List<string>());
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(Anonymous(), ViewerPolicy);

        result.Succeeded.Should().BeFalse("the viewer policy always requires an authenticated user");
    }

    [Fact]
    public async Task OperatorPolicy_ViewerRoleOnly_IsDenied()
    {
        await using var provider = BuildProvider(new List<string> { "batch-viewer" });
        var authz = provider.GetRequiredService<IAuthorizationService>();

        var result = await authz.AuthorizeAsync(UserWithRoles("batch-viewer"), OperatorPolicy);

        result.Succeeded.Should().BeFalse("a viewer role never satisfies the operator policy");
    }
}
