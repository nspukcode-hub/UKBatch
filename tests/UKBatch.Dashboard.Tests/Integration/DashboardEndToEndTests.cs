using System.Net;
using FluentAssertions;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// End-to-end smoke tests for the Blazor Server dashboard mounted in
/// Sample.Dashboard (embedded mode). Each test pulls the prerendered HTML markup over the
/// TestServer and asserts a marker substring rendered by the corresponding page component.
/// </summary>
/// <remarks>
/// <para>The dashboard renders server-side (prerender pass) before the SignalR circuit handshake.
/// We assert only on the prerender markup which is sufficient to catch routing / DI / page
/// composition regressions. Hub reconnect behaviour is covered by
/// <see cref="HubReconnectIntegrationTests"/>.</para>
/// </remarks>
public sealed class DashboardEndToEndTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public DashboardEndToEndTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Dashboard_LandingPage_RendersServiceHealth()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // Landing page renders the page title and the service grid container even if the embedded
        // conductor cannot reach itself during prerender (the card renders with the
        // disconnected modifier, exercising the partial-failure path).
        html.Should().Contain("Services", "Landing page header marker");
        html.Should().Contain("service-grid", "service grid container marker");
        html.Should().Contain("dashboard-sidebar", "shared layout sidebar marker");
    }

    [Fact]
    public async Task Dashboard_JobsCatalogPage_Returns200()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard/self/jobs", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // Jobs catalog page marker — sidebar + content shell render even if data load fails.
        html.Should().Contain("dashboard-content", "dashboard content marker");
    }

    [Fact]
    public async Task Dashboard_AllRoutes_Return200()
    {
        // Quick smoke each of the 10 known dashboard routes returns 200 — protects against
        // accidental @page directive regressions or missing components after refactors.
        var routes = new[]
        {
            "/dashboard",
            "/dashboard/self/jobs",
            "/dashboard/self/batches",
            "/dashboard/self/executions",
            "/dashboard/self/approvals",
            "/dashboard/settings",
        };
        using var client = _factory.CreateClient();
        foreach (var route in routes)
        {
            var resp = await client.GetAsync(new Uri(route, UriKind.Relative));
            resp.StatusCode.Should().Be(HttpStatusCode.OK, $"route {route} should be navigable");
        }
    }

    [Fact]
    public async Task Dashboard_RestApi_JobsEndpoint_Works()
    {
        // Sanity check: the embedded API surface is reachable through the same TestServer that
        // hosts the dashboard. This guards against routing regressions in MapUKBatchApi /
        // MapUKBatchDashboard mounting both surfaces on one host.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?offset=0&limit=10", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadAsStringAsync();
        payload.Should().Contain("InvoiceGenerationJob");
        payload.Should().Contain("totalCount");
    }
}
