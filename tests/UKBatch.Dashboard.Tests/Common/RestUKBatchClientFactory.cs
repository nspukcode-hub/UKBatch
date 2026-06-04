using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;

namespace UKBatch.Dashboard.Tests.Common;

/// <summary>
/// Builds <see cref="RestUKBatchClient"/> instances backed by a WAF-hosted Sample.RestApi.
/// </summary>
/// <remarks>
/// <para>The default <see cref="RestUKBatchClient"/> constructor builds its own
/// <see cref="HubConnection"/> via <c>HubConnectionBuilder.WithUrl(string, ...)</c> which tries
/// to open a real socket against <c>localhost:0</c> — this does NOT bridge to the in-memory
/// <c>TestServer</c>. The hub tests build <see cref="HubConnection"/> directly
/// with <c>HttpMessageHandlerFactory = _factory.Server.CreateHandler()</c> + LongPolling.</para>
/// <para>The hub tests need that same bridge. Since <see cref="RestUKBatchClient"/>'s
/// hub construction is encapsulated (private constructor field), the cleanest seam is to use
/// an HTTP <see cref="HttpClient"/> from <see cref="WebApplicationFactory{T}"/> for REST AND a
/// separately constructed <see cref="HubConnection"/> for hub-direct tests. This factory
/// returns both so each test class chooses what it needs.</para>
/// </remarks>
internal static class RestUKBatchClientFactory
{
    /// <summary>Builds a <see cref="RestUKBatchClient"/> with the WAF's HttpClient (REST-only paths).</summary>
    public static RestUKBatchClient BuildRestOnly(SampleRestApiFactory factory, string serviceName = "self")
    {
        ArgumentNullException.ThrowIfNull(factory);
        var http = factory.CreateClient();
        http.BaseAddress = new Uri(factory.Server.BaseAddress, "/api/");
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = serviceName,
            BaseUrl = http.BaseAddress,
        };
        var opts = Options.Create(new DashboardOptions
        {
            DedupeCacheCapacity = 32,
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        });
        return new RestUKBatchClient(descriptor, http, NullLogger<RestUKBatchClient>.Instance, opts);
    }

    /// <summary>
    /// Builds a raw <see cref="HubConnection"/> bridged to the WAF TestServer via LongPolling.
    /// Use for hub-only assertions (RestUKBatchClient instance still
    /// available via BuildRestOnly for REST methods on the same factory).
    /// </summary>
    public static HubConnection BuildHubConnection(SampleRestApiFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        var hubUri = new Uri(factory.Server.BaseAddress, "/api/hubs/jobs");
        return new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                opt.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }
}
