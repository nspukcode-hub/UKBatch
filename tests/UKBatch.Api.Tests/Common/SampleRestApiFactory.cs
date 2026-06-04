using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UKBatch.Api.Tests.Common;

/// <summary>
/// Shared <see cref="WebApplicationFactory{TEntryPoint}"/> wrapper for the tests.
/// Boots <c>Sample.RestApi</c> with deterministic configuration (short approval timeout so
/// gates auto-resolve without blocking tests).
/// </summary>
public sealed class SampleRestApiFactory : WebApplicationFactory<Sample.RestApi.Program>
{
    public SampleRestApiFactory()
    {
        // Sample.RestApi reads this BEFORE the WebApplicationFactory ConfigureWebHost runs.
        Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
    }
}
