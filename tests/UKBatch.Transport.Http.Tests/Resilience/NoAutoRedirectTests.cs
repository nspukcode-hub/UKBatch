using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UKBatch.Transport.Http;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Resilience;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Resilience;

/// <summary>
/// The named transport client MUST NOT follow HTTP redirects. A 3xx would otherwise re-send the
/// signed body and HMAC headers to the redirect target, replaying a valid signed envelope to a host
/// the caller never intended to reach. The client surfaces the raw 3xx response instead of following it.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class NoAutoRedirectTests
{
    private static IHttpClientFactory BuildFactory()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UKBatch:Transport:Http:SharedSecret"] = "TEST-SECRET-FOR-VALIDATION-FLOOR-32CH+",
                ["UKBatch:Transport:Http:DefaultRequestTimeout"] = "00:00:30",
                ["UKBatch:Transport:Http:LongPollMaxWait"] = "00:00:25",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddUKBatchHttpTransport();
        // Resolve the factory from a long-lived provider so the SocketsHttpHandler stays alive for the request.
        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>();
    }

    [Fact]
    public async Task NamedTransportClient_OnRedirect_DoesNotFollow_SurfacesRawStatus()
    {
        // Spin up a loopback server: /redirect answers 307 pointing at /target; /target answers 200.
        // If the configured client followed the redirect it would land on /target and observe 200.
        var app = WebApplication.CreateBuilder().Build();
        app.MapGet("/redirect", (HttpContext ctx) =>
        {
            ctx.Response.Headers.Location = "/target";
            return Results.StatusCode((int)HttpStatusCode.TemporaryRedirect);
        });
        app.MapGet("/target", () => Results.Ok("followed"));
        app.Urls.Add("http://127.0.0.1:0");

        await app.StartAsync();
        try
        {
            var baseAddress = app.Urls.First();
            var factory = BuildFactory();
            using var client = factory.CreateClient(PollyResilienceHandlerSetup.NamedClientPrefix);

            // Drive the request through the full production chain (HMAC signing handler →
            // primary handler), so the assertion exercises the configured primary handler's
            // AllowAutoRedirect=false rather than a bare HttpClient.
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(baseAddress), "/redirect"));
            HmacSigningHandler.AttachCanonicalPath(request, "GET\n/redirect\n");

            using var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect,
                "the transport client must surface the raw 3xx instead of replaying the signed request to the redirect target");
        }
        finally
        {
            await app.StopAsync();
            await app.DisposeAsync();
        }
    }
}
