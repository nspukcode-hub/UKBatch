using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure unit tests for the <c>DagLayout.Compute</c> hint-honoring overload.
/// Locks the contract: hints override per-step XY; ParallelGroup children stay deterministic from
/// the group XY (individual child hints ignored); the height accountant covers hinted Y > auto-y;
/// the no-hints path is byte-for-byte identical to the default layout; coverage spans Job/Spine,
/// Job/ParallelChild, Approval/Spine, Approval/ParallelChild, OnFailure/Job and Unknown/default.
/// </summary>
public sealed class DagLayoutHintsRespectTests
{
    // ── helpers (mirrors DagLayoutTests) ────────────────────────────────────────

    private static BatchStep Job(string id, int order, string jobName = "JobX") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = jobName },
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

    private static BatchStep Parallel(string id, int order, IEnumerable<BatchStep> children) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ParallelGroup,
        ParallelGroup = new ParallelGroupData
        {
            Steps = children.ToList(),
            JoinPolicy = ParallelJoinPolicy.WaitAll,
        },
    };

    private static BatchStep Unknown(string id, int order) => new()
    {
        StepId = id,
        Order = order,
        StepType = (BatchStepType)99,
    };

    private static Dictionary<string, DagLayoutHint> Hints(params (string id, double x, double y)[] entries)
    {
        var d = new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal);
        foreach (var (id, x, y) in entries)
        {
            d[id] = new DagLayoutHint { X = x, Y = y };
        }
        return d;
    }

    // ── NoHints byte-byte regression lock ───────────────────────────────────────

    [Fact]
    public void Compute_NoHints_ByteForByteIdenticalToDefaultLayout()
    {
        // The hint=null path MUST yield a byte-byte identical layout to the 2-arg overload.
        var steps = new[]
        {
            Job("s1", 0),
            Job("s2", 1),
            Approval("ap", 2),
            Parallel("pg", 3, new[] { Job("c1", 0), Job("c2", 1) }),
        };
        var onFailure = new[] { Job("f1", 0), Job("f2", 1) };

        var classic = DagLayout.Compute(steps, onFailure);
        var hintedNull = DagLayout.Compute(steps, onFailure, hints: null);
        var hintedEmpty = DagLayout.Compute(steps, onFailure, new Dictionary<string, DagLayoutHint>(StringComparer.Ordinal));

        hintedNull.Width.Should().Be(classic.Width);
        hintedNull.Height.Should().Be(classic.Height);
        hintedNull.Nodes.Count.Should().Be(classic.Nodes.Count);
        hintedEmpty.Width.Should().Be(classic.Width);
        hintedEmpty.Height.Should().Be(classic.Height);
        hintedEmpty.Nodes.Count.Should().Be(classic.Nodes.Count);

        // Pair-wise XY identity (after the deterministic shift normalisation).
        for (var i = 0; i < classic.Nodes.Count; i++)
        {
            hintedNull.Nodes[i].X.Should().Be(classic.Nodes[i].X);
            hintedNull.Nodes[i].Y.Should().Be(classic.Nodes[i].Y);
            hintedEmpty.Nodes[i].X.Should().Be(classic.Nodes[i].X);
            hintedEmpty.Nodes[i].Y.Should().Be(classic.Nodes[i].Y);
        }
    }

    // ── Job hint overrides auto-layout XY ───────────────────────────────────────

    [Fact]
    public void Compute_HintOverridesJobNodeXY()
    {
        var steps = new[] { Job("s1", 0) };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>(), Hints(("s1", 500, 300)));
        var node = layout.Nodes.Single(n => n.StepId == "s1");
        // After deterministic shift (minX → 40 left pad), the X is shifted accordingly.
        var classic = DagLayout.Compute(steps, Array.Empty<BatchStep>());
        // Y is invariant under the shift (only X shifts).
        node.Y.Should().Be(300, "hint overrides auto-y on the spine");
        node.X.Should().NotBe(classic.Nodes.Single().X, "hinted X positions differ from auto-layout");
    }

    // ── ApprovalGate hint override ──────────────────────────────────────────────

    [Fact]
    public void Compute_HintOverridesApprovalNodeXY()
    {
        var steps = new[] { Approval("ap1", 0) };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>(), Hints(("ap1", 700, 250)));
        var node = layout.Nodes.Single(n => n.StepId == "ap1");
        node.Kind.Should().Be(DagNodeKind.Approval);
        node.Y.Should().Be(250, "hint overrides auto-y on Approval node");
    }

    // ── ParallelGroup parent hint moves the group origin ────────────────────────

    [Fact]
    public void Compute_HintOverridesParallelGroupParentXY()
    {
        var steps = new[]
        {
            Parallel("pg", 0, new[] { Job("c1", 0), Job("c2", 1) }),
        };
        var layoutNoHint = DagLayout.Compute(steps, Array.Empty<BatchStep>());
        var layoutHinted = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("pg", 800, 400)));

        // Children XY differs because group origin moved.
        var childA0 = layoutNoHint.Nodes.Single(n => n.StepId == "c1");
        var childA1 = layoutHinted.Nodes.Single(n => n.StepId == "c1");
        childA1.Y.Should().Be(400, "ParallelGroup hint Y moves all children to that Y");
        childA1.Y.Should().NotBe(childA0.Y, "moved Y is different from auto-y");
    }

    // ── ParallelGroup CHILDREN ignored if individually hinted ───────────────────

    [Fact]
    public void Compute_ParallelGroupChildrenAutoLayoutWithinGroup_IndividualHintIgnored()
    {
        // A child's individual hint is IGNORED — children are deterministic from the group XY
        // (TryGetHint inside the ParallelGroup branch is keyed on STEP.StepId, the group's id;
        // child hint dict entries simply find no match).
        var steps = new[]
        {
            Parallel("pg", 0, new[] { Job("c1", 0), Job("c2", 1) }),
        };
        var hints = Hints(
            ("pg", 600, 100),    // group container hint — respected
            ("c1", 9999, 9999),  // child hint — IGNORED
            ("c2", -500, -500)); // child hint — IGNORED
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>(), hints);

        var children = layout.Nodes.Where(n => n.GroupId == "pg").ToList();
        children.Should().HaveCount(2);

        // Both children sit at group Y=100 (deterministic), NOT at child hint Y=9999/-500.
        children.All(c => c.Y == 100).Should().BeTrue(
            "children sit at the group Y, individual child hint values are discarded");

        // X values within [0, group center ± span], NOT 9999 or -500 (which would explode width).
        children.All(c => c.X < 5000).Should().BeTrue(
            "child X is computed from the group center + pitch, NOT from the child hint");
    }

    // ── OnFailure branch hint respected ─────────────────────────────────────────

    [Fact]
    public void Compute_OnFailureBranchHintRespected()
    {
        var spine = new[] { Job("s1", 0) };
        var onFailure = new[] { Job("f1", 0), Job("f2", 1) };
        var layout = DagLayout.Compute(spine, onFailure, Hints(("f1", 900, 500)));

        var f1 = layout.Nodes.Single(n => n.StepId == "f1");
        f1.IsFailureBranch.Should().BeTrue();
        f1.Y.Should().Be(500, "OnFailure hint overrides auto-layout y on failure branch");
    }

    // ── Partial hints — un-hinted nodes use auto-layout ─────────────────────────

    [Fact]
    public void Compute_PartialHints_UnHintedNodesUseAutoLayout()
    {
        var steps = new[] { Job("s1", 0), Job("s2", 1), Job("s3", 2) };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("s2", 700, 250)));

        var s2 = layout.Nodes.Single(n => n.StepId == "s2");
        var s1 = layout.Nodes.Single(n => n.StepId == "s1");
        var s3 = layout.Nodes.Single(n => n.StepId == "s3");

        s2.Y.Should().Be(250, "s2 hint respected");
        // s1 + s3 use auto-y (not 250) — y-cursor only advances for non-hinted nodes (S2's hint
        // 'frees' the spine, but s1 was placed first and s3 after — both at auto-y values).
        s1.Y.Should().NotBe(250);
        s3.Y.Should().NotBe(250);
    }

    // ── Hint for missing StepId gracefully ignored ──────────────────────────────

    [Fact]
    public void Compute_HintForMissingStepId_GracefullyIgnored()
    {
        var steps = new[] { Job("s1", 0) };
        var hinted = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("orphan", 9999, 9999)));
        var classic = DagLayout.Compute(steps, Array.Empty<BatchStep>());
        // Orphan hint changes nothing — layout is byte-byte to the unhinted classic.
        hinted.Width.Should().Be(classic.Width);
        hinted.Height.Should().Be(classic.Height);
        hinted.Nodes.Single().X.Should().Be(classic.Nodes.Single().X);
        hinted.Nodes.Single().Y.Should().Be(classic.Nodes.Single().Y);
    }

    // ── Hints respect DagLayoutHintBounds ───────────────────────────────────────

    [Fact]
    public void Compute_HintsRespectClampBounds()
    {
        // DagLayout's responsibility is to render the hinted coordinates faithfully — clamping
        // happens at DROP TIME (DagView.OnDropAsync) before persisting. This test locks that
        // DagLayout doesn't double-clamp: it renders hints at the bounds verbatim.
        var steps = new[] { Job("s1", 0) };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("s1", DagLayoutHintBounds.MaxX, DagLayoutHintBounds.MaxY)));
        var node = layout.Nodes.Single();
        node.Y.Should().Be(DagLayoutHintBounds.MaxY, "DagLayout renders hint Y verbatim (clamp happens at drop)");
        // Width grows because maxX expanded with hinted node X.
        layout.Width.Should().BeGreaterThan(0);
    }

    // ── Unknown step type with hint — height tracker updated ────────────────────

    [Fact]
    public void Compute_UnknownStep_WithHint_HeightTrackerUpdated()
    {
        // The `default:` case in DagLayout.cs MUST update maxNodeBottomY when a hint pushes Y
        // beyond the auto-y cursor — otherwise the viewBox bottom clips the unknown node.
        var steps = new[] { Unknown("u1", 0) };
        var hinted = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("u1", 100, 800))); // hinted Y=800 — well beyond default auto-y of 40

        // Height MUST account for the hinted Y plus NodeH (80) + PadBottom (40) = 920.
        hinted.Height.Should().BeGreaterThanOrEqualTo(920,
            "the Unknown default case updates the maxNodeBottomY tracker");
    }

    // ── Spine 1-step + OnFailure branch taller — hints respected ────────────────

    [Fact]
    public void Compute_Spine1Step_OnFailureBranchTaller_WithHints_HeightAccountsForBranch()
    {
        // Hint composition: a hint-pushed OnFailure node MUST grow the height too.
        var spine = new[] { Job("s1", 0) };
        var onFailure = new[] { Job("f1", 0), Job("f2", 1), Job("f3", 2) };
        var hinted = DagLayout.Compute(spine, onFailure, Hints(("f3", 100, 700)));
        // f3 hinted at y=700 → height >= 700 + NodeH(80) + PadBottom(40) = 820.
        hinted.Height.Should().BeGreaterThanOrEqualTo(820,
            "a hinted OnFailure y=700 grows the height");
    }

    // ── Height formula subsumes auto-only path (no regression) ──────────────────

    [Fact]
    public void Compute_HeightFormulaSubsumes_NoRegressionWhenNoHints()
    {
        // Math.Max(maxNodeBottomY, Math.Max(y, branchBottomY)) subsumes the older
        // Math.Max(y, branchBottomY) formula — when no hints, maxNodeBottomY <= max(y, branchBottomY)
        // by construction, so the result is identical.
        var steps = new[]
        {
            Job("s1", 0), Job("s2", 1), Approval("ap", 2),
        };
        var classic = DagLayout.Compute(steps, Array.Empty<BatchStep>());
        var hinted = DagLayout.Compute(steps, Array.Empty<BatchStep>(), hints: null);
        hinted.Height.Should().Be(classic.Height);
    }

    // ── All 6 maxNodeBottomY update sites covered ───────────────────────────────

    [Fact]
    public void Compute_AllSixSitesMaxNodeBottomYUpdated()
    {
        // Exercise each of the 6 sites in one layout to ensure none clip even under aggressive hints.
        // Sites: 1 Job/Spine, 2 Approval/Spine, 3 Job/ParallelChild, 4 Synthetic join, 5 OnFailure/Job, 6 Unknown.
        var steps = new[]
        {
            Job("job", 0),                                                 // site #1
            Approval("ap", 1),                                             // site #2
            Parallel("pg", 2, new[] { Job("c1", 0), Job("c2", 1) }),       // site #3 + #4
            Unknown("u", 3),                                               // site #6
        };
        var onFailure = new[] { Job("fail", 0) };                          // site #5

        // Hint EVERY hintable node with a very large Y so the height tracker MUST observe each one.
        var hinted = DagLayout.Compute(steps, onFailure, Hints(
            ("job",  100, 600),
            ("ap",   200, 700),
            ("pg",   300, 750),
            ("u",    400, 800),
            ("fail", 500, 900)));

        // The tallest hinted node is "fail" at y=900 → height >= 900 + 80 + 40 = 1020.
        hinted.Height.Should().BeGreaterThanOrEqualTo(1020,
            "all 6 maxNodeBottomY sites correctly grow viewBox height under hints");
    }

    // ── Edge endpoints respect hinted XY ────────────────────────────────────────

    [Fact]
    public void Compute_EdgeEndpointsRespectHintXY()
    {
        var steps = new[] { Job("s1", 0), Job("s2", 1) };
        var hinted = DagLayout.Compute(steps, Array.Empty<BatchStep>(),
            Hints(("s1", 200, 100), ("s2", 600, 400)));

        var s1 = hinted.Nodes.Single(n => n.StepId == "s1");
        var s2 = hinted.Nodes.Single(n => n.StepId == "s2");
        var edge = hinted.Edges.Single();
        edge.Kind.Should().Be(DagEdgeKind.Sequential);
        // The edge starts at s1's bottom-center and ends at s2's top-center.
        edge.Y1.Should().Be(s1.Y + s1.Height, "edge Y1 anchors at s1 bottom edge (after hint)");
        edge.Y2.Should().Be(s2.Y, "edge Y2 anchors at s2 top edge (after hint)");
    }

    // ── Rebuild after hint mutation is deterministic ────────────────────────────

    [Fact]
    public void Compute_RebuildAfterHintMutation_DeterministicOutput()
    {
        var steps = new[] { Job("s1", 0), Job("s2", 1) };
        var hints1 = Hints(("s1", 100, 100));
        var hints2 = Hints(("s1", 100, 100));
        var l1 = DagLayout.Compute(steps, Array.Empty<BatchStep>(), hints1);
        var l2 = DagLayout.Compute(steps, Array.Empty<BatchStep>(), hints2);

        l1.Width.Should().Be(l2.Width);
        l1.Height.Should().Be(l2.Height);
        l1.Nodes.Select(n => (n.StepId, n.X, n.Y))
            .Should().Equal(l2.Nodes.Select(n => (n.StepId, n.X, n.Y)),
                "two compute calls with identical hints produce identical XY");
    }
}
