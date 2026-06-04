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
}
