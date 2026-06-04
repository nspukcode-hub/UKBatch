using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace UKBatch.Dashboard.Tests.Common;

/// <summary>
/// Mirror of <c>UKBatch.Api.Tests.Common.SampleRestApiFactory</c> for Dashboard.Tests. Boots
/// Sample.RestApi with short approval timeout so gates auto-resolve.
/// </summary>
public sealed class SampleRestApiFactory : WebApplicationFactory<Sample.RestApi.Program>
{
    public SampleRestApiFactory()
    {
        Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.UseEnvironment("Development");
    }
}
