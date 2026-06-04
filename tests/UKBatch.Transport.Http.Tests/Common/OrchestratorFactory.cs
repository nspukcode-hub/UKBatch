using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UKBatch.Transport.Http.Tests.Common;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper for Sample.CrossServiceHttp.Orchestrator.
/// Used for the two-service integration tests (orchestrator + worker, with HMAC roundtrip across the
/// in-process bridge).
/// </summary>
public sealed class OrchestratorFactory : WebApplicationFactory<Sample.CrossServiceHttp.Orchestrator.Program>
{
    /// <summary>Optional override for the HMAC shared secret.</summary>
    public string SharedSecret { get; set; } = TestHmacHeaders.TestSecret;

    /// <summary>
    /// The base URL the orchestrator uses for the registered worker service. Defaults to a sentinel
    /// (<c>http://billing-worker.test/</c>) which is rewritten in tests via <see cref="HttpClient"/>
    /// handler injection. The orchestrator's HttpTransport then routes requests through that handler
    /// → WorkerFactory.Server.CreateHandler().
    /// </summary>
    public string WorkerBaseUrl { get; set; } = "http://billing-worker.test/";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:Http:SharedSecret"] = SharedSecret,
                ["UKBatch:Transport:Http:DefaultRequestTimeout"] = "00:00:30",
                ["UKBatch:Transport:Http:LongPollMaxWait"] = "00:00:05",
                ["UKBatch:Transport:Http:MaxClockSkew"] = "00:05:00",
                ["UKBatch:Transport:Http:Services:billing-worker:BaseUrl"] = WorkerBaseUrl,
                // dashboard self pointer (overridden if tests poke loopback)
                ["UKBatch:Dashboard:Services:0:Name"] = "self",
                ["UKBatch:Dashboard:Services:0:BaseUrl"] = "http://localhost/api/",
                ["UKBatch:Dashboard:Services:0:DisplayName"] = "Test Orchestrator",
            });
        });
    }
}
