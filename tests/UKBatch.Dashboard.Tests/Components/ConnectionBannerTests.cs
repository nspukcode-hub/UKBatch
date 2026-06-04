using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Shared;
using UKBatch.Dashboard.Configuration;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Components;

public sealed class ConnectionBannerTests : TestContext
{
    private static UKBatchServiceDescriptor SvcWithName(string name) => new()
    {
        Name = name,
        BaseUrl = new Uri($"http://{name}.local:5000/api/"),
        DisplayName = name,
    };

    [Fact]
    public void Connected_RendersNothing()
    {
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.Connected);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);

        var cut = RenderComponent<ConnectionBanner>(p => p.Add(b => b.CurrentService, SvcWithName("a")));

        cut.Markup.Trim().Should().BeEmpty();
    }

    [Fact]
    public void Reconnecting_RendersReconnectingBanner()
    {
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.Reconnecting);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);

        var cut = RenderComponent<ConnectionBanner>(p => p.Add(b => b.CurrentService, SvcWithName("billing")));

        cut.Markup.Should().Contain("dashboard-banner--reconnecting");
        cut.Markup.Should().Contain("Reconnecting to");
        cut.Markup.Should().Contain("billing");
    }

    [Fact]
    public void PartiallyConnected_RendersWarningWithRetry()
    {
        // PartiallyConnected amber + Retry button.
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.PartiallyConnected);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);

        var cut = RenderComponent<ConnectionBanner>(p => p.Add(b => b.CurrentService, SvcWithName("orders")));

        cut.Markup.Should().Contain("dashboard-banner--warning");
        cut.Markup.Should().Contain("degraded");
        cut.Find("button.btn--inline").TextContent.Should().Contain("Retry");
    }

    [Fact]
    public async Task PartialRetry_TriggersCleanDisconnectThenConnectCycle()
    {
        // clean DisconnectAsync → ConnectAsync cycle.
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.PartiallyConnected);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);

        var cut = RenderComponent<ConnectionBanner>(p => p.Add(b => b.CurrentService, SvcWithName("orders")));
        await cut.Find("button.btn--inline").ClickAsync(new());

        await client.Received(1).DisconnectAsync(Arg.Any<CancellationToken>());
        await client.Received(1).ConnectAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Disconnected_RendersErrorWithConnectButton()
    {
        var client = Substitute.For<IUKBatchClient>();
        client.State.Returns(UKBatchClientState.Disconnected);
        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient(Arg.Any<string>()).Returns(client);
        Services.AddSingleton(factory);

        var cut = RenderComponent<ConnectionBanner>(p => p.Add(b => b.CurrentService, SvcWithName("a")));

        cut.Markup.Should().Contain("dashboard-banner--error");
        cut.Find("button.btn--inline").TextContent.Should().Contain("Connect");
    }
}
