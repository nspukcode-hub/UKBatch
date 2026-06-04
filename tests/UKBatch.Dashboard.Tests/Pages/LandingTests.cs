using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages;
using UKBatch.Dashboard.State;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class LandingTests : TestContext
{
    private static PageEnvelope<T> Empty<T>() => new()
    {
        Items = Array.Empty<T>(),
        TotalCount = 0,
        Offset = 0,
        Limit = 0,
    };

    private static PageEnvelope<T> WithTotal<T>(long total) => new()
    {
        Items = Array.Empty<T>(),
        TotalCount = total,
        Offset = 0,
        Limit = 0,
    };

    [Fact]
    public void Render_SingleServiceConnected_ShowsCountsAndHealthDot()
    {
        var svc = PageTestHelpers.Descriptor("billing");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient(UKBatchClientState.Connected);
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(WithTotal<JobDefinitionDto>(7));
        client.ListBatchesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<Abstractions.Batches.BatchSource?>(), Arg.Any<CancellationToken>())
            .Returns(WithTotal<BatchDefinitionDto>(2));
        client.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingApprovalDto>());

        var factory = PageTestHelpers.FactoryFor(svc.Name, client);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Landing>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("billing"));
        cut.Markup.Should().Contain("7");
        cut.Markup.Should().Contain("2");
        cut.Markup.Should().Contain("service-health-dot--connected");
    }

    [Fact]
    public void Render_ServiceLoadError_DegradesCardWithRetry()
    {
        var svc = PageTestHelpers.Descriptor("orders");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient(UKBatchClientState.Disconnected);
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PageEnvelope<JobDefinitionDto>>>(_ => throw new UKBatchClientException("Connection refused", System.Net.HttpStatusCode.ServiceUnavailable));

        var factory = PageTestHelpers.FactoryFor(svc.Name, client);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Landing>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Connection refused"));
        cut.Markup.Should().Contain("Retry");
    }

    [Fact]
    public void Render_MultipleServices_FansInSequentiallyAndAllRender()
    {
        // invariant: ONE failure does not blank the page.
        var svcA = PageTestHelpers.Descriptor("a");
        var svcB = PageTestHelpers.Descriptor("b");
        var registry = PageTestHelpers.RegistryWith(svcA, svcB);

        var aClient = PageTestHelpers.BuildClient(UKBatchClientState.Connected);
        aClient.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(WithTotal<JobDefinitionDto>(3));
        aClient.ListBatchesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string?>(),
            Arg.Any<Abstractions.Batches.BatchSource?>(), Arg.Any<CancellationToken>())
            .Returns(WithTotal<BatchDefinitionDto>(1));
        aClient.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingApprovalDto>());

        var bClient = PageTestHelpers.BuildClient(UKBatchClientState.Disconnected);
        bClient.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns<Task<PageEnvelope<JobDefinitionDto>>>(_ => throw new InvalidOperationException("boom"));

        var factory = Substitute.For<IUKBatchClientFactory>();
        factory.GetClient("a").Returns(aClient);
        factory.GetClient("b").Returns(bClient);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Landing>();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("a"));
        cut.Markup.Should().Contain("b");
        // Both services rendered — degraded card for b, healthy for a.
        cut.Markup.Should().Contain("service-card--disconnected");
    }
}
