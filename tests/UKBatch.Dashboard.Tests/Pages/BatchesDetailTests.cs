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
/// — <c>Batches/Detail</c> "Recent runs" is now per-RUN (grouped by BatchId),
/// replacing the old per-execution table (no dual tables). Verifies the grouping, the rollup
/// status, the one-row-per-run cardinality, and the run-link target.
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

    private static JobExecution Exec(string id, string batchId, JobStatus status,
        DateTimeOffset enqueued, DateTimeOffset? completed = null) => new()
    {
        ExecutionId = id,
        JobName = "step-job",
        BatchId = batchId,
        BatchDefinitionId = DefId,
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = enqueued,
        CompletedAtUtc = completed,
        AttemptNumber = 1,
        MaxRetries = 0,
        Processed = 0,
        Failed = 0,
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

    private (Bunit.TestDoubles.FakeNavigationManager nav, IUKBatchClient client) Register(IReadOnlyList<JobExecution> executions)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var client = PageTestHelpers.BuildClient();
        client.GetBatchByIdAsync(DefId, Arg.Any<CancellationToken>()).Returns(Definition());
        client.QueryExecutionsAsync(Arg.Any<JobQueryRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobExecution>
            {
                Items = executions,
                TotalCount = executions.Count,
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
    public void RecentRuns_GroupsByBatchId_OneRowPerRun_WithRollupStatus()
    {
        var now = DateTimeOffset.UtcNow;
        // run-A: 3 executions, all Completed → Completed. Earlier start.
        // run-B: 2 executions, one Failed → Failed. Later start.
        var executions = new[]
        {
            Exec("a1", "run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-10), now.AddMinutes(-9)),
            Exec("a2", "run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-10), now.AddMinutes(-8)),
            Exec("a3", "run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-9), now.AddMinutes(-8)),
            Exec("b1", "run-bbbbbbbb", JobStatus.Completed, now.AddMinutes(-5), now.AddMinutes(-4)),
            Exec("b2", "run-bbbbbbbb", JobStatus.Failed, now.AddMinutes(-5), now.AddMinutes(-3)),
        };
        Register(executions);

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Recent runs"));

        // One <tr> per RUN (2), not per execution (5).
        var runRows = cut.FindAll("table.data-table tbody tr");
        runRows.Should().HaveCount(2, "D5: one row per RUN (run-A, run-B), not one per execution");

        // Rollup status: run-B (with a Failed child) reads FAILED; run-A reads COMPLETED.
        cut.Markup.Should().Contain("FAILED", "run-B has a Failed child → rolled-up FAILED");
        cut.Markup.Should().Contain("COMPLETED", "run-A is fully Completed → rolled-up COMPLETED");

        // The run link points at the run-detail route with the (8-char-truncated) batch id label.
        cut.Markup.Should().Contain("/dashboard/svc/runs/run-aaaaaaaa");
        cut.Markup.Should().Contain("/dashboard/svc/runs/run-bbbbbbbb");
    }

    [Fact]
    public void RecentRuns_OrdersByStartedDescending_NewestFirst()
    {
        var now = DateTimeOffset.UtcNow;
        var executions = new[]
        {
            Exec("old1", "run-old00000", JobStatus.Completed, now.AddHours(-3), now.AddHours(-3)),
            Exec("new1", "run-new00000", JobStatus.Completed, now.AddMinutes(-1), now.AddMinutes(-1)),
        };
        Register(executions);

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
    public void RecentRuns_NoExecutions_ShowsEmptyState()
    {
        Register(Array.Empty<JobExecution>());

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No runs yet"));
        cut.FindAll("table.data-table tbody tr").Should().BeEmpty();
    }

    [Fact]
    public void RecentRuns_RunningRun_RollsUpToRunning_NoDuration()
    {
        var now = DateTimeOffset.UtcNow;
        // One execution still Running (no CompletedAtUtc) → run reads Running, Duration em-dash.
        var executions = new[]
        {
            Exec("r1", "run-live0000", JobStatus.Completed, now.AddMinutes(-2), now.AddMinutes(-1)),
            Exec("r2", "run-live0000", JobStatus.Running, now.AddMinutes(-2), completed: null),
        };
        Register(executions);

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("table.data-table tbody tr").Should().HaveCount(1));
        cut.Markup.Should().Contain("RUNNING", "a run with a non-terminal child rolls up to RUNNING");
        // Duration column shows the em-dash for an unfinished run.
        cut.Markup.Should().Contain("—");
    }

    // ── Topology Tree/List toggle (Q1 deferral closed) ──────

    [Fact]
    public void Topology_TreeMode_RendersDagStatusCanvas_NotOldSvgDagView()
    {
        Register(Array.Empty<JobExecution>());

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
        Register(Array.Empty<JobExecution>());

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
