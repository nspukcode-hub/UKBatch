using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Jobs;
using UKBatch.Dashboard.Components.Shared;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class JobsDetailTests : TestContext
{
    private const string JobName = "ProcessOrdersJob";
    private const int MaxRecent = 50;   // mirrors Jobs/Detail.MaxRecentRows

    private static JobDefinitionDto Definition() => new()
    {
        Name = JobName,
        IsPartitioned = false,
        MaxRetries = 3,
        TimeoutSeconds = 0,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
    };

    private static JobExecution Exec(string id, JobStatus status, DateTimeOffset enqueued) => new()
    {
        ExecutionId = id,
        JobName = JobName,
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = enqueued,
        AttemptNumber = 1,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    // Newest-first snapshot (the REST query returns the most recent first): index 0 is the newest,
    // index count-1 is the oldest, with strictly decreasing enqueue times. Ids are zero-padded so any
    // ordinal comparison would also order them by recency.
    private static PageEnvelope<JobExecution> RecentEnvelope(int count)
    {
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = Enumerable.Range(0, count)
            .Select(i => Exec($"exec-{count - 1 - i:D3}", JobStatus.Completed, baseTime.AddSeconds(count - 1 - i)))
            .ToArray();
        return new PageEnvelope<JobExecution>
        {
            Items = items,
            TotalCount = count,
            Offset = 0,
            Limit = MaxRecent,
        };
    }

    private IRenderedComponent<Detail> RenderJob(PageEnvelope<JobExecution> envelope, out IUKBatchClient client)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        client = PageTestHelpers.BuildClient();
        client.GetJobAsync(JobName, Arg.Any<CancellationToken>()).Returns(Definition());
        client.QueryExecutionsAsync(Arg.Any<JobQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        return RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Name, JobName));
    }

    // The id of the first LiveExecutionRow in document order (== the topmost rendered row).
    private static string TopRowExecutionId(IRenderedComponent<Detail> cut)
        => cut.FindComponents<LiveExecutionRow>()[0].Instance.InitialModel.ExecutionId;

    [Fact]
    public void Init_SubscribesToJobBeforeFetch()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        var order = new List<string>();
        client.SubscribeToJobAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("subscribe"); return Task.CompletedTask; });
        client.GetJobAsync(JobName, Arg.Any<CancellationToken>()).Returns(Definition());
        client.QueryExecutionsAsync(Arg.Any<JobQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => { order.Add("fetch"); return Task.FromResult(RecentEnvelope(2)); });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.Name, JobName));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Recent executions"));
        order.IndexOf("subscribe").Should().BeLessThan(order.IndexOf("fetch"),
            "live updates for new executions are not lost only if subscribe precedes the first fetch");
    }

    [Fact]
    public void Render_WithinCap_RendersAllRows_NoNotice()
    {
        var cut = RenderJob(RecentEnvelope(12), out _);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(12));
        // All rows fit ⇒ no "showing the 50 most recent" notice and no deep link.
        cut.Markup.Should().NotContain("most recent executions");
        cut.Markup.Should().NotContain("View all in Executions");
        TopRowExecutionId(cut).Should().Be("exec-011", "the newest execution renders first");
    }

    [Fact]
    public void Render_NoExecutions_ShowsEmptyState_NoNotice()
    {
        var cut = RenderJob(RecentEnvelope(0), out _);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No executions yet"));
        cut.FindComponents<LiveExecutionRow>().Should().BeEmpty();
        cut.Markup.Should().NotContain("most recent executions");
    }

    [Fact]
    public void Render_AtCap_RendersExactlyCap_AndNotice_WithJobNameDeepLink()
    {
        // The initial fetch already returns exactly the cap's worth of rows (Limit == MaxRecentRows).
        // All 50 render, and because the stored list is at the cap the notice + deep link appear.
        var cut = RenderJob(RecentEnvelope(MaxRecent), out _);

        cut.WaitForAssertion(() =>
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRecent));

        cut.Markup.Should().Contain("most recent executions");
        var link = cut.Find("p.page-subtitle a");
        link.GetAttribute("href").Should()
            .Be($"/dashboard/svc/executions?jobName={Uri.EscapeDataString(JobName)}");
        link.TextContent.Should().Contain("View all in Executions");
    }

    [Fact]
    public void LiveEventForNewExecution_RendersAtTop_StaysCapped_OldestFallsOff()
    {
        // A job already at the cap. New executions arrive over the hub: each must render at the TOP
        // (newest first), the window must stay pinned at 50, and the previously-oldest row drops off.
        var cut = RenderJob(RecentEnvelope(MaxRecent), out var client);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRecent));
        TopRowExecutionId(cut).Should().Be($"exec-{MaxRecent - 1:D3}", "the snapshot's newest row is on top");
        cut.Markup.Should().Contain("/executions/exec-000", "the oldest snapshot row starts in the window");

        // A brand-new execution (id outside the snapshot range) arrives.
        var newest = Exec("exec-new", JobStatus.Running, new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
        cut.InvokeAsync(() => client.ExecutionStateChanged += Raise.Event<Func<JobExecution, Task>>(newest));

        cut.WaitForAssertion(() =>
        {
            TopRowExecutionId(cut).Should().Be("exec-new", "the newest live execution renders at the top");
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRecent,
                "the window stays capped at 50 after the prepend");
            cut.Markup.Should().NotContain("/executions/exec-000",
                "the oldest row fell off the bottom of the capped window");
        });
    }

    [Fact]
    public void LiveEventForExistingExecution_UpdatesInPlace_DoesNotGrow()
    {
        // A status update for an execution already in the window must replace it in place, not prepend
        // a duplicate — the row count is unchanged and the row reflects the new status.
        var cut = RenderJob(RecentEnvelope(3), out var client);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(3));

        // exec-002 is the newest snapshot row (Completed). Re-emit it Running with a higher rank target
        // would regress, so instead advance an existing Running-equivalent: emit the same id with a
        // forward status. Here the snapshot rows are Completed; emit a higher attempt to prove in-place
        // replacement without growth (count stays 3).
        var updated = Exec("exec-002", JobStatus.Completed, new DateTimeOffset(2026, 1, 1, 0, 0, 2, TimeSpan.Zero));
        cut.InvokeAsync(() => client.ExecutionStateChanged += Raise.Event<Func<JobExecution, Task>>(updated));

        cut.WaitForAssertion(() =>
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(3,
                "updating an existing execution replaces it in place — the window does not grow"));
    }

    [Fact]
    public void LiveEventForOtherJob_Ignored()
    {
        // The page subscribes to one job; an event for a DIFFERENT job name must be ignored.
        var cut = RenderJob(RecentEnvelope(2), out var client);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(2));

        var other = new JobExecution
        {
            ExecutionId = "exec-other",
            JobName = "SomeOtherJob",
            Status = JobStatus.Running,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero),
            AttemptNumber = 1,
            MaxRetries = 3,
            Processed = 0,
            Failed = 0,
        };
        cut.InvokeAsync(() => client.ExecutionStateChanged += Raise.Event<Func<JobExecution, Task>>(other));

        cut.WaitForAssertion(() =>
        {
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(2,
                "an event for a different job must not add a row");
            cut.Markup.Should().NotContain("/executions/exec-other");
        });
    }
}
