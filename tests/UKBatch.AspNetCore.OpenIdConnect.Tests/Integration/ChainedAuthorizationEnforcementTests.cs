using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using FluentAssertions;
using UKBatch.Api;
using Xunit;

namespace UKBatch.AspNetCore.OpenIdConnect.Tests.Integration;

/// <summary>
/// Boots the same role-gated host as <see cref="RoleGatedApiHostFixture"/> but with the group gated as
/// <c>RequireAuthorization().RequireUKBatchRoleAuthorization()</c> — a composition the library's own
/// docs suggest for hosts that want a blanket authenticated-user requirement.
/// </summary>
public sealed class ChainedRoleGatedApiHostFixture : IAsyncLifetime
{
    private IHost _host = null!;

    public async Task InitializeAsync()
    {
        var builder = new HostBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.UseEnvironment("Development");
            web.ConfigureServices(RoleGatedApiHostFixture.ConfigureServices);
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGroup("/api")
                    .MapUKBatchApi()
                    .RequireAuthorization()
                    .RequireUKBatchRoleAuthorization());
            });
        });
        _host = await builder.StartAsync();
    }

    public async Task DisposeAsync()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    public HttpClient CreateClient(string? token = null)
    {
        var client = _host.GetTestServer().CreateClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        }

        return client;
    }
}

/// <summary>
/// Enforcement proof for the chained composition: <c>RequireAuthorization()</c> stamps a default
/// authorize entry on every endpoint, and the role convention must still add its policy alongside
/// (entries combine with AND). The load-bearing case is
/// <see cref="Viewer_TriggerJob_Forbidden_UnderChainedAuthorization"/> — if the role policy were
/// skipped because authorize metadata already exists, any authenticated viewer could dispatch work.
/// </summary>
public sealed class ChainedAuthorizationEnforcementTests : IClassFixture<ChainedRoleGatedApiHostFixture>
{
    private readonly ChainedRoleGatedApiHostFixture _host;

    public ChainedAuthorizationEnforcementTests(ChainedRoleGatedApiHostFixture host) => _host = host;

    private static HttpContent EmptyJson() => new StringContent("{}", Encoding.UTF8, "application/json");

    [Fact]
    public async Task Viewer_TriggerJob_Forbidden_UnderChainedAuthorization()
    {
        var token = RoleGatedApiHostFixture.TokenWithFlatRoles("vera", RoleGatedApiHostFixture.ViewerRole);
        using var client = _host.CreateClient(token);

        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the operator policy must gate the write even when RequireAuthorization() already stamped a default entry");
    }

    [Fact]
    public async Task Operator_TriggerJob_Accepted_UnderChainedAuthorization()
    {
        var token = RoleGatedApiHostFixture.TokenWithFlatRoles("olga", RoleGatedApiHostFixture.OperatorRole);
        using var client = _host.CreateClient(token);

        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted,
            "the combined default + role requirements both hold for an operator");
    }

    [Fact]
    public async Task Viewer_GetJobs_Ok_UnderChainedAuthorization()
    {
        var token = RoleGatedApiHostFixture.TokenWithFlatRoles("vera", RoleGatedApiHostFixture.ViewerRole);
        using var client = _host.CreateClient(token);

        var resp = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, "reads stay viewer-reachable under the chain");
    }

    [Fact]
    public async Task Anonymous_TriggerJob_Unauthorized_UnderChainedAuthorization()
    {
        using var client = _host.CreateClient();

        var resp = await client.PostAsync(
            new Uri($"/api/jobs/{RoleGatedApiHostFixture.DemoJobName}/trigger", UriKind.Relative),
            EmptyJson());

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
