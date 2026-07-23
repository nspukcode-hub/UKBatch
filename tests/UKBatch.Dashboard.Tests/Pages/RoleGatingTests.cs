using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using JobsDetail = UKBatch.Dashboard.Components.Pages.Jobs.Detail;

namespace UKBatch.Dashboard.Tests.Pages;

/// <summary>
/// Locks the operator-policy UI gating: a write control wrapped in
/// <c>AuthorizeView Policy="UKBatch:Operator"</c> shows for an operator and is hidden from a viewer.
/// Jobs/Detail's "Run Now" trigger is the representative write control.
/// </summary>
public sealed class RoleGatingTests : TestContext
{
    private const string JobName = "ProcessOrdersJob";

    private IRenderedComponent<JobsDetail> RenderJobsDetail()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetJobAsync(JobName, Arg.Any<CancellationToken>()).Returns(new JobDefinitionDto
        {
            Name = JobName,
            IsPartitioned = false,
            MaxRetries = 3,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        });
        client.QueryExecutionsAsync(Arg.Any<JobQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobExecution>
            {
                Items = Array.Empty<JobExecution>(),
                TotalCount = 0,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        return RenderComponent<JobsDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Name, JobName));
    }

    [Fact]
    public void Operator_SeesTriggerButton()
    {
        this.AddPermitAllAuth();
        var cut = RenderJobsDetail();
        cut.Markup.Should().Contain("Run Now", "an operator may trigger the job");
    }

    [Fact]
    public void Viewer_DoesNotSeeTriggerButton()
    {
        this.AddViewerOnlyAuth();
        var cut = RenderJobsDetail();
        cut.Markup.Should().NotContain("Run Now", "the trigger is operator-gated and hidden from a viewer");
    }
}
