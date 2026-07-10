using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using UKBatch.Api.Common;
using UKBatch.Api.Jobs;
using UKBatch.Dashboard.Clients;
using UKBatch.Dashboard.Components.Pages.Batches;
using UKBatch.Dashboard.Components.Shared.Editor;
using UKBatch.Dashboard.Models.Editor;
using UKBatch.Dashboard.Tests.Pages.Common;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Bunit tests for the visual <see cref="Editor"/> orchestrator,
/// focusing on the graceful-degradation wiring (canvas → <c>JsUnavailable</c> → fallback banner).
/// </summary>
/// <remarks>
/// <para>BUNIT LIMITATION (documented in <see cref="DrawflowCanvasTests"/>): bunit cannot make the
/// <c>import</c>/<c>init</c> call (both return <c>IJSObjectReference</c>) throw a <c>JSException</c>
/// it raises <c>"Use one of the SetupModule methods instead"</c>. So we cannot drive the literal
/// import-failure path here. Instead we exercise the SAME wiring it triggers: invoking the child
/// <see cref="DrawflowCanvas"/>'s public <c>JsUnavailable</c> EventCallback (the exact callback the
/// real import-failure raises) and asserting the Editor swaps the canvas for its fallback banner. The
/// literal asset-404 path is the manual smoke step 10.</para>
/// </remarks>
public sealed class EditorTests : TestContext
{
    public EditorTests()
    {
        // The DrawflowCanvas imports dag-editor.js in OnAfterRender; Loose mode returns defaults.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private const string Svc = "svc";

    private IUKBatchClient WireDeps()
    {
        var registry = PageTestHelpers.RegistryWith(PageTestHelpers.Descriptor(Svc));
        var client = PageTestHelpers.BuildClient();
        client.ListJobsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<bool?>(), Arg.Any<CancellationToken>())
            .Returns(new PageEnvelope<JobDefinitionDto>
            {
                Items =
                [
                    new JobDefinitionDto
                    {
                        Name = "JobA", IsPartitioned = false, MaxRetries = 0, TimeoutSeconds = 0,
                        DefaultParameters = new Dictionary<string, object?>(), Tags = [],
                    },
                ],
                TotalCount = 1, Offset = 0, Limit = 500,
            });
        var factory = PageTestHelpers.FactoryFor(Svc, client);
        Services.AddSingleton(registry);
        Services.AddSingleton(factory);
        Services.AddSingleton(PageTestHelpers.NewState());
        Services.AddSingleton(PageTestHelpers.NewNotifications());
        return client;
    }

    private IRenderedComponent<Editor> RenderCreate()
        => RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, (string?)null));

    // ── happy path — the canvas renders, no fallback ───────────────────────

    [Fact]
    public void CreateMode_RendersCanvasAndPalette_NoFallback()
    {
        WireDeps();
        var cut = RenderCreate();

        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0
                            || cut.FindAll("div.dag-ed-fallback").Count > 0);

        cut.FindAll("div.dag-ed-canvas").Should().NotBeEmpty(
            "with a working (loose) runtime the canvas renders — no degrade");
        cut.FindAll("div.dag-ed-fallback").Should().BeEmpty();
        cut.FindAll("div.dag-ed-palette").Should().NotBeEmpty("the palette is always present");
        // The order-rail is present and empty for a fresh batch.
        cut.Find("div.dag-ed-rail").Should().NotBeNull();
    }

    // ── canvas raises JsUnavailable → Editor renders the fallback banner ────

    [Fact]
    public async Task WhenCanvasUnavailable_RendersFallbackBannerWithWizardLink()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Invoke the SAME callback the real import-failure raises (the only bunit-feasible trigger).
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.JsUnavailable.InvokeAsync());

        cut.FindAll("div.dag-ed-fallback").Should().NotBeEmpty(
 "JsUnavailable must swap the canvas for the fallback banner (graceful degradation)");
        cut.FindAll("div.dag-ed-canvas").Should().BeEmpty("the canvas is hidden once the editor is unavailable");

        // The fallback offers the form-wizard escape hatch at the CREATE url (new).
        var link = cut.FindAll("a.btn--primary")
            .FirstOrDefault(a => a.TextContent.Contains("form wizard", StringComparison.OrdinalIgnoreCase));
        link.Should().NotBeNull("the fallback links to the form wizard");
        link!.GetAttribute("href").Should().Be($"/dashboard/{Svc}/batches/new",
            "create-mode wizard url is /new");
    }

    // ── edit-mode fallback links to the wizard's /{id}/edit url ─────────────

    [Fact]
    public async Task EditMode_FallbackWizardLink_PointsToEditUrl()
    {
        var client = WireDeps();
        var existing = new BatchDefinitionDto
        {
            Id = "edit-id", Name = "edit-batch", Source = BatchSource.Dashboard, Version = 4,
            Steps =
            [
                new BatchStep
                {
                    StepId = "s1", Order = 0, StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "JobA" },
                },
            ],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        client.GetBatchByIdAsync("edit-id", Arg.Any<CancellationToken>()).Returns(existing);

        var cut = RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, "edit-id"));
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.JsUnavailable.InvokeAsync());

        var link = cut.FindAll("a.btn--primary")
            .First(a => a.TextContent.Contains("form wizard", StringComparison.OrdinalIgnoreCase));
        link.GetAttribute("href").Should().Be($"/dashboard/{Svc}/batches/edit-id/edit",
            "edit-mode wizard url carries the batch id + /edit");
    }

    // ── Code-source batch redirects (parity with Wizard) ────────────────

    [Fact]
    public void EditMode_CodeSource_RedirectsToDetail()
    {
        var client = WireDeps();
        var codeBatch = new BatchDefinitionDto
        {
            Id = "code-id", Name = "code-batch", Source = BatchSource.Code, Version = 0,
            Steps = [],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        client.GetBatchByIdAsync("code-id", Arg.Any<CancellationToken>()).Returns(codeBatch);

        RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, "code-id"));

        var nav = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        nav.Uri.Should().Contain($"/dashboard/{Svc}/batches/code-id",
 " parity: Code-source batches redirect from the visual editor to read-only Detail");
        nav.Uri.Should().NotContain("/editor", "redirect target is Detail, not the editor route");
    }

    // ── Round-2: 2-column layout (no always-on inspector panel) ───────────────────

    [Fact]
    public void Layout_HasNoAlwaysOnInspectorPanel()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // The old always-on right-side inspector panel is gone (editing moved to a modal); nothing is
        // rendered until a node is selected.
        cut.FindAll("div.dag-ed-inspector").Should().BeEmpty(
            "node editing moved into a modal — there is no always-on inspector panel");
        // The modal is not shown for a fresh batch (no selection yet).
        cut.FindAll("div.dag-ed-modal").Should().BeEmpty("the edit modal is closed until a node is dropped/clicked");
        cut.FindAll("div.modal-overlay").Should().BeEmpty();
    }

    // ── Round-2: drop / canvas-click / rail-click open the edit modal ──────────────

    [Fact]
    public async Task Drop_OpensEditModalForTheFreshNode()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Invoke the SAME callback the JS drop raises (step 1 of the two-step add).
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));

        cut.FindAll("div.dag-ed-modal").Should().NotBeEmpty(
            "a palette drop mints the draft AND opens the modal to configure it");
        // The modal hosts the shared StepDraftEditor (drift-proof with the Wizard).
        cut.FindComponent<StepDraftEditor>().Should().NotBeNull(
            "the modal body hosts the shared StepDraftEditor");
        // And a rail chip now exists for the dropped step.
        cut.FindAll("div.dag-ed-rail__chip").Count.Should().Be(1, "the drop added one top-level step");
    }

    [Fact]
    public async Task CanvasNodeClick_SelectsButDoesNotOpenModal()
    {
        // n8n decouple move/edit (round-3): the node BODY is the drag handle. A canvas node-body click
        // (OnNodeSelected) must SELECT ONLY — it must NOT open the edit modal (otherwise clicking to
        // drag would pop the modal). Editing is the separate hover-Edit-button gesture (see below).
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Seed a node via a drop, then close the modal so we start from a clean (no-modal) state.
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));
        var stepId = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes.Single().StepId;
        await cut.InvokeAsync(() => cut.FindComponent<StepEditorModal>().Instance.OnClose.InvokeAsync());
        cut.FindAll("div.dag-ed-modal").Should().BeEmpty("closing the modal hides it");

        // A canvas node-body click (OnNodeSelected) selects + highlights, but does NOT open the modal.
        await cut.InvokeAsync(() => canvas.Instance.OnNodeSelectedCb.InvokeAsync(stepId));

        cut.FindAll("div.dag-ed-modal").Should().BeEmpty(
            "a node-body click only selects (the body is the drag handle) — it must NOT open the modal");
    }

    [Fact]
    public async Task EditButton_OpensModal()
    {
        // The hover Edit button on the node (OnNodeEditRequested) is the EXPLICIT edit gesture
        // decoupled from the node-body drag/select. Clicking it opens the edit modal for that step.
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Seed a node via a drop, then close the modal so we can re-open it via the Edit button.
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));
        var stepId = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes.Single().StepId;
        await cut.InvokeAsync(() => cut.FindComponent<StepEditorModal>().Instance.OnClose.InvokeAsync());
        cut.FindAll("div.dag-ed-modal").Should().BeEmpty("closing the modal hides it");

        // Invoke the SAME callback the JS hover-Edit-button click raises.
        await cut.InvokeAsync(() => canvas.Instance.OnNodeEditRequestedCb.InvokeAsync(stepId));

        cut.FindAll("div.dag-ed-modal").Should().NotBeEmpty(
            "the hover Edit button opens the edit modal for that step");
        cut.FindComponent<StepDraftEditor>().Should().NotBeNull(
            "the modal body hosts the shared StepDraftEditor");
    }

    [Fact]
    public async Task RailChipClick_OpensEditModal()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Seed a node, close the modal, then click its rail chip → re-opens the modal.
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));
        await cut.InvokeAsync(() => cut.FindComponent<StepEditorModal>().Instance.OnClose.InvokeAsync());

        cut.Find("div.dag-ed-rail__chip").Click();

        cut.FindAll("div.dag-ed-modal").Should().NotBeEmpty(
            "a rail-chip click opens the SAME edit modal for that step");
    }

    [Fact]
    public async Task ModalClose_HidesModal()
    {
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.ApprovalGate, 12, 8)));
        cut.FindAll("div.dag-ed-modal").Should().NotBeEmpty();

        // The Done/X/backdrop all route through OnClose.
        await cut.InvokeAsync(() => cut.FindComponent<StepEditorModal>().Instance.OnClose.InvokeAsync());

        cut.FindAll("div.dag-ed-modal").Should().BeEmpty("Done/X/backdrop dismiss the modal");
        cut.FindAll("div.modal-overlay").Should().BeEmpty();
    }

    // ── Round-2: LEFT-TO-RIGHT auto-layout (hint-less nodes flow horizontally) ─────

    [Fact]
    public void HintlessNodes_LaidOutLeftToRight_SameRowIncreasingX()
    {
        // A Wizard-created batch (no layout hints) must open as a horizontal chain: same Y, X increasing
        // by a fixed stride — so the output→input edges are short forward curves, not loopy vertical sweeps.
        var client = WireDeps();
        var hintless = new BatchDefinitionDto
        {
            Id = "wiz-id", Name = "wizard-made", Source = BatchSource.Dashboard, Version = 2,
            Steps =
            [
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
                new BatchStep { StepId = "s2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
                new BatchStep { StepId = "s3", Order = 2, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
            ],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Metadata = null,
        };
        client.GetBatchByIdAsync("wiz-id", Arg.Any<CancellationToken>()).Returns(hintless);

        var cut = RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, "wiz-id"));
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var nodes = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes
            .OrderBy(n => n.OrderBadge, StringComparer.Ordinal).ToList();
        nodes.Should().HaveCount(3);

        // All on the same row (constant Y).
        nodes.Select(n => n.Y).Distinct().Should().ContainSingle("hint-less nodes share one row (constant Y)");

        // Strictly increasing X by a constant stride (left-to-right).
        var xs = nodes.Select(n => n.X).ToList();
        xs[1].Should().BeGreaterThan(xs[0], "node 2 is to the RIGHT of node 1");
        xs[2].Should().BeGreaterThan(xs[1], "node 3 is to the RIGHT of node 2");
        (xs[1] - xs[0]).Should().Be(xs[2] - xs[1], "the LTR stride is constant (NodeWidth + GapX)");
    }

    [Fact]
    public async Task DraggedNode_KeepsHintPosition_NotLtrFormula()
    {
        // A node WITH a saved hint (operator-dragged) keeps its hint position — the LTR formula applies
        // ONLY to hint-less nodes. We drop a node (origin gesture → snaps to LTR start), simulate a drag
        // (OnNodeMoved → records the hint, but does NOT rebuild the cached graph — geometry-only, by
        // design: the canvas already moved the node visually), then drop a SECOND node which DOES trigger
        // a structural RebuildGraph. After the rebuild the first node must read from its hint (not be
        // re-snapped to the LTR formula) — that's the hint-wins invariant.
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 5, 5)));
        var firstId = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes.Single().StepId;

        // Drag-settle the first node to (777, 555) — records the hint.
        await cut.InvokeAsync(() => canvas.Instance.OnNodeMovedCb.InvokeAsync(new NodeMovedArgs(firstId, 777, 555)));

        // A second drop forces a structural RebuildGraph (the path that re-runs ToNodeSpec for all nodes).
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 5, 5)));

        var node = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes.Single(n => n.StepId == firstId);
        node.X.Should().Be(777, "a dragged node keeps its operator-set X (hint wins over the LTR formula)");
        node.Y.Should().Be(555, "a dragged node keeps its operator-set Y");
    }

    // ── ParallelGroup in-card branches: the node spec carries its child job labels ─────

    [Fact]
    public void ParallelGroupNode_CarriesChildJobNames_JobNodeCarriesNull()
    {
        // The ParallelGroup node now renders its child jobs inside the card (dag-editor.js branchesHtml).
        // bunit cannot render the Drawflow-injected DOM, so we assert at the spec boundary: ToNodeSpec
        // (via the public Graph) populates Children with the FULL child job names for a group, and leaves
        // Children null for a Job node (which has no branches container).
        var client = WireDeps();
        var existing = new BatchDefinitionDto
        {
            Id = "grp-id", Name = "grp-batch", Source = BatchSource.Dashboard, Version = 3,
            Steps =
            [
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "JobA" } },
                new BatchStep
                {
                    StepId = "g1", Order = 1, StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps =
                        [
                            new BatchStep { StepId = "c1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "Sample.Jobs.AlphaJob" } },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "BetaJob" } },
                        ],
                    },
                },
            ],
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };
        client.GetBatchByIdAsync("grp-id", Arg.Any<CancellationToken>()).Returns(existing);

        var cut = RenderComponent<Editor>(p => p
            .Add(e => e.ServiceName, Svc)
            .Add(e => e.BatchId, "grp-id"));
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var nodes = cut.FindComponent<DrawflowCanvas>().Instance.Graph.Nodes;

        var jobNode = nodes.Single(n => n.StepId == "s1");
        jobNode.Children.Should().BeNull("a Job node has no branches container — Children is null");

        var groupNode = nodes.Single(n => n.StepId == "g1");
        groupNode.Children.Should().NotBeNull("a ParallelGroup node carries its child job labels");
        groupNode.Children!.Should().ContainInOrder("Sample.Jobs.AlphaJob", "BetaJob")
            .And.HaveCount(2,
            "Children carries the FULL child job names in order (dag-editor.js displayTitle shortens for the chip)");
        groupNode.Subtitle.Should().Be("Parallel · WaitAll",
            "a ParallelGroup node gets a 'Parallel · {JoinPolicy}' subheading for the branches block");
    }

    // ── onFailure-canvas Bucket 5 — compensation palette tile + 2nd order rail ────

    [Fact]
    public void Palette_RendersFourthCompensationTile()
    {
        // The DagPalette now has a 4th "Compensation" tile (onFailure lane). bunit CAN render the static
        // palette markup (it is plain HTML, no JSInterop), so we assert the tile + its label are present.
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        cut.FindAll("div.dag-ed-palette__tile").Count.Should().Be(4,
            "the palette has FOUR draggable tiles: Job, Parallel group, Approval gate, Compensation");
        cut.FindAll("div.dag-ed-palette__tile--failure").Should().NotBeEmpty(
            "the 4th tile is the failure-chain (onFailure) tile with the --failure modifier");
        cut.Find("div.dag-ed-palette").TextContent.Should().Contain("Failure chain",
            "the tile is labelled 'Failure chain' — it appends to the batch-level OnFailure chain, "
            + "deliberately distinct from the per-step compensator edited in a step's own dialog");
    }

    [Fact]
    public async Task CompensationDrop_RendersSecondOrderRail_AndFlipsPolicy()
    {
        // Dropping the 'OnFailure' palette tile (IsOnFailure: true) appends to _model.OnFailureSteps and
        // — because OnFailureSteps is now non-empty — the Editor renders a SECOND DagOrderRail (the
        // compensation lane). It also flips the failure policy to Compensate (EnsureCompensatePolicy).
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        // Pre-drop: only the main-flow rail exists (OnFailureSteps empty ⇒ no 2nd rail).
        cut.FindAll("div.dag-ed-rail").Count.Should().Be(1, "no compensation steps yet ⇒ one rail only");

        // Invoke the SAME callback the JS compensation drop raises (IsOnFailure flag set).
        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8, IsOnFailure: true)));

        cut.FindAll("div.dag-ed-rail").Count.Should().Be(2,
            "a non-empty OnFailureSteps list renders the SECOND DagOrderRail (the compensation lane)");
        // The drop opened the modal for the fresh compensation node.
        cut.FindAll("div.dag-ed-modal").Should().NotBeEmpty(
            "a compensation drop mints the draft AND opens the modal to configure it");
    }

    // ── per-step compensator display node (canvas ↔ model sync) ─────────────────────

    [Fact]
    public async Task EnableCompensator_InModal_MarksTheStepRailChip()
    {
        // Enabling "Add compensator" in a step's Edit dialog attaches a compensator to that draft. The
        // per-step compensator is a property of the step (its own display node is added imperatively on
        // the canvas), and the always-rendered rail chip gains a comp marker — the observable proof the
        // model actually carries the compensator, right from the dialog (no save/reload).
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));
        cut.FindAll("span.dag-ed-rail__comp").Should().BeEmpty("a fresh step has no compensator yet");

        // Toggle the compensation editor's checkbox inside the modal.
        var toggle = cut.FindAll("div.dag-ed-modal input[type=checkbox]").Last();
        await cut.InvokeAsync(() => toggle.Change(true));

        cut.FindAll("span.dag-ed-rail__comp").Should().ContainSingle(
            "enabling the compensator marks the step's rail chip — the model now carries Compensation");
    }

    [Fact]
    public async Task DeleteCompensatorNode_DetachesCompensator_KeepsParentStep()
    {
        // Deleting the compensator DISPLAY node (its derived "{parent}:comp" id) detaches the compensator
        // from its parent step WITHOUT removing the parent — the node is a projection of the parent's
        // Compensation field, not a step of its own.
        WireDeps();
        var cut = RenderCreate();
        cut.WaitForState(() => cut.FindAll("div.dag-ed-canvas").Count > 0);

        var canvas = cut.FindComponent<DrawflowCanvas>();
        await cut.InvokeAsync(() => canvas.Instance.OnNodeDroppedCb.InvokeAsync(
            new NodeDropIntent(BatchStepType.Job, 12, 8)));
        var parentId = canvas.Instance.Graph.Nodes.Single().StepId;
        var toggle = cut.FindAll("div.dag-ed-modal input[type=checkbox]").Last();
        await cut.InvokeAsync(() => toggle.Change(true));
        cut.FindAll("span.dag-ed-rail__comp").Should().ContainSingle("precondition: compensator attached");

        // The JS raises OnNodeRemoved with the compensator's DERIVED id.
        await cut.InvokeAsync(() => canvas.Instance.OnNodeRemovedCb.InvokeAsync(
            UKBatch.Abstractions.Batches.CompensationStepIds.For(parentId)));

        cut.FindAll("span.dag-ed-rail__comp").Should().BeEmpty(
            "deleting the compensator node clears the parent's Compensation (marker gone)");
        cut.FindAll("div.dag-ed-rail__chip").Count.Should().Be(1,
            "the PARENT step must survive — only its compensator was detached");
    }
}
