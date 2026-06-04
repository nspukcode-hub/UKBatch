using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using NSubstitute;
using UKBatch.Abstractions.Workers;
using UKBatch.Api.Common;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Jobs;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class JobsCatalogTests : TestContext
{
    private static readonly string[] ProdTag = ["prod"];
    private static readonly string[] BillingTag = ["billing"];

    private static PageEnvelope<JobDefinitionDto> EmptyJobs() => new()
    {
        Items = Array.Empty<JobDefinitionDto>(),
        TotalCount = 0,
        Offset = 0,
        Limit = 50,
    };

    private static JobDefinitionDto Job(string name, bool partitioned = false, IReadOnlyList<string>? tags = null) => new()
    {
        Name = name,
        IsPartitioned = partitioned,
        MaxRetries = 3,
        TimeoutSeconds = 300,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = tags ?? Array.Empty<string>(),
    };

    [Fact]
    public void Render_EmptyJobs_ShowsEmptyState()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items = Array.Empty<JobDefinitionDto>(),
                TotalCount = 0,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No jobs registered"));
    }

    [Fact]
    public void Render_WithJobs_ShowsTableRows()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items = new[]
                {
                    new JobDefinitionDto
                    {
                        Name = "process-orders",
                        IsPartitioned = true,
                        MaxRetries = 3,
                        TimeoutSeconds = 300,
                        DefaultParameters = new Dictionary<string, object?>(),
                        Tags = ProdTag,
                    },
                },
                TotalCount = 1,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("process-orders"));
        cut.Markup.Should().Contain("PARTITIONED");
    }

    // ── worker-advertised jobs section ──────────────────────────────────────────────

    [Fact]
    public void Render_WorkerJobs_ShowsNamesWorkersAndTags_NoDetailLink()
    {
        var svc = PageTestHelpers.Descriptor("orchestrator");
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(EmptyJobs());
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new()
            {
                Name = "billing-worker",
                Jobs = ["GenerateInvoice"],
                Tags = BillingTag,
                Status = WorkerStatus.Online,
                LastSeenUtc = DateTimeOffset.UtcNow,
                Online = true,
            },
        });

        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Worker-advertised jobs",
            "the worker section header renders when a worker advertises a job"));
        cut.Markup.Should().Contain("GenerateInvoice", "the worker job NAME renders");
        cut.Markup.Should().Contain("billing-worker", "the advertising WORKER name renders");
        cut.Markup.Should().Contain("billing", "the worker's TAGS render on the row");

        // The job definition lives on the worker, NOT this server → no Detail link on the worker-job name.
        cut.Markup.Should().NotContain("/jobs/GenerateInvoice",
            "a worker-advertised job has no Detail link (the definition is not registered on this server)");
        cut.FindAll("a").Should().NotContain(a => a.TextContent.Contains("GenerateInvoice"),
            "the worker-job name is plain text, not an anchor");
    }

    [Fact]
    public void Render_LocalEmpty_WorkersPresent_ShowsWorkerSection_NotFullPageEmptyState()
    {
        var svc = PageTestHelpers.Descriptor("orchestrator");
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(EmptyJobs());
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new()
            {
                Name = "shipping-worker",
                Jobs = ["ShipOrder"],
                Tags = [],
                Status = WorkerStatus.Online,
                LastSeenUtc = DateTimeOffset.UtcNow,
                Online = true,
            },
        });

        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("ShipOrder"));
        cut.Markup.Should().Contain("Worker-advertised jobs", "the worker section is shown for a pure orchestrator");
        cut.Markup.Should().Contain("No local jobs on this service",
            "a small inline note replaces the full-page empty state when only worker jobs exist");
        cut.Markup.Should().NotContain("No jobs registered",
            "the full-page empty state is suppressed when worker jobs are present");
    }

    [Fact]
    public void Render_BothEmpty_ShowsFullPageEmptyState()
    {
        var svc = PageTestHelpers.Descriptor("orchestrator");
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(EmptyJobs());
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>());

        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No jobs registered",
            "the full-page empty state shows only when BOTH local and worker jobs are empty"));
        cut.Markup.Should().NotContain("Worker-advertised jobs");
    }

    [Fact]
    public void Render_GetWorkersThrows_StillRendersLocalJobs()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items = new[] { Job("process-orders", partitioned: true, tags: ProdTag) },
                TotalCount = 1,
                Offset = 0,
                Limit = 50,
            });
        // The worker fetch is best-effort: a thrown GetWorkersAsync must NOT blank the local table
        // or surface an error banner.
        client.GetWorkersAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkerInfo>>(_ => throw new UKBatchClientException(
                "server unreachable", HttpStatusCode.ServiceUnavailable, new HttpRequestException("boom")));

        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());

        var cut = RenderComponent<Catalog>(p => p.Add(c => c.ServiceName, svc.Name));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("process-orders",
            "the local table renders even when the worker fetch throws"));
        cut.Markup.Should().Contain("PARTITIONED");
        cut.Markup.Should().NotContain("Worker-advertised jobs", "a failed worker fetch contributes no rows");
        cut.Markup.Should().NotContain("error-banner",
            "a best-effort worker-fetch failure does not surface a page-level error banner");
    }
}
