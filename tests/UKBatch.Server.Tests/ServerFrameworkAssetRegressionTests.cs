using System.Net;
using FluentAssertions;
using UKBatch.Server.Tests.Common;
using Xunit;

namespace UKBatch.Server.Tests;

/// <summary>
/// The lesson applied to the SERVER host. Over the
/// <c>UKBatch.Server</c> WAF, the dashboard's Blazor framework assets MUST serve. The server owns no
/// <c>.razor</c> file (it references <c>UKBatch.Dashboard</c> by type), so without
/// <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c> in
/// <c>UKBatch.Server.csproj</c> the Web SDK would silently drop <c>blazor.web.js</c> from the
/// static-web-assets manifest and operators would see a blank dashboard. bunit + WAF page tests miss
/// this — only fetching the asset over the live pipeline catches it. This is the equivalent of
/// <c>FrameworkAssetRegressionTests</c>.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ServerFrameworkAssetRegressionTests
{
    [Fact]
    public async Task BlazorWebFrameworkJs_ServedUnderServerHost_200WithNonTrivialLength()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/_framework/blazor.web.js", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
 "blazor.web.js MUST be in the static-web-assets manifest under the Server host" +
            "a 404 means UKBatch.Server.csproj lost <RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>");
        var body = await resp.Content.ReadAsByteArrayAsync();
        body.Length.Should().BeGreaterThan(100_000,
            "blazor.web.js is ~200KB; a smaller payload indicates a different asset is being served");
    }

    [Fact]
    public async Task DashboardLanding_ServedUnderServerHost_ContainsBlazorWebJsScriptTag()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("_framework/blazor.web.js",
            "App.razor emits the blazor.web.js script tag under the Server host — its absence means the manifest is broken");
    }
}
