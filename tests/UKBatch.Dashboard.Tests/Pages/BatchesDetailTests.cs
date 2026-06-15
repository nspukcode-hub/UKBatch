using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Batches;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Pages;

/// <summary>
/// — <c>Batches/Detail</c> "Recent runs" reads the run-store (one row per RUN), each row carrying its own
/// authoritative terminal status. Replaces the earlier per-execution roll-up. Verifies the one-row-per-run
/// cardinality, the authoritative status (a gate-failed run reads Failed without an approvals cross-ref),
/// the newest-first order, the running-run em-dash, and the run-link target.
/// </summary>
public sealed class BatchesDetailTests : TestContext
{
    public BatchesDetailTests()
    {
        // Detail's Topology Tree view now renders DagStatusCanvas (Q1 deferral
        // closed), whose OnAfterRenderAsync imports dag-status.js. Loose JSInterop returns defaults for
        // that un-set-up import — the production component catches the (graceful-degradation) failure.
        // Mirrors DagStatusCanvasTests.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private const string DefId = "def-nightly";

    private static BatchRun Run(string batchId, JobStatus? status, DateTimeOffset started,
        DateTimeOffset? completed = null, int stepCount = 3,
        int total = 0, int succeeded = 0, int failed = 0, int cancelled = 0) => new()
    {
        BatchId = batchId,
        BatchDefinitionId = DefId,
        BatchName = "NightlyClose",
        Status = status,
        StartedAtUtc = started,
        CompletedAtUtc = completed,
        StepCount = stepCount,
        Total = total,
        Succeeded = succeeded,
        Failed = failed,
        Cancelled = cancelled,
    };

    private static BatchDefinitionDto Definition() => new()
    {
        Id = DefId,
        Name = "NightlyClose",
        Source = BatchSource.Dashboard,
        Steps = new List<BatchStep>
        {
            new() { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "Close" } },
        },
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
    };

    private (Bunit.TestDoubles.FakeNavigationManager nav, IUKBatchClient client) Register(IReadOnlyList<BatchRun> runs)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var client = PageTestHelpers.BuildClient();
        client.GetBatchByIdAsync(DefId, Arg.Any<CancellationToken>()).Returns(Definition());
        client.QueryRunsAsync(DefId, Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<BatchRun>
            {
                Items = runs,
                TotalCount = runs.Count,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());
        var nav = Services.GetRequiredService<Bunit.TestDoubles.FakeNavigationManager>();
        return (nav, client);
    }

    [Fact]
    public void RecentRuns_OneRowPerRun_WithAuthoritativeStatus()
    {
        var now = DateTimeOffset.UtcNow;
        // run-A completed; run-B FAILED at a gate (no execution row would reveal this — the run record does).
        var runs = new[]
        {
            Run("run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-10), now.AddMinutes(-8), total: 3, succeeded: 3),
            Run("run-bbbbbbbb", JobStatus.Failed, now.AddMinutes(-5), now.AddMinutes(-3), total: 1, succeeded: 1),
        };
        Register(runs);

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Recent runs"));

        // One <tr> per RUN.
        var runRows = cut.FindAll("table.data-table tbody tr");
        runRows.Should().HaveCount(2, "one row per RUN from the run-store");

        // Authoritative status straight off the run record: a gate-failed run reads FAILED.
        cut.Markup.Should().Contain("FAILED", "run-B's recorded terminal status is Failed (gate-failed)");
        cut.Markup.Should().Contain("COMPLETED", "run-A's recorded terminal status is Completed");

        // The run link points at the run-detail route with the (8-char-truncated) batch id label.
        cut.Markup.Should().Contain("/dashboard/svc/runs/run-aaaaaaaa");
        cut.Markup.Should().Contain("/dashboard/svc/runs/run-bbbbbbbb");
    }

    [Fact]
    public void RecentRuns_OrdersByStartedDescending_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var runs = new[]
        {
            Run("run-old00000", JobStatus.Completed, now.AddHours(-3), now.AddHours(-3)),
            Run("run-new00000", JobStatus.Completed, now.AddMinutes(-1), now.AddMinutes(-1)),
        };
        Register(runs);

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("table.data-table tbody tr").Should().HaveCount(2));

        var markup = cut.Markup;
        var newIdx = markup.IndexOf("run-new00000", StringComparison.Ordinal);
        var oldIdx = markup.IndexOf("run-old00000", StringComparison.Ordinal);
        newIdx.Should().BeGreaterThan(0);
        oldIdx.Should().BeGreaterThan(0);
        newIdx.Should().BeLessThan(oldIdx, "runs are ordered by StartedAtUtc DESC — newest first");
    }

    [Fact]
    public void RecentRuns_NoRuns_ShowsEmptyState()
    {
        Register(Array.Empty<BatchRun>());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No runs yet"));
        cut.FindAll("table.data-table tbody tr").Should().BeEmpty();
    }

    [Fact]
    public void RecentRuns_RunningRun_ReadsRunning_NoDuration()
    {
        var now = DateTimeOffset.UtcNow;
        // A run still in progress (Status null, no CompletedAtUtc) → reads RUNNING, Duration em-dash.
        var runs = new[]
        {
            Run("run-live0000", status: null, now.AddMinutes(-2), completed: null),
        };
        Register(runs);

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("table.data-table tbody tr").Should().HaveCount(1));
        cut.Markup.Should().Contain("RUNNING", "a run with a null (in-progress) status reads RUNNING");
        // Duration column shows the em-dash for an unfinished run.
        cut.Markup.Should().Contain("—");
    }

    // ── Topology Tree/List toggle (Q1 deferral closed) ──────

    [Fact]
    public void Topology_TreeMode_RendersDagStatusCanvas_NotOldSvgDagView()
    {
        Register(Array.Empty<BatchRun>());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        // _treeMode defaults to true ⇒ the Tree view renders the read-only Drawflow canvas container.
        cut.WaitForAssertion(() =>
            cut.FindAll("div.dag-status-canvas").Should().HaveCount(1,
                "Tree view now renders DagStatusCanvas (transform-safe), not the old SVG DagView"));
        // The old SVG renderer's <svg.dag-view> is gone in Tree mode.
        cut.FindAll("svg.dag-view").Should().BeEmpty("the SVG DagView is no longer used on Detail");
        cut.FindAll("div.batch-step-list").Should().BeEmpty("List view is not active in Tree mode");
    }

    [Fact]
    public void Topology_ToggleToList_RendersBatchStepListView_AndBack()
    {
        Register(Array.Empty<BatchRun>());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("div.dag-status-canvas").Should().HaveCount(1));

        // Click "List" — the toolbar's second toggle button (Detail owns the toggle now).
        var listButton = cut.FindAll("button.dag-toolbar__btn")
            .First(b => b.TextContent.Contains("List"));
        listButton.Click();

        cut.FindAll("div.batch-step-list").Should().HaveCount(1, "List view shows the step list");
        cut.FindAll("div.dag-status-canvas").Should().BeEmpty("the canvas is hidden in List mode");

        // Toggle back to Tree — the canvas returns.
        var treeButton = cut.FindAll("button.dag-toolbar__btn")
            .First(b => b.TextContent.Contains("Tree"));
        treeButton.Click();

        cut.WaitForAssertion(() => cut.FindAll("div.dag-status-canvas").Should().HaveCount(1,
            "toggling back to Tree restores the canvas"));
        cut.FindAll("div.batch-step-list").Should().BeEmpty();
    }
}
