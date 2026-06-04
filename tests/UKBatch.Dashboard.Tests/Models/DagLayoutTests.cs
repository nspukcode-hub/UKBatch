using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure C# unit tests for
/// <see cref="DagLayout.Compute(IReadOnlyList{BatchStep}, IReadOnlyList{BatchStep})"/>.
/// No Blazor, no bunit. Locks the deterministic top-down layout invariants the
/// <c>DagView</c> renderer depends on.
/// </summary>
public sealed class DagLayoutTests
{
    // ── helpers ──────────────────────────────────────────────────────────────────

    private static BatchStep Job(string id, int order, string jobName = "JobX", string? targetService = null) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = jobName, TargetService = targetService },
    };

    private static BatchStep Approval(string id, int order, string title = "Confirm") => new()
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

    /// <summary>An unrecognised forward-compat step type (cast int 99 onto the enum).</summary>
    private static BatchStep Unknown(string id, int order) => new()
    {
        StepId = id,
        Order = order,
        StepType = (BatchStepType)99,
    };

    // ── sequential 3-job ────────────────────────────────────────────────

    [Fact]
    public void Compute_SequentialThreeJobs_ProducesThreeNodesAndTwoSequentialEdges()
    {
        var steps = new[]
        {
            Job("s1", 0, "Step1"),
            Job("s2", 1, "Step2"),
            Job("s3", 2, "Step3"),
        };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes.Should().HaveCount(3);
        layout.Edges.Should().HaveCount(2);
        layout.Edges.Should().AllSatisfy(e => e.Kind.Should().Be(DagEdgeKind.Sequential));
        layout.Nodes.Should().AllSatisfy(n => n.Kind.Should().Be(DagNodeKind.Job));

        // y monotonically increases (top-down spine).
        var ordered = layout.Nodes.Select(n => n.Y).ToList();
        ordered.Should().BeInAscendingOrder("sequential spine is top-down");

        // Standard job dimensions: 200x80.
        layout.Nodes.Should().AllSatisfy(n => { n.Width.Should().Be(200); n.Height.Should().Be(80); });

        layout.Width.Should().BeGreaterThan(0);
        layout.Height.Should().BeGreaterThan(0);
    }

    // ── parallel(3) fan-out/fan-in ──────────────────────────────────────

    [Fact]
    public void Compute_ParallelGroupWithThreeChildren_FansOutAndFansIn_SixParallelEdges()
    {
        var children = new[]
        {
            Job("c1", 0, "ChildA"),
            Job("c2", 1, "ChildB"),
            Job("c3", 2, "ChildC"),
        };
        var steps = new[]
        {
            Job("upstream", 0, "Upstream"),       // anchor on spine
            Parallel("pg", 1, children),
        };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        // 1 upstream + 3 children = 4 nodes; no synthetic join node (R-10: join is a point on the spine).
        layout.Nodes.Should().HaveCount(4);

        // Three children + the sequential edge from the upstream are tested separately.
        var parallelEdges = layout.Edges.Where(e => e.Kind == DagEdgeKind.Parallel).ToList();
        parallelEdges.Should().HaveCount(6,
            "3 fan-out (upstream→child) + 3 fan-in (child→join) = 6 parallel edges");

        // Children are at three DISTINCT x-coordinates (the fan spreads them horizontally).
        var childNodes = layout.Nodes.Where(n => n.GroupId == "pg").ToList();
        childNodes.Should().HaveCount(3);
        childNodes.Select(n => n.X).Distinct().Should().HaveCount(3,
            "parallel children spread at horizontal pitch");

        // All children sit at the same y (one parallel "row").
        childNodes.Select(n => n.Y).Distinct().Should().ContainSingle();
    }

    // ── approval-gate RECTANGLE node (Chrome DAG-render fix, 2026-06) ───────────

    [Fact]
    public void Compute_ApprovalGate_RendersRectNode_SameDimsAsJob()
    {
        // Chrome DAG-render fix: the approval node is a RECTANGLE the same size as a job node
        // (200×80), NOT a 100×100 hexagon. The narrow hex foreignObject mis-placed its centered
        // content far LEFT under the canvas CSS transform; the job-node rectangle renders correctly.
        var steps = new[] { Approval("ap1", 0, "Approve me") };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes.Should().ContainSingle();
        var n = layout.Nodes[0];
        n.Kind.Should().Be(DagNodeKind.Approval);
        n.Width.Should().Be(DagLayout.NodeW, "approval node is now a rectangle the same size as a job node");
        n.Height.Should().Be(DagLayout.NodeH);
        n.Title.Should().Be("Approve me");
    }

    [Fact]
    public void Compute_ApprovalGate_CentersOnSpine_LikeJobNode()
    {
        // The rectangle must center on the spine exactly like a job node: a lone approval and a lone
        // job land at the SAME X after the deterministic minX→40 shift (both NodeW wide, both centered).
        var approvalLayout = DagLayout.Compute(new[] { Approval("ap1", 0) }, Array.Empty<BatchStep>());
        var jobLayout = DagLayout.Compute(new[] { Job("j1", 0) }, Array.Empty<BatchStep>());

        approvalLayout.Nodes[0].X.Should().Be(jobLayout.Nodes[0].X,
            "the approval rectangle centers on the spine identically to a job node");
        approvalLayout.Nodes[0].Width.Should().Be(jobLayout.Nodes[0].Width);
    }

    // ── spine taller than OnFailure (or vice versa) ──────────

    [Fact]
    public void Compute_Spine1Step_OnFailureBranchTaller_HeightAccountsForBranch()
    {
        // Code-review: when the OnFailure branch is taller than the main spine, the
        // layout height MUST account for the branch — NOT clip at the spine height.
        var spine = new[] { Job("s1", 0, "OnlyOne") };
        var onFailure = new[]
        {
            Job("f1", 0, "C1"),
            Job("f2", 1, "C2"),
            Job("f3", 2, "C3"),
        };

        var layout = DagLayout.Compute(spine, onFailure);

        // Branch bottom node sits at PadTop + 2*RowPitch (3 nodes total starting at PadTop).
        // PadTop=40, NodeH=80, RowGap=60, RowPitch=140. Branch bottom-edge ≈ 40+2*140+80 = 400.
        // Spine bottom-edge ≈ 40 + 80 = 120. With PadBottom=40, layout height ≥ 400+40 = 440.
        // The bug (pre-fix) would clip at the spine: 120+40 = 160.
        layout.Height.Should().BeGreaterThanOrEqualTo(440,
 "height MUST include the taller OnFailure branch (not clipped at the spine)");

        // Sanity: at least 4 nodes (1 spine + 3 branch).
        layout.Nodes.Should().HaveCountGreaterThanOrEqualTo(4);

        // At least one OnFailure edge (dashed red).
        layout.Edges.Should().Contain(e => e.Kind == DagEdgeKind.OnFailure);
    }

    // ── non-Job parallel children render as correct kind ────

    [Fact]
    public void Compute_ParallelGroup_WithApprovalChild_RendersApprovalKind()
    {
        // parallel-children that are NOT Job (forward-compat v0.2 data) must render
        // as the correct kind — NOT silently as JobNode.
        var children = new BatchStep[]
        {
            Job("c1", 0, "ChildJob"),
            Approval("c2", 1, "ChildApproval"),
        };
        var steps = new[] { Parallel("pg", 0, children) };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var c2 = layout.Nodes.SingleOrDefault(n => n.StepId == "c2");
        c2.Should().NotBeNull();
        c2!.Kind.Should().Be(DagNodeKind.Approval,
 "an ApprovalGate parallel-child must render as DagNodeKind.Approval");
    }

    [Fact]
    public void Compute_ParallelGroup_WithUnknownChild_RendersUnknownKind()
    {
        // a v0.2 future step type as a parallel-child must render Unknown (forward-compat).
        var children = new BatchStep[]
        {
            Job("c1", 0, "ChildJob"),
            Unknown("c2", 1),
        };
        var steps = new[] { Parallel("pg", 0, children) };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var c2 = layout.Nodes.SingleOrDefault(n => n.StepId == "c2");
        c2.Should().NotBeNull();
        c2!.Kind.Should().Be(DagNodeKind.Unknown,
 "an unknown-type parallel-child must render as DagNodeKind.Unknown");
    }

    // ── empty + single + deep parallel ──────────────────────────────────

    [Fact]
    public void Compute_EmptySteps_ReturnsEmptyLayout_DoesNotThrow()
    {
        var layout = DagLayout.Compute(Array.Empty<BatchStep>(), Array.Empty<BatchStep>());
        layout.Nodes.Should().BeEmpty();
        layout.Edges.Should().BeEmpty();
    }

    [Fact]
    public void Compute_SingleStep_ProducesOneNodeAndNoEdges()
    {
        var steps = new[] { Job("only", 0, "OnlyJob") };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes.Should().ContainSingle();
        layout.Edges.Should().BeEmpty();
    }

    [Fact]
    public void Compute_DeepParallel_GrowsViewBoxWidth()
    {
        // Six parallel children — viewBox width must widen vs a 2-child parallel.
        BatchStep ParallelOfN(int n) => Parallel("pg", 0,
            Enumerable.Range(0, n).Select(i => Job($"c{i}", i, $"C{i}")));

        var narrow = DagLayout.Compute(new[] { ParallelOfN(2) }, Array.Empty<BatchStep>());
        var wide = DagLayout.Compute(new[] { ParallelOfN(6) }, Array.Empty<BatchStep>());

        wide.Width.Should().BeGreaterThan(narrow.Width,
            "wider parallel fan grows the viewBox width deterministically");

        // The 6 children sit at 6 distinct x positions (fan spread at 220 px pitch).
        wide.Nodes.Where(n => n.GroupId == "pg")
            .Select(n => n.X).Distinct()
            .Should().HaveCount(6);
    }

    // ── Unknown step type on the spine (forward-compat) ─────────────────

    [Fact]
    public void Compute_UnknownSpineStepType_DoesNotThrow_RendersAsUnknown()
    {
        // The layout MUST NEVER throw on an unrecognised future step type (BatchStep deserialization
        // contract). It renders a neutral Unknown placeholder.
        var steps = new[] { Unknown("u1", 0) };

        Action act = () => DagLayout.Compute(steps, Array.Empty<BatchStep>());
        act.Should().NotThrow();

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());
        layout.Nodes.Should().ContainSingle();
        layout.Nodes[0].Kind.Should().Be(DagNodeKind.Unknown);
    }

    // ── coordinate shift keeps minX >= 0 ────────────────────────────────

    [Fact]
    public void Compute_CoordinateShift_KeepsAllNodesAtNonNegativeX()
    {
        // Internal shift normalises minX to 40 (left pad). After deep parallel fan-out, child nodes
        // could go negative pre-shift; the layout must shift them so the viewBox stays at x >= 0.
        var children = Enumerable.Range(0, 6)
            .Select(i => Job($"c{i}", i, $"C{i}"));
        var steps = new[] { Parallel("pg", 0, children) };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes.Should().AllSatisfy(n => n.X.Should().BeGreaterThanOrEqualTo(0,
            "shift normalisation must keep all node x >= 0"));
        layout.Edges.Should().AllSatisfy(e =>
        {
            e.X1.Should().BeGreaterThanOrEqualTo(0);
            e.X2.Should().BeGreaterThanOrEqualTo(0);
        });
    }

    // ── TargetService propagates onto the node ──────────────────────────

    [Fact]
    public void Compute_JobWithTargetService_PropagatesTargetServiceOntoNode()
    {
        var steps = new[] { Job("s1", 0, "RemoteJob", targetService: "worker-svc") };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes[0].TargetService.Should().Be("worker-svc");
    }

    // ── WaitMajority parallel uses correct join policy subtitle ─────────
    // (subtitle behaviour is layout-internal — covered indirectly; the asserted invariant is
    // the layout simply produces 3 nodes + 6 parallel edges as for any 3-child group.)

    [Fact]
    public void Compute_WaitMajorityParallelGroup_StillProducesSameTopology()
    {
        // The layout algorithm doesn't change based on join policy — it just records the structure.
        // 3 children, no upstream → 3 fan-in edges (to join). With an upstream the fan-out adds another 3.
        var children = new[]
        {
            Job("c1", 0, "C1"), Job("c2", 1, "C2"), Job("c3", 2, "C3"),
        };
        var steps = new[] { Parallel("pg", 0, children, ParallelJoinPolicy.WaitMajority) };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        layout.Nodes.Should().HaveCount(3);
        // Without an upstream anchor, fan-out edges are skipped (no `prevAnchorStepId` yet);
        // only the fan-in (children → join) edges materialise — 3.
        layout.Edges.Where(e => e.Kind == DagEdgeKind.Parallel).Should().HaveCount(3,
            "without an upstream, only fan-in edges are emitted (3 children → 3 fan-in)");
    }

    // ── edge endpoint StepIds for live-edge coloring ─────────

    [Fact]
    public void Compute_SequentialEdge_CarriesFromAndToStepIds()
    {
        // a sequential edge threads from=prevAnchor, to=currentStep so DagView can color it
        // by the DESTINATION node's status.
        var steps = new[] { Job("a", 0, "A"), Job("b", 1, "B") };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var seq = layout.Edges.Should().ContainSingle(e => e.Kind == DagEdgeKind.Sequential).Subject;
        seq.FromStepId.Should().Be("a", "sequential edge departs the previous spine anchor");
        seq.ToStepId.Should().Be("b", "sequential edge arrives at the current step (destination)");
    }

    [Fact]
    public void Compute_ParallelFanOut_ToStepIdIsChild_FanIn_ToStepIdIsNull()
    {
        // Fan-out edges carry to=child.StepId; fan-in edges target the SYNTHETIC join
        // anchor → ToStepId is null (DagView source-fallbacks to the child).
        var children = new[] { Job("c1", 0, "C1"), Job("c2", 1, "C2") };
        var steps = new[] { Job("up", 0, "Up"), Parallel("pg", 1, children) };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var parallel = layout.Edges.Where(e => e.Kind == DagEdgeKind.Parallel).ToList();
        parallel.Should().HaveCount(4, "2 fan-out + 2 fan-in for a 2-child group with an upstream");

        // Fan-out: from the upstream anchor INTO each child (ToStepId non-null = child).
        var fanOut = parallel.Where(e => e.ToStepId is not null).ToList();
        fanOut.Should().HaveCount(2);
        fanOut.Should().AllSatisfy(e => e.FromStepId.Should().Be("up"));
        fanOut.Select(e => e.ToStepId).Should().Contain("c1").And.Contain("c2");

        // Fan-in: from each child to the synthetic join anchor (ToStepId == null = source-fallback).
        var fanIn = parallel.Where(e => e.ToStepId is null).ToList();
        fanIn.Should().HaveCount(2, "fan-in edges target the synthetic join → null destination");
        fanIn.Select(e => e.FromStepId).Should().Contain("c1").And.Contain("c2");
    }

    [Fact]
    public void Compute_OnFailureEdges_HaveNullFromAndToStepIds()
    {
        // OnFailure connectors stay status-neutral (both endpoints null) so their dashed-red
        // is never overridden by a live status tint.
        var spine = new[] { Job("s1", 0, "Main") };
        var onFailure = new[] { Job("f1", 0, "Rollback"), Job("f2", 1, "Rollback2") };

        var layout = DagLayout.Compute(spine, onFailure);

        var failureEdges = layout.Edges.Where(e => e.Kind == DagEdgeKind.OnFailure).ToList();
        failureEdges.Should().NotBeEmpty();
        failureEdges.Should().AllSatisfy(e =>
        {
            e.FromStepId.Should().BeNull("OnFailure edges carry no status endpoints");
            e.ToStepId.Should().BeNull();
        });
    }
}
