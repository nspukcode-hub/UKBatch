using System.Net;
using Bunit;
using FluentAssertions;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Approvals;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Batches;
using UKBatch.Dashboard.Components.Shared;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;
using Microsoft.Extensions.DependencyInjection;

namespace UKBatch.Dashboard.Tests.Pages;

public sealed class BatchesRunDetailTests : TestContext
{
    public BatchesRunDetailTests()
    {
        // Tests that exercise the live DAG (defId set ⇒ DagStatusCanvas renders) need Loose JS mode
        // the canvas does import/init/buildGraph (all returning IJSObjectReference); STRICT would crash.
        // The pre-existing tests never render the canvas (their execs carry no BatchDefinitionId), so
        // Loose mode is a no-op for them.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private static JobExecution Exec(string id, string? batchId, JobStatus status,
        string? batchStepId = null) => new()
    {
        ExecutionId = id,
        JobName = "step-job",
        BatchId = batchId,
        BatchStepId = batchStepId,
        Status = status,
        Parameters = new Dictionary<string, object?>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
        MaxRetries = 3,
        Processed = 0,
        Failed = 0,
    };

    [Fact]
    public async Task Init_SubscribesToBatchBeforeFetch()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        var order = new List<string>();
        client.SubscribeToBatchAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => { order.Add("subscribe"); return Task.CompletedTask; });
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                order.Add("fetch");
                return new PageEnvelope<JobExecution>
                {
                    Items = new[] { Exec("e1", "br-1", JobStatus.Running) },
                    TotalCount = 1,
                    Offset = 0,
                    Limit = 50,
                };
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, "br-1"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("br-1"));
        order.IndexOf("subscribe").Should().BeLessThan(order.IndexOf("fetch"));
    }

    [Fact]
    public void Render_NoExecutionsYet_ShowsWaitingState()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, "br-empty"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Waiting for first execution"));
    }

    [Fact]
    public void Render_WithExecutions_ShowsLiveRows()
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobExecution>
            {
                Items = new[]
                {
                    Exec("e1", "br-2", JobStatus.Completed),
                    Exec("e2", "br-2", JobStatus.Running),
                },
                TotalCount = 2,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, "br-2"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("COMPLETED"));
        cut.Markup.Should().Contain("RUNNING");
    }

    // ── BUG 2: cross-service row advances Running → Completed in the table ──────────

    [Fact]
    public void CrossServiceRow_RunningThenCompleted_TableRowEndsCompleted()
    {
        // BUG 2 REGRESSION. A cross-service step mints a Running shadow row (RecordCrossServiceStartAsync)
        // then UPDATES it to Completed (RecordCrossServiceEndAsync) — BOTH publish through the watch hub
        // as ExecutionStateChanged for the SAME ExecutionId. The page replaces _executionRows[idx] and
        // re-renders the @key-stable LiveExecutionRow. Before the fix, the row's OnParametersSet froze
        // on `id-equal ⇒ skip`, so the table stayed RUNNING while the DAG (built from the same row list
        // via RebuildStatusMap) showed COMPLETED. After the fix the table row also reaches COMPLETED.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();

        // Snapshot already carries the Running cross-service shadow row (BatchStepId = DAG join key).
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobExecution>
            {
                Items = new[] { Exec("xsvc-exec", "br-xs", JobStatus.Running, batchStepId: "step-1") },
                TotalCount = 1,
                Offset = 0,
                Limit = 50,
            });

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, "br-xs"));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("RUNNING"));

        // The remote worker finished — RecordCrossServiceEndAsync's terminal update arrives as an
        // ExecutionStateChanged for the SAME ExecutionId (UpdateStatusAsync preserves BatchStepId).
        cut.InvokeAsync(() => client.ExecutionStateChanged += Raise.Event<Func<JobExecution, Task>>(
            Exec("xsvc-exec", "br-xs", JobStatus.Completed, batchStepId: "step-1")));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("COMPLETED",
                "BUG 2: the table row for a cross-service execution MUST advance Running → Completed");
            cut.Markup.Should().NotContain("RUNNING",
                "no row should be stuck Running once the cross-service terminal update arrives");
        });
    }

    // ── completion banner XOR empty-state ─────────────

    [Fact]
    public void Render_CompletionSet_RowsEmpty_ShowsBannerOnly_NoWaitingState()
    {
        // a batch that completed with ZERO surviving rows (e.g. cross-service-only run whose
        // shadow rows were undercounted) must show the completion banner — NOT the "Waiting…" empty
        // state. The empty-state gate is `_executionRows.Count == 0 && _completion is null`.
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
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
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, "br-done"));

        // Initially empty + no completion ⇒ the waiting state shows.
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Waiting for first execution"));

        // Raise BatchCompleted for THIS run id — the page sets _completion, the gate now suppresses
        // the empty-state and renders the banner instead.
        var summary = new BatchCompletionSummary
        {
            BatchId = "br-done",
            BatchDefinitionId = "def-1",
            BatchName = "NightlyClose",
            FinalStatus = JobStatus.Completed,
            TotalJobs = 3,
            SucceededJobs = 3,
            FailedJobs = 0,
            CancelledJobs = 0,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        cut.InvokeAsync(() => client.BatchCompleted += Raise.Event<Func<BatchCompletionSummary, Task>>(summary));

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("NightlyClose completed", "the completion banner renders");
            cut.Markup.Should().NotContain("Waiting for first execution",
 "banner and empty-state are mutually exclusive once _completion is set");
        });
    }

    // ── inline approve/reject from the live DAG node inspector ──────────────
    //
    // The Drawflow node DOM is JS-built and not present under bunit, so a node CLICK can't be
    // simulated. Instead we drive the SAME contract the JS bridge invokes: DagStatusCanvas.OnNodeSelected
    // (the [JSInvokable] callback forwards a click to exactly this EventCallback). Selecting the gate
    // renders the inspector's Decision section; the panel's Approve/Reject buttons flow through to
    // RunDetail's HandleApprove/HandleRejectAsync. This is the real handler path — NOT a faked JS path.

    private const string GateStepId = "step-gate";

    private static BatchStep GateStep() => new()
    {
        StepId = GateStepId,
        Order = 0,
        StepType = BatchStepType.ApprovalGate,
        Approval = new ApprovalGateConfig
        {
            Title = "Release approval",
            AllowedRoles = new[] { "ops" },
            OnTimeout = ApprovalTimeoutAction.Fail,
        },
    };

    private static BatchDefinitionDto GateDefinition(string defId) => new()
    {
        Id = defId,
        Name = "GatedBatch",
        Source = BatchSource.Dashboard,
        Steps = new[] { GateStep() },
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
    };

    private static PendingApprovalDto PendingFor(string approvalId, string batchRunId) => new()
    {
        ApprovalId = approvalId,
        BatchId = batchRunId,
        BatchStepId = GateStepId,
        BatchName = "GatedBatch",
        Config = GateStep().Approval!,
        PendingSinceUtc = DateTimeOffset.UtcNow,
    };

    /// <summary>
    /// Wires a client whose run carries a definition (DAG renders) with a single pending gate for THIS
    /// run. Returns the rendered RunDetail + the client so the test can select the gate node and assert.
    /// </summary>
    private (IRenderedComponent<RunDetail> Cut, IUKBatchClient Client) RenderGatedRun(
        string batchRunId, string approvalId, IReadOnlyList<PendingApprovalDto> approvals)
    {
        const string defId = "def-gate";
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        var client = PageTestHelpers.BuildClient();

        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobExecution>
            {
                // A row carrying the BatchDefinitionId so RunDetail lazy-loads the topology.
                Items = new[] { Exec("e1", batchRunId, JobStatus.Running) with { BatchDefinitionId = defId } },
                TotalCount = 1,
                Offset = 0,
                Limit = 50,
            });
        client.GetBatchByIdAsync(defId, Arg.Any<CancellationToken>()).Returns(GateDefinition(defId));
        client.ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(approvals);

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewOptions());

        var cut = RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, batchRunId));

        cut.WaitForAssertion(() => cut.FindComponent<DagStatusCanvas>().Should().NotBeNull(),
            TimeSpan.FromSeconds(2));
        return (cut, client);
    }

    private static async Task SelectNodeAsync(IRenderedComponent<RunDetail> cut, BatchStep step)
    {
        var canvas = cut.FindComponent<DagStatusCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeSelected.InvokeAsync(step));
    }

    // drive the in-node Approve contract the JS bridge invokes: the delegated container
    // click resolves the StepId and raises DagStatusCanvas.OnApproveClicked. bunit can't render the
    // JS-built button, so (exactly as with OnNodeSelected) we invoke the EventCallback directly — the
    // real RunDetail handler path, NOT a faked JS path.
    private static async Task ApproveNodeAsync(IRenderedComponent<RunDetail> cut, BatchStep step)
    {
        var canvas = cut.FindComponent<DagStatusCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnApproveClicked.InvokeAsync(step));
    }

    [Fact]
    public async Task SelectPendingGate_ShowsDecisionSection()
    {
        var (cut, _) = RenderGatedRun("br-gate", "appr-1", new[] { PendingFor("appr-1", "br-gate") });

        await SelectNodeAsync(cut, GateStep());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Decision", "the inspector offers the decision UI for a live gate");
            cut.FindAll("button.btn--primary").Should().NotBeEmpty();
            cut.FindAll("button.btn--danger").Should().NotBeEmpty();
        });
    }

    [Fact]
    public async Task Approve_CallsApproveWithStoredId_ThenReLists()
    {
        var (cut, client) = RenderGatedRun("br-gate", "appr-77", new[] { PendingFor("appr-77", "br-gate") });
        client.ClearReceivedCalls();   // ignore the init-time list; assert on the post-approve re-list

        await SelectNodeAsync(cut, GateStep());
        cut.WaitForAssertion(() => cut.Find("button.btn--primary").Should().NotBeNull());

        await cut.Find("button.btn--primary").ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            // The stored approvalId (from the pending DTO keyed by step) is used — not the step id.
            client.Received().ApproveAsync("appr-77", null, Arg.Any<CancellationToken>());
            // On success the page re-lists approvals so the reconciler turns the gate green.
            client.Received().ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task Reject_WithReason_CallsRejectWithStoredIdAndReason()
    {
        var (cut, client) = RenderGatedRun("br-gate", "appr-9", new[] { PendingFor("appr-9", "br-gate") });

        await SelectNodeAsync(cut, GateStep());
        cut.WaitForAssertion(() => cut.Find("input.form-field__input").Should().NotBeNull());

        cut.Find("input.form-field__input").Input("compliance hold");
        await cut.Find("button.btn--danger").ClickAsync(new());

        cut.WaitForAssertion(() =>
            client.Received().RejectAsync("appr-9", "compliance hold", Arg.Any<CancellationToken>()));
    }

    [Fact]
    public async Task Approve_403_SurfacesRoleMismatchMessage_NoCrash()
    {
        var (cut, client) = RenderGatedRun("br-gate", "appr-x", new[] { PendingFor("appr-x", "br-gate") });
        client.ApproveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new UKBatchClientException("Forbidden", HttpStatusCode.Forbidden, "ukbatch:forbidden"));

        await SelectNodeAsync(cut, GateStep());
        cut.WaitForAssertion(() => cut.Find("button.btn--primary").Should().NotBeNull());

        await cut.Find("button.btn--primary").ClickAsync(new());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Not authorized to decide this gate (role mismatch).",
                "a 403 role mismatch is surfaced in the panel, not thrown");
            cut.FindAll("span.form-field__error").Should().NotBeEmpty();
        });
        // The page is still alive and the gate is still selectable (not redirected / crashed).
        cut.FindComponent<DagStatusCanvas>().Should().NotBeNull();
    }

    // ── in-node Approve button on the live DAG gate ─────────────────────────

    [Fact]
    public async Task PendingGate_FeedsAwaitingGatesToCanvasAsPendingStepIds()
    {
        // The canvas reveals the in-node Approve button only when the gate's StepId is in PendingStepIds.
        // RunDetail must feed that from _awaitingGates (the reconciler's pending set), NOT from the status
        // map. Assert the parameter actually reaches the child carrying THIS gate's StepId.
        var (cut, _) = RenderGatedRun("br-gate", "appr-1", new[] { PendingFor("appr-1", "br-gate") });

        cut.WaitForAssertion(() =>
        {
            var canvas = cut.FindComponent<DagStatusCanvas>();
            canvas.Instance.PendingStepIds.Should().Contain(GateStepId,
                "RunDetail feeds _awaitingGates to the canvas so the in-node Approve button can appear on the pending gate");
        });
    }

    [Fact]
    public async Task ApproveFromNode_SelectsGate_CallsApproveWithStoredId_ThenReLists()
    {
        // The in-node Approve raises OnApproveClicked. RunDetail must (a) select the gate (so the inspector
        // — the error surface — opens) and (b) run the SAME approve path: ApproveAsync(storedId) → re-list.
        var (cut, client) = RenderGatedRun("br-gate", "appr-55", new[] { PendingFor("appr-55", "br-gate") });
        client.ClearReceivedCalls();   // ignore the init-time list; assert on the post-approve re-list

        await ApproveNodeAsync(cut, GateStep());

        cut.WaitForAssertion(() =>
        {
            // Selecting the gate opened the inspector (the Decision section renders for the live gate).
            cut.Markup.Should().Contain("Decision",
                "the in-node approve selects the gate first → the inspector (error surface) opens");
            // The stored approvalId (keyed by step) is used — reusing HandleApproveAsync, not a new path.
            client.Received().ApproveAsync("appr-55", null, Arg.Any<CancellationToken>());
            client.Received().ListApprovalsAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task ApproveFromNode_403_SurfacesRoleMismatch_InOpenPanel_NoCrash()
    {
        // A failed in-node approve must surface in the inspector that the approve just opened — the gate is
        // already in view. Same DecisionError contract as the panel button; no separate error path.
        var (cut, client) = RenderGatedRun("br-gate", "appr-x", new[] { PendingFor("appr-x", "br-gate") });
        client.ApproveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new UKBatchClientException("Forbidden", HttpStatusCode.Forbidden, "ukbatch:forbidden"));

        await ApproveNodeAsync(cut, GateStep());

        cut.WaitForAssertion(() =>
        {
            cut.Markup.Should().Contain("Not authorized to decide this gate (role mismatch).",
                "a 403 from the in-node approve surfaces in the panel the approve opened, not thrown");
            cut.FindAll("span.form-field__error").Should().NotBeEmpty();
        });
        cut.FindComponent<DagStatusCanvas>().Should().NotBeNull("the page survives a failed in-node approve");
    }

    [Fact]
    public async Task ApproveFromNode_BusyGuard_SecondCallNoOps()
    {
        // Double-click safety on the node path. HandleApproveFromNodeAsync re-selects the gate first, which
        // RESETS _decisionBusy — so without its own same-gate busy short-circuit a second rapid click would
        // defeat HandleApproveAsync's guard and double-submit. Hold the first ApproveAsync open (the first
        // call parks busy), fire a second node-approve on the same gate (must no-op), release, then assert
        // EXACTLY ONE ApproveAsync reached the client.
        var (cut, client) = RenderGatedRun("br-gate", "appr-busy", new[] { PendingFor("appr-busy", "br-gate") });
        client.ClearReceivedCalls();

        var gate = new TaskCompletionSource();
        client.ApproveAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(_ => gate.Task);

        // First approve enters the busy section and PARKS on the gated ApproveAsync. Do NOT await its
        // completion here (it can't finish until gate.SetResult) — start it and let it park.
        var canvas = cut.FindComponent<DagStatusCanvas>();
        var firstClick = cut.InvokeAsync(() => canvas.Instance.OnApproveClicked.InvokeAsync(GateStep()));

        // It is busy now (the first parked on the gate). The second approve on the SAME gate must be
        // rejected by the same-gate busy short-circuit BEFORE re-selecting / calling ApproveAsync again.
        cut.WaitForState(() => client.ReceivedCalls()
            .Any(c => c.GetMethodInfo().Name == nameof(IUKBatchClient.ApproveAsync)));
        await cut.InvokeAsync(() => canvas.Instance.OnApproveClicked.InvokeAsync(GateStep()));

        gate.SetResult();      // release the first call
        await firstClick;      // now it can complete

        await client.Received(1).ApproveAsync("appr-busy", null, Arg.Any<CancellationToken>());
    }

    // ── capped newest-first live window over the Executions table ────────────────────
    //
    // The Executions section is a live activity window: it renders the most recent executions newest-first
    // and caps the rendered rows. The stored list stays whole (the section header count, RebuildStatusMap,
    // and the completion roll-up all still see every row); the full list lives on the Executions page,
    // linked once the cap is exceeded.

    private const int MaxRendered = 50;   // mirrors RunDetail.MaxRenderedRows

    private static PageEnvelope<JobExecution> RowsEnvelope(string batchRunId, int count)
    {
        // Distinct ids AND distinct, increasing enqueue times so "newest first" is unambiguous: index 0 is
        // the oldest, index count-1 is the newest. Ids are zero-padded so an ordinal tiebreak (if two rows
        // shared a timestamp) would also sort by index.
        var baseTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = Enumerable.Range(0, count)
            .Select(i => Exec($"exec-{i:D3}", batchRunId, JobStatus.Completed)
                with { EnqueuedAtUtc = baseTime.AddSeconds(i) })
            .ToArray();
        return new PageEnvelope<JobExecution>
        {
            Items = items,
            TotalCount = count,
            Offset = 0,
            Limit = 500,
        };
    }

    private IRenderedComponent<RunDetail> RenderRun(string batchRunId, PageEnvelope<JobExecution> envelope,
        out IUKBatchClient client)
    {
        var svc = PageTestHelpers.Descriptor("svc");
        var registry = PageTestHelpers.RegistryWith(svc);
        client = PageTestHelpers.BuildClient();
        client.GetBatchRunStatusAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(envelope);

        Services.AddSingleton(registry);
        Services.AddSingleton(PageTestHelpers.FactoryFor(svc.Name, client));
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewOptions());

        return RenderComponent<RunDetail>(p => p
            .Add(d => d.ServiceName, svc.Name)
            .Add(d => d.BatchRunId, batchRunId));
    }

    // The id of the first LiveExecutionRow in document order (== the topmost rendered row).
    private static string TopRowExecutionId(IRenderedComponent<RunDetail> cut)
        => cut.FindComponents<LiveExecutionRow>()[0].Instance.InitialModel.ExecutionId;

    [Fact]
    public void Executions_FetchBeyondCap_RendersExactlyCap_NewestFirst()
    {
        // 64 rows (> 50-cap). Only the 50 most recent render, newest at the top. exec-063 is the newest by
        // EnqueuedAtUtc; the window spans indices 63..14 (50 rows), so exec-014 is the oldest still shown
        // and exec-013 and older fall outside.
        var cut = RenderRun("br-cap", RowsEnvelope("br-cap", 64), out _);

        cut.WaitForAssertion(() =>
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRendered,
                "only the 50 most recent rows render, not all 64"));

        TopRowExecutionId(cut).Should().Be("exec-063", "the newest execution renders first");
        cut.Markup.Should().Contain("/executions/exec-063");
        cut.Markup.Should().Contain("/executions/exec-014", "the 50th-newest row is still in the window");
        cut.Markup.Should().NotContain("/executions/exec-013", "older rows fall outside the 50-cap");
        // The section header keeps the FULL count, not the rendered count.
        cut.Markup.Should().Contain($"Executions ({64})");
    }

    [Fact]
    public void Executions_FetchWithinCap_RendersAllRows_NoNotice()
    {
        var cut = RenderRun("br-fits", RowsEnvelope("br-fits", 12), out _);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(12));
        // All rows fit ⇒ no "showing the 50 most recent" notice and no deep link.
        cut.Markup.Should().NotContain("most recent executions");
        cut.Markup.Should().NotContain("View all in Executions");
        TopRowExecutionId(cut).Should().Be("exec-011", "even within the cap the window is newest-first");
    }

    [Fact]
    public void Executions_FetchBeyondCap_RendersViewAllLink_WithBatchIdQuery()
    {
        var cut = RenderRun("br link/special", RowsEnvelope("br link/special", 60), out _);

        cut.WaitForAssertion(() =>
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRendered));

        cut.Markup.Should().Contain("most recent executions");
        // The deep link targets the Executions query page with the batch-run id URL-encoded.
        var link = cut.Find("p.page-subtitle a");
        link.GetAttribute("href").Should()
            .Be($"/dashboard/svc/executions?batchId={Uri.EscapeDataString("br link/special")}");
        link.TextContent.Should().Contain("View all in Executions");
    }

    [Fact]
    public void Executions_LiveEventForNewExecution_RendersAtTop_StaysCapped()
    {
        // A run already at the cap. A brand-new execution arrives over the hub with the newest enqueue time:
        // it must render at the TOP, and the window must stay capped at 50.
        var cut = RenderRun("br-live", RowsEnvelope("br-live", 60), out var client);

        cut.WaitForAssertion(() => cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRendered));
        TopRowExecutionId(cut).Should().Be("exec-059", "the snapshot's newest row is on top before the event");

        // The new execution's enqueue time is later than every snapshot row (year 2027 > 2026 base).
        var newest = Exec("exec-new", "br-live", JobStatus.Running)
            with { EnqueuedAtUtc = new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero) };
        cut.InvokeAsync(() => client.ExecutionStateChanged += Raise.Event<Func<JobExecution, Task>>(newest));

        cut.WaitForAssertion(() =>
        {
            TopRowExecutionId(cut).Should().Be("exec-new", "the newest live execution renders at the top");
            cut.FindComponents<LiveExecutionRow>().Count.Should().Be(MaxRendered,
                "the window stays capped at 50 after the append");
            // The full count (header) reflects the appended row.
            cut.Markup.Should().Contain($"Executions ({61})");
        });
    }
}
