using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Approvals;
using UKBatch.Dashboard.Components.Pages.Approvals;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class ApprovalsQueueTests : TestContext
{
    public ApprovalsQueueTests() => this.AddPermitAllAuth();

    private static readonly string[] OpsRole = ["ops"];

    private static PendingApprovalDto Approval(string id, string batchId, string title) => new()
    {
        ApprovalId = id,
        BatchId = batchId,
        BatchStepId = "step-1",
        BatchName = "demo-batch",
        Config = new ApprovalGateConfig
        {
            Title = title,
            AllowedRoles = OpsRole,
            OnTimeout = ApprovalTimeoutAction.Fail,
        },
        PendingSinceUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Render_EmptyApprovals_ShowsOperatorWarningAndCheerfulEmpty()
    {
        // OPERATOR WARNING — visible whenever zero approvals returned (claim mismatch heuristic).
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingApprovalDto>());

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Queue>(p => p.Add(q => q.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No approvals"));
        cut.Markup.Should().Contain("claim");
        cut.Markup.Should().Contain("ApprovalRoleClaimTypes");
    }

    [Fact]
    public void Render_WithApprovals_ShowsRows()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new[] { Approval("ap-1", "br-1", "Confirm deploy") });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Queue>(p => p.Add(q => q.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Confirm deploy"));
        cut.Markup.Should().Contain("Approve");
        cut.Markup.Should().Contain("Reject");
    }

    [Fact]
    public async Task Init_SubscribesAllForLiveApprovals()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<PendingApprovalDto>());

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Queue>(p => p.Add(q => q.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().NotBeNullOrEmpty());
        await client.Received(1).SubscribeAllAsync(Arg.Any<CancellationToken>());
    }
}
