using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Executions;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Batches;
using UKBatch.Dashboard.Configuration;
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

    // A scheduled variant — carries a cron and the active/paused flag the chip + toggle read.
    private static BatchDefinitionDto ScheduledDefinition(bool scheduleEnabled) => Definition() with
    {
        Schedule = "0 0/2 * * * *",
        ScheduleEnabled = scheduleEnabled,
    };

    private (Bunit.TestDoubles.FakeNavigationManager nav, IUKBatchClient client) Register(
        IReadOnlyList<BatchRun> runs, BatchDefinitionDto? definition = null)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var client = PageTestHelpers.BuildClient();
        client.GetBatchByIdAsync(DefId, Arg.Any<CancellationToken>()).Returns(definition ?? Definition());
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

    // ── Schedule pause/resume ───────────────────────────────

    [Fact]
    public void Schedule_ActiveDefinition_RendersActiveChip_AndPauseButton()
    {
        Register(Array.Empty<BatchRun>(), ScheduledDefinition(scheduleEnabled: true));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("span.schedule-chip--active").Should().HaveCount(1,
            "an enabled schedule reads as an active chip"));
        cut.FindAll("span.schedule-chip--paused").Should().BeEmpty();

        var toggle = cut.FindAll("button.btn").First(b => b.TextContent.Contains("Pause schedule"));
        toggle.TextContent.Should().Contain("Pause schedule");
    }

    [Fact]
    public void Schedule_PausedDefinition_RendersPausedChip_AndResumeButton()
    {
        Register(Array.Empty<BatchRun>(), ScheduledDefinition(scheduleEnabled: false));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("span.schedule-chip--paused").Should().HaveCount(1,
            "a paused schedule reads as a paused chip"));
        cut.FindAll("span.schedule-chip--active").Should().BeEmpty();

        var toggle = cut.FindAll("button.btn").First(b => b.TextContent.Contains("Resume schedule"));
        toggle.TextContent.Should().Contain("Resume schedule");
    }

    [Fact]
    public void Schedule_ClickPause_CallsSetScheduleEnabledFalse()
    {
        var (_, client) = Register(Array.Empty<BatchRun>(), ScheduledDefinition(scheduleEnabled: true));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("button.btn").Any(b => b.TextContent.Contains("Pause schedule")).Should().BeTrue());
        cut.FindAll("button.btn").First(b => b.TextContent.Contains("Pause schedule")).Click();

        // Pausing an active schedule disables it.
        client.Received(1).SetScheduleEnabledAsync(DefId, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Schedule_ClickResume_CallsSetScheduleEnabledTrue()
    {
        var (_, client) = Register(Array.Empty<BatchRun>(), ScheduledDefinition(scheduleEnabled: false));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.FindAll("button.btn").Any(b => b.TextContent.Contains("Resume schedule")).Should().BeTrue());
        cut.FindAll("button.btn").First(b => b.TextContent.Contains("Resume schedule")).Click();

        // Resuming a paused schedule enables it.
        client.Received(1).SetScheduleEnabledAsync(DefId, true, Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Schedule_StaleNotExecutedLabel_IsGone()
    {
        // Regression lock: the old "(not executed yet)" caption must never render again.
        Register(Array.Empty<BatchRun>(), ScheduledDefinition(scheduleEnabled: true));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("schedule-chip"));
        cut.Markup.Should().NotContain("(not executed yet)", "batch schedules fire on their cron — the stale caption is removed");
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

    // ── Live "Recent runs" ──────────────────────────────────
    // The run list refetches on an execution event for THIS definition (a run that starts / changes state /
    // completes streams an ExecutionStateChanged carrying our BatchDefinitionId), is unaffected by an event
    // for a DIFFERENT definition, and unsubscribes on dispose. A recording fake client is used (rather than
    // the NSubstitute Register helper) so an awaitable ExecutionStateChanged can be fired and the
    // QueryRunsAsync refetch counted.

    private RecordingDetailClient WireRecording(params BatchRun[] runs)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var client = new RecordingDetailClient(DefId, Definition()) { Runs = runs };
        Services.AddSingleton(PageTestHelpers.RegistryWith(svc));
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());
        // A near-zero debounce window keeps the refetch synchronous-enough for WaitForAssertion without
        // slowing the test; the production default (100 ms) is exercised by the live pages at runtime.
        Services.AddSingleton(PageTestHelpers.NewOptions(new UKBatch.Dashboard.Configuration.DashboardOptions
        {
            UiRefreshDebounce = TimeSpan.FromMilliseconds(1),
        }));
        return client;
    }

    private static JobExecution ExecForDef(string defId, string execId = "exec-1") => new()
    {
        ExecutionId = execId,
        JobName = "Close",
        BatchId = "run-live",
        BatchDefinitionId = defId,
        Status = JobStatus.Running,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    [Fact]
    public async Task LiveRuns_ExecutionEventForThisDefinition_RefetchesRunPage()
    {
        var now = DateTimeOffset.UtcNow;
        var client = WireRecording(Run("run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-5), now.AddMinutes(-4)));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        // Initial load = one QueryRunsAsync; the subscription is armed before the first fetch.
        cut.WaitForAssertion(() => client.QueryRunsCount.Should().Be(1));
        client.AllSubscribed.Should().BeTrue("the page subscribes to the all-stream to drive liveness");

        // A new run of THIS definition appears, and an execution event streams in carrying our definition id.
        client.Runs = new[]
        {
            Run("run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-5), now.AddMinutes(-4)),
            Run("run-bbbbbbbb", status: null, now.AddSeconds(-2)),   // new, still running
        };
        await client.RaiseExecutionStateChangedAsync(ExecForDef(DefId));

        // The debounced refetch fires → the run list now reflects the new run without a page reload.
        cut.WaitForAssertion(() =>
        {
            client.QueryRunsCount.Should().BeGreaterThan(1, "an execution event for this definition refetches the runs");
            cut.Markup.Should().Contain("run-bbbbbbbb", "the newly-fired run appears live");
        });
    }

    [Fact]
    public async Task LiveRuns_ExecutionEventForDifferentDefinition_DoesNotRefetch()
    {
        var now = DateTimeOffset.UtcNow;
        var client = WireRecording(Run("run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-5), now.AddMinutes(-4)));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => client.QueryRunsCount.Should().Be(1));

        // An execution event for an UNRELATED definition must be ignored — no refetch.
        await client.RaiseExecutionStateChangedAsync(ExecForDef("some-other-definition"));

        // Give any (erroneous) debounced refetch a window to fire, then assert it did not.
        await Task.Delay(50);
        client.QueryRunsCount.Should().Be(1, "an event for a different definition does not refetch this list");
    }

    [Fact]
    public async Task LiveRuns_DisposeUnsubscribes()
    {
        var now = DateTimeOffset.UtcNow;
        var client = WireRecording(Run("run-aaaaaaaa", JobStatus.Completed, now.AddMinutes(-5), now.AddMinutes(-4)));

        var cut = RenderComponent<Detail>(p => p
            .Add(d => d.ServiceName, "svc")
            .Add(d => d.BatchId, DefId));

        cut.WaitForAssertion(() => client.AllSubscribed.Should().BeTrue());

        // Disposing the component must detach the handler and leave the all-group (subscription symmetry).
        await cut.Instance.DisposeAsync();

        client.HandlerCount.Should().Be(0, "DisposeAsync detaches the ExecutionStateChanged handler");
        client.UnsubscribeAllCount.Should().Be(1, "DisposeAsync leaves the all-group it subscribed to");

        // A late event after dispose must be a no-op (handler gone) — must not refetch or throw.
        var before = client.QueryRunsCount;
        await client.RaiseExecutionStateChangedAsync(ExecForDef(DefId));
        client.QueryRunsCount.Should().Be(before, "a post-dispose event does not refetch");
    }

    /// <summary>
    /// Recording <see cref="IUKBatchClient"/> stub for the live run-list tests: returns a settable run page,
    /// counts <c>QueryRunsAsync</c> / subscribe / unsubscribe calls, and exposes the captured
    /// <c>ExecutionStateChanged</c> invocation list so a test can fire an awaitable event.
    /// </summary>
    private sealed class RecordingDetailClient : IUKBatchClient
    {
        private readonly string _defId;
        private readonly BatchDefinitionDto _definition;

        public RecordingDetailClient(string defId, BatchDefinitionDto definition)
        {
            _defId = defId;
            _definition = definition;
        }

        public IReadOnlyList<BatchRun> Runs { get; set; } = Array.Empty<BatchRun>();
        public int QueryRunsCount { get; private set; }
        public bool AllSubscribed { get; private set; }
        public int UnsubscribeAllCount { get; private set; }
        public int HandlerCount => ExecutionStateChanged?.GetInvocationList().Length ?? 0;

        public UKBatchServiceDescriptor Service { get; } = new()
        {
            Name = "svc",
            BaseUrl = new Uri("http://svc.local:5000/api/"),
        };
        public UKBatchClientState State => UKBatchClientState.Connected;

#pragma warning disable CS0067 // events declared to satisfy the interface; only ExecutionStateChanged is fired
        public event Func<UKBatchClientState, Task>? StateChanged;
        public event Func<JobExecution, Task>? ExecutionStateChanged;
        public event Func<ProgressBeat, Task>? ProgressUpdated;
        public event Func<PendingApproval, Task>? ApprovalRequested;
        public event Func<BatchCompletionSummary, Task>? BatchCompleted;
#pragma warning restore CS0067

        public Task RaiseExecutionStateChangedAsync(JobExecution exec)
        {
            var handler = ExecutionStateChanged;
            return handler is null
                ? Task.CompletedTask
                : Task.WhenAll(handler.GetInvocationList()
                    .Cast<Func<JobExecution, Task>>()
                    .Select(h => h(exec)));
        }

        public Task<BatchDefinitionDto?> GetBatchByIdAsync(string definitionId, CancellationToken ct)
            => Task.FromResult<BatchDefinitionDto?>(definitionId == _defId ? _definition : null);

        public Task<PageEnvelope<BatchRun>> QueryRunsAsync(string? batchDefinitionId, bool includeRunning, int offset, int limit, CancellationToken ct)
        {
            QueryRunsCount++;
            return Task.FromResult(new PageEnvelope<BatchRun>
            {
                Items = Runs,
                TotalCount = Runs.Count,
                Offset = 0,
                Limit = limit,
            });
        }

        public Task SubscribeAllAsync(CancellationToken ct) { AllSubscribed = true; return Task.CompletedTask; }
        public Task UnsubscribeAllAsync(CancellationToken ct) { UnsubscribeAllCount++; return Task.CompletedTask; }

        public Task ConnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task DisconnectAsync(CancellationToken ct) => Task.CompletedTask;
        public Task<PageEnvelope<JobDefinitionDto>> ListJobsAsync(int offset, int limit, bool? partitioned, CancellationToken ct) => throw new NotImplementedException();
        public Task<JobDefinitionDto?> GetJobAsync(string jobName, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> TriggerJobAsync(string jobName, IReadOnlyDictionary<string, object?>? parameters, string? triggeredBy, CancellationToken ct) => throw new NotImplementedException();
        public Task<PageEnvelope<BatchDefinitionDto>> ListBatchesAsync(int offset, int limit, string? nameContains, BatchSource? source, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchDefinitionDto?> GetBatchByNameAsync(string name, BatchSource? source, CancellationToken ct) => throw new NotImplementedException();
        public Task<string> RunBatchByIdAsync(string definitionId, IReadOnlyDictionary<string, object?>? initialParameters, string? triggeredBy, CancellationToken ct) => throw new NotImplementedException();
        public Task<PageEnvelope<JobExecution>> GetBatchRunStatusAsync(string batchRunId, int offset, int limit, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchDefinitionDto> CreateBatchAsync(CreateBatchRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<BatchDefinitionDto> UpdateBatchAsync(string definitionId, UpdateBatchRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task DeleteBatchAsync(string definitionId, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelRunAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task<string> RetryRunAsync(string batchRunId, CancellationToken ct) => Task.FromResult(string.Empty);
        public Task SetScheduleEnabledAsync(string definitionId, bool enabled, CancellationToken ct) => Task.CompletedTask;
        public Task<JobExecution?> GetExecutionAsync(string executionId, CancellationToken ct) => throw new NotImplementedException();
        public Task<PageEnvelope<JobExecution>> QueryExecutionsAsync(JobQueryRequest query, CancellationToken ct) => throw new NotImplementedException();
        public Task CancelExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<PendingApprovalDto>> ListApprovalsAsync(string? role, CancellationToken ct) => throw new NotImplementedException();
        public Task ApproveAsync(string approvalId, string? note, CancellationToken ct) => throw new NotImplementedException();
        public Task RejectAsync(string approvalId, string reason, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApprovalGateViewDto>> ListBatchGatesAsync(string batchId, CancellationToken ct) => throw new NotImplementedException();
        public Task<IReadOnlyList<UKBatch.Abstractions.Workers.WorkerInfo>> GetWorkersAsync(CancellationToken ct) => throw new NotImplementedException();
        public Task SubscribeToExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromExecutionAsync(string executionId, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeToBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromBatchAsync(string batchRunId, CancellationToken ct) => Task.CompletedTask;
        public Task SubscribeToJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public Task UnsubscribeFromJobAsync(string jobName, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
