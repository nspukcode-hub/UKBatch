using Bunit;
using Bunit.TestDoubles;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Executions;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class ExecutionsQueryTests : TestContext
{
    private static PageEnvelope<JobExecution> EmptyEnvelope() => new()
    {
        Items = Array.Empty<JobExecution>(),
        TotalCount = 0,
        Offset = 0,
        Limit = 50,
    };

    private IUKBatchClient Wire(string svcName)
    {
        var svc = PageTestHelpers.Descriptor(svcName);
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.QueryExecutionsAsync(Arg.Any<JobQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(EmptyEnvelope());

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        return client;
    }

    [Fact]
    public void Init_WithBatchIdQuery_PreFillsFilter_AndFirstQueryCarriesBatchId()
    {
        const string svcName = "svc";
        const string batchId = "br-deep-link";
        var client = Wire(svcName);

        // A deep link from Batches/RunDetail navigates here with ?batchId=... The query parameter must be
        // applied BEFORE the first query, so the very first QueryExecutionsAsync is already scoped to it.
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo($"/dashboard/{svcName}/executions?batchId={Uri.EscapeDataString(batchId)}");

        var cut = RenderComponent<Query>(p => p.Add(d => d.ServiceName, svcName));

        // The filter input is pre-filled with the supplied batch run id.
        cut.WaitForAssertion(() =>
            cut.Find("#batch-id").GetAttribute("value").Should().Be(batchId));

        // The FIRST (and so far only) query carried BatchId == the supplied value.
        client.Received().QueryExecutionsAsync(
            Arg.Is<JobQueryRequest>(q => q.BatchId == batchId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Init_WithoutBatchIdQuery_FilterEmpty_FirstQueryHasNoBatchId()
    {
        const string svcName = "svc";
        var client = Wire(svcName);

        var cut = RenderComponent<Query>(p => p.Add(d => d.ServiceName, svcName));

        cut.WaitForAssertion(() =>
            client.Received().QueryExecutionsAsync(
                Arg.Is<JobQueryRequest>(q => q.BatchId == null),
                Arg.Any<CancellationToken>()));
        cut.Find("#batch-id").GetAttribute("value").Should().BeNullOrEmpty();
    }

    [Fact]
    public void Init_WithJobNameQuery_PreFillsFilter_AndFirstQueryCarriesJobName()
    {
        const string svcName = "svc";
        const string jobName = "ProcessOrdersJob";
        var client = Wire(svcName);

        // A deep link from Jobs/Detail navigates here with ?jobName=... The query parameter must be
        // applied BEFORE the first query, so the very first QueryExecutionsAsync is already scoped to it.
        var nav = Services.GetRequiredService<FakeNavigationManager>();
        nav.NavigateTo($"/dashboard/{svcName}/executions?jobName={Uri.EscapeDataString(jobName)}");

        var cut = RenderComponent<Query>(p => p.Add(d => d.ServiceName, svcName));

        // The filter input is pre-filled with the supplied job name.
        cut.WaitForAssertion(() =>
            cut.Find("#job-name").GetAttribute("value").Should().Be(jobName));

        // The FIRST (and so far only) query carried JobName == the supplied value.
        client.Received().QueryExecutionsAsync(
            Arg.Is<JobQueryRequest>(q => q.JobName == jobName),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Init_WithoutJobNameQuery_FilterEmpty_FirstQueryHasNoJobName()
    {
        const string svcName = "svc";
        var client = Wire(svcName);

        var cut = RenderComponent<Query>(p => p.Add(d => d.ServiceName, svcName));

        cut.WaitForAssertion(() =>
            client.Received().QueryExecutionsAsync(
                Arg.Is<JobQueryRequest>(q => q.JobName == null),
                Arg.Any<CancellationToken>()));
        cut.Find("#job-name").GetAttribute("value").Should().BeNullOrEmpty();
    }
}
