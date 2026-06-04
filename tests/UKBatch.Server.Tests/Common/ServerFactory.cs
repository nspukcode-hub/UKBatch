using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UKBatch.Server.Tests.Common;

/// <summary>
/// <see cref="WebApplicationFactory{TEntryPoint}"/> over <c>UKBatch.Server.Program</c> (the marker
/// <c>public partial class Program</c> at the bottom of Program.cs). Boots the server with its
/// default knobs (inmemory storage / inprocess transport / dashboard enabled) unless a test supplies
/// configuration overrides via <see cref="ConfigOverrides"/>.
/// </summary>
/// <remarks>
/// Overrides are applied through an in-memory configuration layer (highest precedence) rather than by
/// mutating the process environment — the server's Program.cs reads the flat <c>UKBATCH_*</c> keys
/// first, and an in-memory provider added last shadows them cleanly. No real RabbitMQ / Postgres is
/// stood up; every assertion here is Docker-free.
/// </remarks>
public sealed class ServerFactory : WebApplicationFactory<global::UKBatch.Server.Program>
{
    /// <summary>Optional flat/structured config overrides applied as the last (winning) provider.</summary>
    public IDictionary<string, string?>? ConfigOverrides { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");

        if (ConfigOverrides is { Count: > 0 })
        {
            // UseSetting writes host configuration, which WebApplication.CreateBuilder reads at the
            // EARLIEST stage — so a flat key like UKBATCH_ENABLE_DASHBOARD is visible to the
            // Program.cs `cfg[...]` reads that run BEFORE app.Build. (ConfigureAppConfiguration is
            // layered too late for those build-time reads under minimal hosting.)
            foreach (var (key, value) in ConfigOverrides)
            {
                builder.UseSetting(key, value);
            }
        }
    }
}
