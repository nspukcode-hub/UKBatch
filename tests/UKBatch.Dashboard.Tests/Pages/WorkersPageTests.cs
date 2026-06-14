using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Net;
using UKBatch.Abstractions.Workers;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Pages;

/// <summary>
/// Fuller <c>Workers.razor</c> panel coverage (extends the smoke suite
/// <see cref="WorkersSmokeTests"/>): the offline badge variant (grey + "last seen"), the error banner
/// on a failed GET (page does not crash), and the empty/online cases. Polling is NOT exercised here
/// bunit cannot reliably advance the real <c>PeriodicTimer</c>; the poll-calls-GetWorkersAsync wiring
/// is locked by a source-grep instead (per the bunit-can't-advance-real-time rule).
/// </summary>
public sealed class WorkersPageTests : TestContext
{
    private void Register(string serviceName, IUKBatchClient client)
    {
        var svc = PageTestHelpers.Descriptor(serviceName);
        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(serviceName, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Render_OfflineWorker_ShowsGreyBadgeWithLastSeen()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new()
            {
                Name = "shipping",
                Jobs = ["ShipOrder"],
                Tags = [],
                Status = WorkerStatus.Offline,
                LastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                Online = false,
            },
        });
        Register("orders", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "orders"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("shipping"));
        cut.Markup.Should().Contain("worker-badge--offline", "an offline worker renders the grey offline badge");
        cut.Markup.Should().NotContain("worker-badge--online");
        cut.Markup.Should().Contain("OFFLINE", "the offline badge text is present");
        cut.Markup.Should().Contain("ago", "the offline badge shows a relative 'last seen … ago' label");
    }

    [Fact]
    public void Render_MixedRows_ShowsBothBadges()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new() { Name = "alpha", Status = WorkerStatus.Online, LastSeenUtc = DateTimeOffset.UtcNow, Online = true },
            new() { Name = "bravo", Status = WorkerStatus.Offline, LastSeenUtc = DateTimeOffset.UtcNow.AddMinutes(-5), Online = false },
        });
        Register("svc", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "svc"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("alpha"));
        cut.Markup.Should().Contain("bravo");
        cut.Markup.Should().Contain("worker-badge--online");
        cut.Markup.Should().Contain("worker-badge--offline");
    }

    [Fact]
    public void Render_GetThrows_ShowsErrorBanner_DoesNotCrash()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlyList<WorkerInfo>>(_ => throw new UKBatchClientException(
                "server unreachable", HttpStatusCode.ServiceUnavailable, new HttpRequestException("boom")));
        Register("down", client);

        // The page must render an error banner, not throw out of OnInitializedAsync.
        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "down"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("error-banner",
            "a failed GET surfaces the ErrorBanner component, the page does not crash"));
        cut.Markup.Should().Contain("Failed to load workers", "the banner message reflects the load failure");
        cut.Markup.Should().NotContain("worker-badge--online");
    }

    [Fact]
    public void Render_EmptyList_ShowsEmptyState()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>());
        Register("orders", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "orders"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No workers reporting"));
    }

    [Fact]
    public void Render_CallsGetWorkersAsync_OnInit()
    {
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>());
        Register("orders", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "orders"));

        cut.WaitForAssertion(() =>
            client.Received().GetWorkersAsync(Arg.Any<CancellationToken>()));
    }

    [Fact]
    public void Render_NineJobs_Collapsed_ShowsEightChipsPlusMoreToggle()
    {
        // 9 jobs > MaxVisibleJobs (8) ⇒ collapsed: first 8 chips + a "+1 more" toggle at the end;
        // the 9th chip is hidden until the toggle is clicked, and there is no "Show less" yet.
        var jobs = Enumerable.Range(1, 9).Select(i => $"Job{i}").ToList();
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new() { Name = "busy", Jobs = jobs, Tags = [], Status = WorkerStatus.Online, LastSeenUtc = DateTimeOffset.UtcNow, Online = true },
        });
        Register("svc", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "svc"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Job1"));
        cut.Markup.Should().Contain("+1 more", "the collapsed row shows the overflow toggle at the end");
        cut.Markup.Should().NotContain("Show less", "the row is collapsed, so the toggle reads '+N more'");
        cut.Markup.Should().Contain("Job8", "the first 8 chips render");
        cut.Markup.Should().NotContain("Job9", "the overflow chip is hidden while collapsed");
    }

    [Fact]
    public void Click_MoreToggle_ExpandsAllChipsAndFlipsToShowLess()
    {
        var jobs = Enumerable.Range(1, 9).Select(i => $"Job{i}").ToList();
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new() { Name = "busy", Jobs = jobs, Tags = [], Status = WorkerStatus.Online, LastSeenUtc = DateTimeOffset.UtcNow, Online = true },
        });
        Register("svc", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "svc"));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("+1 more"));

        cut.Find("button.worker-chip--more").Click();

        cut.Markup.Should().Contain("Job9", "the overflow chip is revealed once expanded");
        cut.Markup.Should().Contain("Show less", "the toggle label flips when expanded");
        cut.Markup.Should().NotContain("+1 more", "the '+N more' label is replaced by 'Show less'");
    }

    [Fact]
    public void Render_EightJobs_NoOverflowToggle()
    {
        // Exactly MaxVisibleJobs ⇒ all chips render with no toggle at all.
        var jobs = Enumerable.Range(1, 8).Select(i => $"Job{i}").ToList();
        var client = PageTestHelpers.BuildClient();
        client.GetWorkersAsync(Arg.Any<CancellationToken>()).Returns(new List<WorkerInfo>
        {
            new() { Name = "exact", Jobs = jobs, Tags = [], Status = WorkerStatus.Online, LastSeenUtc = DateTimeOffset.UtcNow, Online = true },
        });
        Register("svc", client);

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "svc"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Job8"));
        cut.Markup.Should().NotContain("worker-chip--more", "8 jobs fit, so no overflow toggle renders");
        cut.Markup.Should().NotContain("Show less");
    }

    [Fact]
    public void Render_UnknownService_RedirectsToDashboard()
    {
        // An unregistered service name → the page redirects to /dashboard rather than rendering a table.
        var client = PageTestHelpers.BuildClient();
        var registry = PageTestHelpers.RegistryWith(); // empty — TryGet returns null for any name
        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor("ghost", client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(TimeProvider.System);

        var nav = Services.GetRequiredService<Bunit.TestDoubles.FakeNavigationManager>();

        var cut = RenderComponent<Workers>(p => p.Add(c => c.ServiceName, "ghost"));

        nav.Uri.Should().EndWith("/dashboard", "an unknown service redirects to the dashboard landing");
        cut.Markup.Should().NotContain("data-table", "no worker table renders for an unknown service");
    }
}
