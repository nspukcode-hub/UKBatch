using System.Globalization;
using System.Text.RegularExpressions;
using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Bunit render/lifecycle contract for <see cref="DagStatusCanvas"/>.
/// </summary>
/// <remarks>
/// <para>BUNIT LIMITATION (mirrors <c>DagViewTests</c> + <c>DrawflowCanvasTests</c>): bunit cannot make
/// the <c>import</c>/<c>init</c> calls (both return <see cref="Microsoft.JSInterop.IJSObjectReference"/>)
/// throw a <c>JSException</c> — its <c>Setup&lt;IJSObjectReference&gt;</c> path is blocked. So the
/// literal <c>import</c>-throws → <c>_jsFailed=true</c> → fallback-<c>&lt;ul&gt;</c> path cannot be
/// reached here; that degrade is exercised by the <c>DagStatusAssetRegressionTests</c> HttpClient guard
/// (real 404) + manual smoke. The fallback list's status-binding LOGIC (the <c>&lt;li&gt;</c> class +
/// pill bound to <c>StatusByStepId</c>) is unit-tested at the <c>DagStatusClasses</c> level. Here
/// we pin the lifecycle/render behaviours bunit CAN exercise.</para>
/// </remarks>
public sealed class DagStatusCanvasTests : TestContext
{
    public DagStatusCanvasTests()
    {
        // Loose mode = the closest analogue to "module loaded fine": import/init/buildGraph/setStatuses
        // all return defaults silently, so OnAfterRenderAsync's happy path runs without crashing.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private static BatchStep Job(string id, int order, string name = "JobX", string? targetService = null) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name, TargetService = targetService },
    };

    private static Dictionary<string, JobStatus> Map(params (string, JobStatus)[] entries)
    {
        var d = new Dictionary<string, JobStatus>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    // ── render: canvas vs EmptyState ─────────────────────────────────────────────

    [Fact]
    public void RendersCanvasContainer_WhenStepsPresent()
    {
        var steps = new[] { Job("s1", 0, "Step1") };

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        cut.FindAll("div.dag-status-canvas").Should().HaveCount(1,
            "Steps>0 ⇒ the Drawflow container div renders");
        cut.FindAll("div.empty-state").Should().BeEmpty();
        // Toolbar zoom buttons are present (discrete @onclick — allowed).
        cut.Find("button[aria-label='Zoom in']").Should().NotBeNull();
        cut.Find("button[aria-label='Reset view']").Should().NotBeNull();
    }

    [Fact]
    public void RendersEmptyState_WhenNoSteps()
    {
        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, Array.Empty<BatchStep>())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        cut.FindAll("div.empty-state").Should().HaveCount(1, "Steps=0 ⇒ EmptyState renders");
        cut.Markup.Should().Contain("No steps");
        cut.FindAll("div.dag-status-canvas").Should().BeEmpty("no canvas container when there are no steps");
    }

    // ── fresh-run lifecycle — canvas first-render ≠ component first-render ──

    [Fact]
    public void FreshRun_StepsEmptyThenPushed_TransitionsFromEmptyStateToCanvas()
    {
        // A fresh run mounts with Steps=[] ⇒ EmptyState (the @ref div is NOT in the DOM). When the first
        // hub event loads the definition (Steps>0), the canvas container appears — the render on which
        // JS-init + the first buildGraph fire. bunit can't count the JS invocation through the
        // loose IJSObjectReference controller, but it CAN observe the render transition that gates it.
        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, Array.Empty<BatchStep>())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        cut.FindAll("div.dag-status-canvas").Should().BeEmpty("fresh run: EmptyState first, no canvas div yet");

        // First hub event arrives → definition loaded → Steps>0.
        cut.SetParametersAndRender(p => p.Add(d => d.Steps, new[] { Job("s1", 0, "Loaded") }));

        cut.FindAll("div.dag-status-canvas").Should().HaveCount(1,
 "the canvas container appears on the LATER render once the definition arrives (init/buildGraph fire here, not on component first render)");
    }

    [Fact]
    public void TopologyChange_DoesNotCrash_UnderLooseRuntime()
    {
        // OnParametersSet recomputes the layout on a topology change and marks _topologyDirty; the
        // OnAfterRenderAsync ladder must run buildGraph without throwing under the loose runtime.
        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, new[] { Job("s1", 0, "A") })
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        var act = () => cut.SetParametersAndRender(p => p
            .Add(d => d.Steps, new[] { Job("s1", 0, "A"), Job("s2", 1, "B") }));

        act.Should().NotThrow("a topology change re-runs the guarded buildGraph ladder without crashing");
    }

    // ── selection: the [JSInvokable] → OnNodeSelected EventCallback seam ──────────

    [Fact]
    public async Task OnNodeSelectedFromJs_RaisesOnNodeSelected_WithResolvedStep()
    {
        // The ONLY JS→C# callback. The delegated container click in dag-status.js invokes
        // OnNodeSelectedFromJs(stepId); the component resolves the BatchStep and raises OnNodeSelected.
        // We invoke the JSInvokable directly (the JS bridge is not loaded in bunit) and assert the
        // EventCallback fires with the right step.
        var steps = new[] { Job("s1", 0, "A"), Job("s2", 1, "B") };
        BatchStep? selected = null;

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        await cut.InvokeAsync(() => cut.Instance.OnNodeSelectedFromJs("s2"));

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("s2", "OnNodeSelectedFromJs resolves the StepId to its BatchStep and raises OnNodeSelected");
    }

    [Fact]
    public async Task OnNodeSelectedFromJs_ResolvesParallelChild()
    {
        // FindStep must also resolve a parallel-group CHILD (selection inside a group).
        var children = new[] { Job("c1", 0, "Child1"), Job("c2", 1, "Child2") };
        var steps = new[]
        {
            Job("up", 0, "Up"),
            new BatchStep
            {
                StepId = "pg", Order = 1, StepType = BatchStepType.ParallelGroup,
                ParallelGroup = new ParallelGroupData { Steps = children.ToList(), JoinPolicy = ParallelJoinPolicy.WaitAll },
            },
        };
        BatchStep? selected = null;

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        await cut.InvokeAsync(() => cut.Instance.OnNodeSelectedFromJs("c2"));

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("c2", "selection resolves a parallel-group child by StepId");
    }

    [Fact]
    public async Task OnNodeSelectedFromJs_UnknownStepId_DoesNotRaise()
    {
        var steps = new[] { Job("s1", 0, "A") };
        var raised = false;

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, _ => { raised = true; }));

        await cut.InvokeAsync(() => cut.Instance.OnNodeSelectedFromJs("does-not-exist"));

        raised.Should().BeFalse("an unresolved StepId must NOT raise OnNodeSelected (guarded null path)");
    }

    // ── in-node approve callback seam (OnApproveClickedFromJs → OnApproveClicked) ──

    private static readonly string[] OpsRole = { "ops" };
    private static readonly string[] GatePending = { "gate" };

    private static BatchStep Gate(string id, int order, string title = "Approve gate") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ApprovalGate,
        Approval = new ApprovalGateConfig { Title = title, AllowedRoles = OpsRole, OnTimeout = ApprovalTimeoutAction.Fail },
    };

    [Fact]
    public async Task OnApproveClickedFromJs_RaisesOnApproveClicked_WithResolvedGate()
    {
        // The SECOND discrete JS→C# callback. The delegated container click checks the
        // .dag-st-approve button BEFORE the generic node-select branch and invokes OnApproveClickedFromJs;
        // the component resolves the BatchStep and raises OnApproveClicked. We invoke the JSInvokable
        // directly (the JS bridge is not loaded in bunit) — the real resolution path, not a faked one.
        var steps = new[] { Job("s1", 0, "A"), Gate("gate", 1) };
        BatchStep? approved = null;

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnApproveClicked, s => { approved = s; }));

        await cut.InvokeAsync(() => cut.Instance.OnApproveClickedFromJs("gate"));

        approved.Should().NotBeNull();
        approved!.StepId.Should().Be("gate", "OnApproveClickedFromJs resolves the StepId and raises OnApproveClicked");
    }

    [Fact]
    public async Task OnApproveClickedFromJs_UnknownStepId_DoesNotRaise()
    {
        var steps = new[] { Gate("gate", 0) };
        var raised = false;

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnApproveClicked, _ => { raised = true; }));

        await cut.InvokeAsync(() => cut.Instance.OnApproveClickedFromJs("nope"));

        raised.Should().BeFalse("an unresolved StepId must NOT raise OnApproveClicked (guarded null path)");
    }

    [Fact]
    public void PendingStepIds_Change_DoesNotCrash_UnderLooseRuntime()
    {
        // PendingStepIds flows to JS via setPending on the next render (the gate's in-node Approve flag).
        // RunDetail rebuilds the pending HashSet each refresh, so the component diffs an order-insensitive
        // signature and pushes only on a real change. Under the loose runtime this push must not throw.
        var steps = new[] { Job("s1", 0, "A"), Gate("gate", 1) };

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, Map(("gate", JobStatus.AwaitingApproval)))
            .Add<IReadOnlyCollection<string>>(d => d.PendingStepIds, GatePending));

        // Gate resolves → pending set empties → setPending pushes the cleared set.
        var act = () => cut.SetParametersAndRender(p => p
            .Add(d => d.StatusByStepId, Map(("gate", JobStatus.Completed)))
            .Add<IReadOnlyCollection<string>>(d => d.PendingStepIds, Array.Empty<string>()));

        act.Should().NotThrow("a pending-set change pushes setPending on the next render without crashing");
    }

    // ── status update across renders does not crash (live mode) ──────────────────

    [Fact]
    public void StatusUpdateAcrossRenders_DoesNotCrash_UnderLooseRuntime()
    {
        // analogue at the canvas level: a status-only change (same Steps reference) marks
        // _statusDirty and pushes setStatuses on the next render. Under the loose runtime this must run
        // without throwing. (The actual live-status-in-degraded-fallback binding is unit-tested via
        // DagStatusClasses — see the bunit-limitation note in this class's <remarks>.)
        var steps = new[] { Job("s1", 0, "A"), Job("s2", 1, "B") };

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, Map(("s1", JobStatus.Running))));

        var act = () => cut.SetParametersAndRender(p => p
            .Add(d => d.StatusByStepId, Map(("s1", JobStatus.Completed), ("s2", JobStatus.Running))));

        act.Should().NotThrow("a status-only update pushes setStatuses on the next render without crashing");
    }

    // ── InvariantCulture: no comma-decimal corruption leaks into rendered markup ──

    [Fact]
    public void Renders_UnderTurkishCulture_NoCommaDecimalInMarkup()
    {
        // Coordinates cross C#→JS as JSON doubles (culture-invariant), and the rendered
        // Blazor markup carries the static toolbar + container only (the canvas DOM is JS-built, not in
        // the bunit markup). Assert no tr-TR `12,5`-style comma-decimal leaks into the emitted markup
        // a regression that stringified a coordinate with the ambient culture would surface here.
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = tr;
            CultureInfo.CurrentUICulture = tr;

            var steps = new[] { Job("s1", 0, "Step1"), Job("s2", 1, "Step2") };

            var cut = RenderComponent<DagStatusCanvas>(p => p
                .Add(d => d.Steps, steps)
                .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
                .Add(d => d.StatusByStepId, Map(("s1", JobStatus.Running))));

            // A comma-decimal is `digit,digit` (e.g. `12,5`); none may appear in the rendered markup.
            Regex.IsMatch(cut.Markup, @"\d,\d").Should().BeFalse(
                $"R-1: no culture-sensitive comma-decimal may leak into the rendered markup under tr-TR. Markup: '{cut.Markup}'");
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    [Fact]
    public void Job_WithTargetService_LayoutCarriesTargetService_ForCloudBadge()
    {
        // cross-service: a Job with a TargetService must flow into the graph spec (the JS builds the
        // cloud badge from it). bunit can't render the JS-built node, but we lock that the component
        // mounts a cross-service step without crashing and the canvas container appears.
        var steps = new[] { Job("s1", 0, "Remote", targetService: "worker-svc") };

        var cut = RenderComponent<DagStatusCanvas>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, Map(("s1", JobStatus.Running))));

        cut.FindAll("div.dag-status-canvas").Should().HaveCount(1);
    }
}
