using System.Net;
using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// locks down the <c>RequiresAspNetWebAssets</c> MSBuild prop invariant.
/// If anyone removes <c>&lt;RequiresAspNetWebAssets&gt;true&lt;/RequiresAspNetWebAssets&gt;</c>
/// from the host csproj, <c>app.MapStaticAssets</c> from the pipeline, or alters
/// <c>App.razor</c>'s script tag, these tests fail at CI before the bug reaches the operator.
/// </summary>
/// <remarks>
/// <para>The bug surface is empirical: when the Web SDK does NOT detect any <c>.razor</c> file
/// in the host project (Microsoft.NET.Sdk.Web.ProjectSystem.targets:32), Razor Components
/// framework assets (<c>blazor.web.js</c>, <c>blazor.server.js</c>, fingerprinted variants) are
/// silently dropped from the static-web-assets manifest. bunit + WebApplicationFactory mocks
/// did NOT catch this — only browser DevTools surfaced the 404. These tests reproduce the
/// observable symptom over the live <see cref="SampleDashboardFactory"/> TestServer pipeline.</para>
/// <para>The SUT host is <c>Sample.Dashboard</c>; its csproj sets the prop explicitly (project
/// reference path) and <c>Program.cs</c> calls <c>app.MapStaticAssets</c>. This guard is an
/// INTEGRATION invariant (host-side wiring), not a library DEFAULT — PackageReference consumers
/// receive the prop automatically via the bundled <c>build/UKBatch.Dashboard.props</c>.</para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class FrameworkAssetRegressionTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public FrameworkAssetRegressionTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task BlazorWebFrameworkJs_IsServedWithExpectedSize()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/_framework/blazor.web.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "blazor.web.js MUST be in the static-web-assets manifest — see README 'Host project setup'");
        var body = await resp.Content.ReadAsByteArrayAsync();
        body.Length.Should().BeGreaterThan(100_000,
            "blazor.web.js is ~200KB; a smaller payload indicates a different asset is being served");
    }

    [Fact]
    public async Task BlazorServerFrameworkJs_IsServedWithExpectedSize()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/_framework/blazor.server.js", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsByteArrayAsync();
        body.Length.Should().BeGreaterThan(80_000,
            "blazor.server.js is ~150KB; a smaller payload indicates a different asset is being served");
    }

    [Fact]
    public async Task BlazorInitializers_ReturnsJsonAt200()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/_blazor/initializers", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "/_blazor/initializers is part of the Blazor framework endpoint set added by AddInteractiveServerRenderMode");
        resp.Content.Headers.ContentType?.MediaType
            .Should().Be("application/json", "framework initializers respond with JSON");
    }

    [Fact]
    public async Task DashboardLanding_HtmlContainsBlazorWebJsScriptTag()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("_framework/blazor.web.js",
            "App.razor emits the blazor.web.js script tag — its absence means the manifest is broken or App.razor was altered");
    }
}
