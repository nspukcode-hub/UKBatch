using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace UKBatch.Transport.Http.Tests.Common;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper for Sample.CrossServiceHttp.Worker.
/// Boots the worker in Development with the test HMAC shared secret. Tests obtain real
/// <see cref="HttpClient"/>s via <see cref="WebApplicationFactory{T}.CreateClient()"/> or the
/// in-process <c>Server.CreateHandler()</c> bridge for orchestrator-side WAF cross-wiring.
/// </summary>
public sealed class WorkerFactory : WebApplicationFactory<Sample.CrossServiceHttp.Worker.Program>
{
    /// <summary>Optional override for the HMAC shared secret. Default is <see cref="TestHmacHeaders.TestSecret"/>.</summary>
    public string SharedSecret { get; set; } = TestHmacHeaders.TestSecret;

    /// <summary>
    /// Optional override for the receiver's <c>MaxClockSkew</c>. Default 5 minutes (5:00).
    /// Clock-skew tests narrow this so tampered timestamps surface deterministically.
    /// </summary>
    public TimeSpan MaxClockSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Optional override for <c>MaxBodyBytes</c>. Default 1 MB.</summary>
    public int MaxBodyBytes { get; set; } = 1_048_576;

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
                ["UKBatch:Transport:Http:LongPollMaxWait"] = "00:00:05", // shorter for tests
                ["UKBatch:Transport:Http:MaxClockSkew"] = MaxClockSkew.ToString(),
                ["UKBatch:Transport:Http:MaxBodyBytes"] = MaxBodyBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });
        });
    }
}
