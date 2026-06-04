using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper for integration
/// tests. Boots <c>Sample.Dashboard</c> in <c>Development</c> with a short approval timeout and
/// the in-process loopback descriptor. Tests obtain real <c>HttpClient</c>s via
/// <see cref="WebApplicationFactory{T}.CreateClient()"/>.
/// </summary>
/// <remarks>
/// <para>The <c>BaseUrl</c> for the embedded "self" descriptor is overridden to
/// <see cref="WebApplicationFactory{T}.Server"/>.<c>BaseAddress</c> (set per-test in
/// <see cref="ConfigureWebHost(IWebHostBuilder)"/>) so the dashboard talks back to the TestServer
/// instead of an unreachable real <c>localhost:5000</c>. The trailing slash is preserved per the
/// HttpClient <c>BaseAddress</c> RFC 3986 gotcha (see Sample.Dashboard/README.md).</para>
/// </remarks>
public sealed class SampleDashboardFactory : WebApplicationFactory<Sample.Dashboard.Program>
{
    /// <summary>Override-able base URL for the embedded "self" descriptor. <c>null</c> → use the TestServer address.</summary>
    public Uri? SelfBaseUrlOverride { get; set; }

    public SampleDashboardFactory()
    {
        Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");

        // Sample.Dashboard binds UKBatch:Dashboard:Services[] from configuration. Tests override the
        // descriptor BaseUrl via an in-memory configuration layer so the loopback REST + hub URLs
        // point at the WebApplicationFactory TestServer rather than a hardcoded localhost:5000.
        // SelfBaseUrlOverride lets specific tests (e.g. partial-failure) substitute an unreachable
        // URL.
        builder.ConfigureAppConfiguration((_, config) =>
        {
            var selfBaseUrl = SelfBaseUrlOverride?.ToString() ?? "http://localhost/api/";
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Dashboard:Services:0:Name"] = "self",
                ["UKBatch:Dashboard:Services:0:BaseUrl"] = selfBaseUrl,
                ["UKBatch:Dashboard:Services:0:DisplayName"] = "Local",
            });
        });
    }
}
