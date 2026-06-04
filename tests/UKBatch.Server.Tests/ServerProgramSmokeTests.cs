using System.Net;
using FluentAssertions;
using UKBatch.Server.Tests.Common;
using Xunit;

namespace UKBatch.Server.Tests;

/// <summary>
/// <c>UKBatch.Server</c> Program.cs boot smoke over a WAF. With defaults
/// (inmemory / inprocess / dashboard=true): <c>/healthz</c>, <c>GET /api/workers</c>, <c>/dashboard</c>,
/// and the OpenAPI document all serve. With <c>UKBATCH_ENABLE_DASHBOARD=false</c>: <c>/dashboard</c>
/// is 404 but the REST surface is unaffected.
/// </summary>
public sealed class ServerProgramSmokeTests
{
    [Fact]
    public async Task Healthz_DefaultConfig_Returns200()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/healthz", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MapHealthChecks(\"/healthz\") is wired");
    }

    [Fact]
    public async Task GetWorkers_DefaultConfig_Returns200EmptyArray()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/api/workers", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Trim().Should().Be("[]", "no workers have beaten yet → an empty JSON array");
    }

    [Fact]
    public async Task Dashboard_DefaultConfig_Returns200()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the dashboard is mounted by default (UKBATCH_ENABLE_DASHBOARD defaults true); " +
            "a 500 here would indicate the UseAntiforgery gotcha regressed");
    }

    [Fact]
    public async Task OpenApiDocument_DefaultConfig_Returns200()
    {
        using var factory = new ServerFactory();
        using var client = factory.CreateClient();

        var resp = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "MapOpenApi() exposes the OpenAPI document");
    }

    [Fact]
    public async Task Dashboard_DashboardDisabled_Returns404_ButWorkersStill200()
    {
        using var factory = new ServerFactory
        {
            ConfigOverrides = new Dictionary<string, string?>
            {
                ["UKBATCH_ENABLE_DASHBOARD"] = "false",
            },
        };
        using var client = factory.CreateClient();

        var dashboard = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        dashboard.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "with the dashboard disabled, MapUKBatchDashboard is not called → /dashboard is unmapped");

        var workers = await client.GetAsync(new Uri("/api/workers", UriKind.Relative));
        workers.StatusCode.Should().Be(HttpStatusCode.OK,
            "disabling the dashboard must NOT affect the REST API surface");
    }
}
