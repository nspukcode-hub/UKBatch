using System.Globalization;
using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// bunit render assertions for <see cref="DagView"/>.
/// Inputs: <c>Steps</c> / <c>OnFailureSteps</c> / <c>StatusByStepId</c> / <c>SelectedStepId</c>.
/// Outputs: DOM markup (foreignObject node count, edge classes, status classes, click → selection).
/// </summary>
public sealed class DagViewTests : TestContext
{
    public DagViewTests()
    {
        // graceful degradation: the production component catches JSException when the module
        // import fails. bunit's STRICT mode raises JSRuntimeUnhandledInvocationException for any
        // un-set-up call — which is NOT a JSException and would crash the test. Loose mode returns
        // defaults silently, which is the closest analogue to "module 404'd" in the real browser.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static BatchStep Job(string id, int order, string name = "JobX", string? targetService = null) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name, TargetService = targetService },
    };

    private static BatchStep Approval(string id, int order, string title = "Approve") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ApprovalGate,
        Approval = new ApprovalGateConfig
        {
            Title = title,
            AllowedRoles = new[] { "ops" },
            OnTimeout = ApprovalTimeoutAction.Fail,
        },
    };

    private static BatchStep Parallel(string id, int order, IEnumerable<BatchStep> children,
        ParallelJoinPolicy join = ParallelJoinPolicy.WaitAll) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ParallelGroup,
        ParallelGroup = new ParallelGroupData
        {
            Steps = children.ToList(),
            JoinPolicy = join,
        },
    };

    // ── sequential render ──────────────────────────────────────────────────

    [Fact]
    public void Sequential_RendersNodesAndEdges()
    {
        var steps = new[]
        {
            Job("s1", 0, "Step1"),
            Job("s2", 1, "Step2"),
            Job("s3", 2, "Step3"),
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // 3 foreignObject nodes.
        var nodes = cut.FindAll("foreignObject");
        nodes.Should().HaveCount(3);
        cut.Markup.Should().Contain("Step1").And.Contain("Step2").And.Contain("Step3");

        // 2 edges, all sequential (NO --parallel / --on-failure modifier classes).
        var edges = cut.FindAll("path.dag-edge");
        edges.Should().HaveCount(2);
        foreach (var e in edges)
        {
            var cls = e.GetAttribute("class") ?? string.Empty;
            cls.Should().NotContain("dag-edge--parallel");
            cls.Should().NotContain("dag-edge--on-failure");
        }
    }

    // ── parallel fan-out + approval rectangle ──────────────────────────────

    [Fact]
    public void ParallelGroup_FansOutAndIn_RendersSixParallelEdges()
    {
        var children = new[]
        {
            Job("c1", 0, "A"),
            Job("c2", 1, "B"),
            Job("c3", 2, "C"),
        };
        var steps = new[]
        {
            Job("upstream", 0, "Upstream"),
            Parallel("pg", 1, children),
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // 1 upstream + 3 parallel children = 4 nodes (R-10 — fan-in join has no node).
        cut.FindAll("foreignObject").Should().HaveCount(4);

        // 6 parallel edges (3 fan-out + 3 fan-in).
        var parallel = cut.FindAll("path.dag-edge--parallel");
        parallel.Should().HaveCount(6);
    }

    [Fact]
    public void ApprovalGate_RendersRectNode_WithApprovalClass()
    {
        var steps = new[] { Approval("ap1", 0, "Confirm") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // The approval node is a `dag-node dag-node--approval` rectangle (NOT a hex). Both classes
        // must be present so it inherits the job-node rectangle surface + the purple accent.
        var node = cut.Find("div.dag-node--approval");
        (node.GetAttribute("class") ?? string.Empty).Should().Contain("dag-node",
            "the approval node is a rectangle variant of dag-node, not a separate shape");
        cut.Markup.Should().Contain("Confirm");
        // The `rule` icon distinguishes the approval node from a job node.
        cut.Markup.Should().Contain("rule");
    }

    [Fact]
    public void ApprovalGate_IsRectangle_NoPolygonOrGWrapper()
    {
        // Chrome DAG-render fix (2026-06) STRUCTURAL GUARD. The original bug: a hexagon rendered as a
        // <polygon> + a NARROW (100px) <foreignObject> whose centered content mis-placed far LEFT under
        // the canvas CSS transform in Chromium, while the JOB node's full-width rectangle foreignObject
        // rendered fine. A prior fix (dropping the <g> wrapper) did NOT help — the differentiator was the
        // shape/width, not the <g>. The robust fix converges the approval node onto the EXACT job-node
        // structure: a single bare <foreignObject> (full NodeW width), NO <polygon>, NO <g>.
        //
        // bunit cannot render the transform/displacement (no layout engine) — so we lock the STRUCTURE
        // that differed: NO <polygon>, NO <g>, and the approval foreignObject is a direct <svg> child
        // exactly like the job node. A regression re-introducing the hex fails here. FINAL verification
        // remains a live Chrome smoke (transform render).
        var steps = new[] { Approval("ap1", 0, "Confirm") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // No <polygon> (the hexagon is gone) and no <g> wrapper anywhere.
        cut.FindAll("polygon").Should().BeEmpty(
            "the approval node is now a rectangle — the hex <polygon> must be gone (Chrome transform displacement bug)");
        cut.FindAll("g").Should().BeEmpty("no <g> wrapper — the foreignObject is a direct <svg> child");

        var svg = cut.Find("svg.dag-canvas__svg");
        var directChildTags = svg.Children.Select(c => c.TagName.ToLowerInvariant()).ToList();
        directChildTags.Should().Contain("foreignobject",
            "the approval node renders as a bare <foreignObject> direct <svg> child — identical to the working job node");

        // Exactly one foreignObject (the rectangle) — no separate polygon sibling.
        cut.FindAll("foreignObject").Should().HaveCount(1);
    }

    [Fact]
    public async Task ApprovalGate_NodeClick_RaisesOnNodeSelected()
    {
        // The inner div.dag-node--approval owns the click directly (it carries its own @onclick).
        // Lock that selection still fires on the rectangle conversion.
        var steps = new[] { Approval("ap1", 0, "Confirm") };
        BatchStep? selected = null;

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        await cut.Find("div.dag-node--approval").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("ap1", "clicking the approval node must surface its BatchStep");
    }

    [Fact]
    public void ApprovalGate_LiveStatus_AppliesStatusClass_OnRectangle()
    {
        // Status coloring must still work on the approval rectangle: an AwaitingApproval gate maps to
        // dag-node--running (StatusClass), so it gets BOTH dag-node--approval (purple identity) and the
        // live status modifier. Locks that the rect conversion didn't drop status styling.
        var steps = new[] { Approval("ap1", 0, "Confirm") };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["ap1"] = JobStatus.AwaitingApproval,
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        var node = cut.Find("div.dag-node--approval");
        var cls = node.GetAttribute("class") ?? string.Empty;
        cls.Should().Contain("dag-node--running",
            "AwaitingApproval maps to the running status class — coloring survives the hex→rect conversion");
    }

    // ── live status class, OnFailure dashed branch, click → OnNodeSelected ─

    [Fact]
    public void LiveStatus_AppliesRunningClass_ToMatchingNode()
    {
        var steps = new[]
        {
            Job("s1", 0, "Step1"),
            Job("s2", 1, "Step2"),
        };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["s1"] = JobStatus.Running,
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        // Running ⇒.dag-node--running class on the s1 node.
        cut.FindAll("div.dag-node--running").Should().HaveCount(1);

        // s2 has no entry — must be muted, not Running (not-started node = no badge / muted).
        cut.FindAll("div.dag-node--muted").Should().HaveCount(1);
    }

    [Fact]
    public void OnFailureSteps_RenderDashedBranchEdges()
    {
        var spine = new[] { Job("s1", 0, "Main") };
        var onFailure = new[] { Job("f1", 0, "Rollback") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, spine)
            .Add(d => d.OnFailureSteps, onFailure));

        cut.FindAll("path.dag-edge--on-failure").Should().NotBeEmpty(
            "OnFailureSteps render as dashed `dag-edge--on-failure` connectors");
    }

    [Fact]
    public async Task NodeClick_RaisesOnNodeSelected_WithCorrectStep()
    {
        var steps = new[]
        {
            Job("s1", 0, "Step1"),
            Job("s2", 1, "Step2"),
        };
        BatchStep? selected = null;

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        // The 1st rendered foreignObject contains s1. Click its inner div.dag-node.
        var nodeDivs = cut.FindAll("div.dag-node");
        nodeDivs.Should().NotBeEmpty();
        await nodeDivs[0].ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("s1", "click on the first node must surface that BatchStep to OnNodeSelected");
    }

    // ── empty / single / no-JS graceful degradation ────────────────────────

    [Fact]
    public void EmptySteps_RendersEmptyState_NoSvg()
    {
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, Array.Empty<BatchStep>())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // EmptyState surfaces the "No steps" copy.
        cut.Markup.Should().Contain("No steps");
        cut.FindAll("svg.dag-canvas__svg").Should().BeEmpty();
    }

    [Fact]
    public void SingleStep_RendersOneNode_NoEdges()
    {
        var steps = new[] { Job("only", 0, "OnlyJob") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        cut.FindAll("foreignObject").Should().HaveCount(1);
        cut.FindAll("path.dag-edge").Should().BeEmpty();
    }

    [Fact]
    public void Renders_WithoutRealJsRuntime_ZoomButtonsMutateTransformStyle()
    {
        // graceful degradation: bunit's JSInterop is mocked — no real JS module loads. The
        // toolbar +/-/Reset buttons MUST still work via C# state + the inline transform.
        var steps = new[] { Job("s1", 0, "Step") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // No exception was thrown by OnAfterRenderAsync — the JSException catch swallowed it.
        cut.Find("svg.dag-canvas__svg").Should().NotBeNull();

        // Default zoom = 100% — toolbar % indicator surfaces it.
        cut.Markup.Should().Contain("100%");

        // Click "Zoom in" — _zoom becomes 1.1, the transform style updates and the % indicator ticks.
        var zoomIn = cut.Find("button[aria-label='Zoom in']");
        zoomIn.Click();

        cut.Markup.Should().Contain("110%");
        // The transform must use a `.` decimal (InvariantCulture): scale(1.1).
        cut.Markup.Should().Contain("scale(1.1)");
    }

    [Fact]
    public void Renders_UnderTurkishCulture_EmitsInvariantDecimalsForSvgCoordinates()
    {
        // contract: tr-TR formats decimals as `12,5` by default — SVG MUST use `.` regardless.
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            var tr = CultureInfo.GetCultureInfo("tr-TR");
            CultureInfo.CurrentCulture = tr;
            CultureInfo.CurrentUICulture = tr;

            var steps = new[] { Job("s1", 0, "Step1") };

            var cut = RenderComponent<DagView>(p => p
                .Add(d => d.Steps, steps)
                .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

            // No comma DECIMALS — distinct from CSS argument separators like `translate(0px, 0px)`.
            // A comma-decimal is `digit,digit` (e.g. `12,5`); we assert no such pattern appears.
            var svgEl = cut.Find("svg.dag-canvas__svg");
            var style = svgEl.GetAttribute("style") ?? string.Empty;
            System.Text.RegularExpressions.Regex.IsMatch(style, @"\d,\d").Should().BeFalse(
                $"R-1: SVG transform MUST use InvariantCulture decimals (no tr-TR `12,5`). Got: '{style}'");

            // viewBox is space-separated coordinates — must use `.` decimals exclusively.
            var viewBox = svgEl.GetAttribute("viewBox") ?? string.Empty;
            System.Text.RegularExpressions.Regex.IsMatch(viewBox, @"\d,\d").Should().BeFalse(
                $"R-1: viewBox MUST use InvariantCulture decimals. Got: '{viewBox}'");

            // Edge path `d` attribute is also coord-heavy — verify the same invariant.
            var edges = cut.FindAll("path.dag-edge");
            if (edges.Count > 0)
            {
                var d = edges[0].GetAttribute("d") ?? string.Empty;
                System.Text.RegularExpressions.Regex.IsMatch(d, @"\d,\d").Should().BeFalse(
                    $"R-1: path `d` MUST use InvariantCulture decimals. Got: '{d}'");
            }
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
        }
    }

    // ── contract: not-started node has no badge ────────────────────────────

    [Fact]
    public void LiveMode_NodeWithoutStatusEntry_RendersWithoutBadge()
    {
        // when StatusByStepId is non-null (live mode) but the node has NO entry yet, the
        // badge is omitted and the node renders muted. A regression would paint `Scheduled` (rank 0)
        // by accidentally calling `default(JobStatus)`.
        var steps = new[] { Job("s1", 0, "NotYet") };
        var emptyMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal);

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, emptyMap));

        // Muted class applied (not-started).
        cut.FindAll("div.dag-node--muted").Should().HaveCount(1);

        // No JobStatusBadge for this node — `status-badge` is the badge's outer class.
        cut.FindAll("span.status-badge").Should().BeEmpty(
 "a node with no StatusByStepId entry MUST NOT paint default(JobStatus) as a badge");
    }

    // ── cross-service badge ───────────────────────────────────────────────────

    [Fact]
    public void Job_WithTargetService_RendersCloudBadge()
    {
        var steps = new[] { Job("s1", 0, "Remote", targetService: "worker-svc") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        cut.FindAll("span.dag-node__badge").Should().HaveCount(1);
        cut.Markup.Should().Contain("worker-svc");
    }

    // ── SelectedStepId drives --selected class ──────────────────────────────────

    [Fact]
    public void SelectedStepId_AppliesSelectedClassToMatchingNode()
    {
        var steps = new[] { Job("s1", 0, "A"), Job("s2", 1, "B") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.SelectedStepId, "s2"));

        cut.FindAll("div.dag-node--selected").Should().HaveCount(1,
            "exactly the node whose StepId matches SelectedStepId carries the --selected modifier");
    }

    // ── #12: edge status coloring (destination-only) ─────

    [Fact]
    public void LiveMode_SequentialEdge_GetsDestinationStatusClass()
    {
        // the s1→s2 edge colors by its DESTINATION (s2). s2 Running ⇒ dag-edge--running.
        var steps = new[] { Job("s1", 0, "A"), Job("s2", 1, "B") };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["s1"] = JobStatus.Completed,
            ["s2"] = JobStatus.Running,
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        cut.FindAll("path.dag-edge--running").Should().HaveCount(1,
            "the single sequential edge colors by its destination (s2 = Running)");
    }

    [Fact]
    public void LiveMode_EdgeIntoNotStartedNode_HasNoStatusClass()
    {
        // destination-ONLY (no source fallback for normal edges): s1 Completed but s2 not started
        // (no map entry) ⇒ the s1→s2 edge stays GREY — an honest "not fired yet" signal.
        var steps = new[] { Job("s1", 0, "A"), Job("s2", 1, "B") };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["s1"] = JobStatus.Completed,
            // s2 deliberately absent — not started.
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        var edge = cut.Find("path.dag-edge");
        var cls = edge.GetAttribute("class") ?? string.Empty;
        cls.Should().NotContain("dag-edge--completed",
 "a sequential edge does NOT fall back to the SOURCE status; a not-started destination = grey");
        cls.Should().NotContain("dag-edge--running");
        cut.FindAll("path.dag-edge--completed").Should().BeEmpty();
    }

    [Fact]
    public void LiveMode_FanInEdge_ColorsBySourceChild_WhenDestinationIsSyntheticAnchor()
    {
        // source-fallback: the fan-in edge's destination is the synthetic join (ToStepId == null),
        // so it colors by its FromStepId (the child). Child c1 = Completed ⇒ a completed fan-in edge.
        var children = new[] { Job("c1", 0, "C1"), Job("c2", 1, "C2") };
        var steps = new[] { Job("up", 0, "Up"), Parallel("pg", 1, children) };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["up"] = JobStatus.Completed,
            ["c1"] = JobStatus.Completed,
            ["c2"] = JobStatus.Running,
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        // Completed-tinted edges: fan-out up→c1 (dest c1 Completed) + fan-in c1→join (source c1 Completed).
        cut.FindAll("path.dag-edge--completed").Should().HaveCount(2,
 "fan-in colors by the source child (c1 Completed) since its destination is the synthetic join");
    }

    [Fact]
    public void StaticMode_NoEdgeGetsStatusClass()
    {
        // with StatusByStepId == null (Detail's static topology), NO edge carries a status
        // class. Locks that EdgeStatusClass short-circuits in static mode (no false coloring).
        var children = new[] { Job("c1", 0, "C1"), Job("c2", 1, "C2") };
        var steps = new[] { Job("s1", 0, "A"), Parallel("pg", 1, children) };
        var onFailure = new[] { Job("f1", 0, "Rollback") };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, steps)
            .Add(d => d.OnFailureSteps, onFailure)
            .Add(d => d.StatusByStepId, (IReadOnlyDictionary<string, JobStatus>?)null));

        cut.FindAll("path.dag-edge--running").Should().BeEmpty();
        cut.FindAll("path.dag-edge--completed").Should().BeEmpty();
        cut.FindAll("path.dag-edge--failed").Should().BeEmpty();
        cut.FindAll("path.dag-edge--cancelled").Should().BeEmpty();
    }

    [Fact]
    public void LiveMode_OnFailureEdge_NeverGetsStatusClass_KeepsDashedRed()
    {
        // OnFailure edges have null endpoints → EdgeStatusClass returns empty → the dashed-red
        // (dag-edge--on-failure) is preserved even in live mode. A regression would tint it.
        var spine = new[] { Job("s1", 0, "Main") };
        var onFailure = new[] { Job("f1", 0, "Rollback") };
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["s1"] = JobStatus.Failed,
            ["f1"] = JobStatus.Running,
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, spine)
            .Add(d => d.OnFailureSteps, onFailure)
            .Add(d => d.StatusByStepId, statusMap));

        var failureEdge = cut.Find("path.dag-edge--on-failure");
        var cls = failureEdge.GetAttribute("class") ?? string.Empty;
        cls.Should().NotContain("dag-edge--running",
 "an OnFailure edge (null endpoints) must NOT receive a live status tint");
        cls.Should().NotContain("dag-edge--failed");
        cls.Should().NotContain("dag-edge--completed");
    }
}
