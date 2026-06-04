using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// Auth-on smoke for the dashboard. Verifies the
/// <c>MapUKBatchDashboard.RequireAuthorization()</c> seam contract:
/// <list type="bullet">
/// <item>Default v0.1: no auth scheme on the dashboard route group → anonymous accepted.</item>
/// <item>With <c>RequireAuthorization()</c> chained: anonymous → 401; authenticated → 200.</item>
/// </list>
/// </summary>
public sealed class DashboardAuthOnTests
{
    [Fact]
    public async Task Dashboard_NoAuthScheme_AnonymousAllowed_v01()
    {
        // Default-off: the Sample.Dashboard mounts the dashboard WITHOUT RequireAuthorization().
        using var factory = new DefaultFactory();
        using var client = factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "v0.1 default — dashboard is auth-agnostic; caller opt-in via RequireAuthorization");
    }

    [Fact]
    public async Task Dashboard_WithRequireAuthorization_AnonymousReturns401Or302()
    {
        // Auth-on lock: when the caller chains RequireAuthorization(), the dashboard endpoints
        // require an authenticated user. Anonymous → 401 (default DevAuth scheme) OR 302 redirect
        // (if a challenge-handler is wired). Accept either: the security contract is "anonymous
        // CANNOT reach 200 OK".
        using var factory = new AuthRequiredFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().BeOneOf(
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.Found,
            HttpStatusCode.Redirect);
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK,
            "auth-on contract — anonymous MUST NOT see the dashboard content");
    }

    [Fact]
    public async Task Dashboard_WithRequireAuthorization_AuthenticatedReturns200()
    {
        // The matching positive case: with valid DevAuth headers the dashboard returns 200.
        using var factory = new AuthRequiredFactory();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Dev-User", "alice");
        client.DefaultRequestHeaders.Add("X-Dev-Roles", "ops");
        var resp = await client.GetAsync(new Uri("/dashboard", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed class DefaultFactory : WebApplicationFactory<Sample.Dashboard.Program>
    {
        public DefaultFactory()
        {
            Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
            Environment.SetEnvironmentVariable("Sample__Dashboard__RequireAuthorization", null);
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UKBatch:Dashboard:Services:0:Name"] = "self",
                    ["UKBatch:Dashboard:Services:0:BaseUrl"] = "http://localhost/api/",
                    ["UKBatch:Dashboard:Services:0:DisplayName"] = "Local",
                });
            });
        }
    }

    private sealed class AuthRequiredFactory : WebApplicationFactory<Sample.Dashboard.Program>
    {
        public AuthRequiredFactory()
        {
            Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["UKBatch:Dashboard:Services:0:Name"] = "self",
                    ["UKBatch:Dashboard:Services:0:BaseUrl"] = "http://localhost/api/",
                    ["UKBatch:Dashboard:Services:0:DisplayName"] = "Local",
                    // The Sample.Dashboard.Program reads this flag and chains
                    // .RequireAuthorization() on the dashboard endpoints.
                    ["Sample:Dashboard:RequireAuthorization"] = "true",
                });
            });
        }
    }
}
