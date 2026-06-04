using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Configuration;
using Xunit;

namespace UKBatch.Dashboard.Tests.Integration;

/// <summary>
/// End-to-end hub reconnect contract on the embedded Sample.Dashboard host.
/// Complements the unit-level tests in <c>RestUKBatchClientReconnectTests</c> by routing a
/// <see cref="RestUKBatchClient"/> through the real Sample.Dashboard hub mount (the <c>/api/hubs/jobs</c>
/// endpoint exposed by the same embedded-mode process the dashboard pages talk to).
/// </summary>
public sealed class HubReconnectIntegrationTests : IClassFixture<SampleDashboardFactory>
{
    private readonly SampleDashboardFactory _factory;

    public HubReconnectIntegrationTests(SampleDashboardFactory factory)
    {
        _factory = factory;
    }

    private RestUKBatchClient BuildBridgedClient()
    {
        var http = _factory.CreateClient();
        http.BaseAddress = new Uri(_factory.Server.BaseAddress, "/api/");
        var descriptor = new UKBatchServiceDescriptor
        {
            Name = "self",
            BaseUrl = http.BaseAddress,
        };
        var opts = Options.Create(new DashboardOptions
        {
            DedupeCacheCapacity = 16,
            ReconnectDelays = [TimeSpan.FromMilliseconds(50)],
        });
        var hubUri = new Uri(_factory.Server.BaseAddress, "/api/hubs/jobs");
        var hub = new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                opt.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .WithAutomaticReconnect([TimeSpan.FromMilliseconds(50)])
            .Build();
        return new RestUKBatchClient(descriptor, http, NullLogger<RestUKBatchClient>.Instance, opts, hub);
    }

    [Fact]
    public async Task SampleDashboardHub_HappyPath_AllSubscribesResolveOnReconnect()
    {
        // Happy-path inverse of RestUKBatchClientReconnectTests.Reconnect_GroupResubscribeFails_*:
        // when the dashboard's bridged hub bounces and ALL re-subscribes succeed, the client stays
        // in Connected (NOT triggered).
        await using var client = BuildBridgedClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(cts.Token);
        client.State.Should().Be(UKBatchClientState.Connected);

        await client.SubscribeAllAsync(cts.Token);
        client.ActiveGroupsSnapshot.Should().Contain("all");

        // Simulate reconnect via the internal handler — same path AutoReconnect would invoke.
        await client.InvokeReconnectedForTestAsync("connection-2");
        await Task.Delay(200, cts.Token);

        client.State.Should().Be(UKBatchClientState.Connected,
            "Sample.Dashboard hub is healthy → all re-subscribes succeed → state stays Connected.");
    }

    [Fact]
    public async Task SampleDashboardHub_PartialFailure_TransitionsToPartiallyConnected()
    {
        // The integration complement of the unit test: track an invalid group then
        // trigger reconnect. The hub is still up (so the connection itself transitions to
        // Connected), but the bad group's re-subscribe call throws → PartiallyConnected.
        await using var client = BuildBridgedClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.ConnectAsync(cts.Token);

        // Empty execution id → server-side ArgumentException on SubscribeToExecution.
        client.TrackGroupForTest("exec:");

        await client.InvokeReconnectedForTestAsync("connection-2");
        await Task.Delay(300, cts.Token);

        client.State.Should().Be(UKBatchClientState.PartiallyConnected,
 " invariant on the real Sample.Dashboard hub: bad re-subscribe ⇒ PartiallyConnected.");
    }
}
