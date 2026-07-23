using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using UKBatch.Dashboard.Components.Pages.Batches;
using Xunit;

namespace UKBatch.Dashboard.Tests.Pages;

/// <summary>
/// Locks the route-level gating of the authoring pages. Both are write surfaces (the API enforces the
/// operator policy on their submits), so their ROUTES carry the same policy: a viewer never reaches the
/// form, instead of filling it in and failing at save. Under the auth-off default the policy always
/// succeeds, so nothing changes there.
/// </summary>
public sealed class AuthoringRouteGatingTests
{
    private const string OperatorPolicy = "UKBatch:Operator";

    [Theory]
    [InlineData(typeof(Wizard))]
    [InlineData(typeof(Editor))]
    public void AuthoringPage_RequiresOperatorPolicy(Type pageType)
    {
        var authorize = pageType
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .ToList();

        authorize.Should().ContainSingle(a => a.Policy == OperatorPolicy,
            $"{pageType.Name} is a write surface and its route must be operator-gated");
    }

    [Theory]
    [InlineData(typeof(Catalog))]
    [InlineData(typeof(Detail))]
    public void ReadPage_IsNotOperatorGated(Type pageType)
    {
        // Read pages stay reachable for viewers — only authoring routes carry the operator policy.
        pageType.GetCustomAttributes(typeof(AuthorizeAttribute), inherit: true)
            .Cast<AuthorizeAttribute>()
            .Should().NotContain(a => a.Policy == OperatorPolicy,
                $"{pageType.Name} is a read surface and must stay viewer-reachable");
    }
}
