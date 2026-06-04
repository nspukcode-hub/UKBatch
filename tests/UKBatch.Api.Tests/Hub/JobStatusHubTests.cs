using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Hub;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Hub;

/// <summary>
/// SignalR hub tests.
/// gotcha applied: <see cref="HubConnectionBuilder"/> against
/// <see cref="WebApplicationFactory{TEntryPoint}"/> needs <c>HttpMessageHandlerFactory</c>
/// + <c>LongPolling</c> transport, otherwise the negotiate handshake hangs on
/// <c>localhost:0</c> trying to open a real socket.
/// </summary>
public sealed class JobStatusHubTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public JobStatusHubTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private HubConnection BuildHubConnection()
    {
        var baseUri = _factory.Server.BaseAddress;
        var hubUri = new Uri(baseUri, "/api/hubs/jobs");
        var connection = new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                // bridge SignalR's HttpClient to the in-memory TestServer.
                opt.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();
        return connection;
    }

    [Fact]
    public async Task Hub_Connect_AndSubscribeAll_Succeeds()
    {
        await using var connection = BuildHubConnection();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await connection.StartAsync(cts.Token);
        connection.State.Should().Be(HubConnectionState.Connected);
        await connection.InvokeAsync("SubscribeAll", cts.Token);
        await connection.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Hub_SubscribeAll_ReceivesExecutionStateChanged_AfterTrigger()
    {
        await using var connection = BuildHubConnection();
        var tcs = new TaskCompletionSource<JobExecution>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<JobExecution>(nameof(IJobStatusHubClient.ExecutionStateChanged), exec =>
        {
            tcs.TrySetResult(exec);
        });
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        // Trigger a job over REST.
        using var client = _factory.CreateClient();
        var trigger = await client.PostAsync(
            new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var winner = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(15), cts.Token));
        if (winner != tcs.Task)
        {
            throw new TimeoutException("Did NOT receive ExecutionStateChanged within 15s.");
        }
        var snapshot = await tcs.Task;
        snapshot.JobName.Should().Contain("InvoiceGenerationJob");

        await connection.StopAsync(cts.Token);
    }

    [Fact]
    public void IJobStatusHubClient_DoesNotExpose_HubBackpressureWarning()
    {
        // / deferral lock — repeat the diagnostic at the hub level too.
        typeof(IJobStatusHubClient).GetMethods().Should().NotContain(m => m.Name == "HubBackpressureWarning");
    }
}
