using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// pure-C# unit tests for <see cref="DagStatusLayout.Compute"/>, the LEFT-TO-RIGHT,
/// parallel-EXPANDED position layout that replaces <c>DagLayout</c>'s vertical-spine coords on the
/// read-only canvas (taller live cards overlapped under the old coords). These pin the overlap-free
/// invariants by CONSTRUCTION (the operator visually verifies; bunit can't render real Drawflow).
/// </summary>
public sealed class DagStatusLayoutTests
{
    private const double ColStride = 320;   // mirror DagStatusLayout.ColStride
    private const double ChildStride = 150; // mirror DagStatusLayout.ChildStride
    private const double StartX = 40;
    private const double MidY = 240;

    private static BatchStep Job(string id, int order, string name = "JobX") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
    };

    private static BatchStep Approval(string id, int order) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ApprovalGate,
        Approval = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "ops" }, OnTimeout = ApprovalTimeoutAction.Fail },
    };

    private static BatchStep Parallel(string id, int order, params BatchStep[] children) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.ParallelGroup,
        ParallelGroup = new ParallelGroupData { Steps = children.ToList(), JoinPolicy = ParallelJoinPolicy.WaitAll },
    };

    private static IReadOnlyDictionary<string, (double X, double Y)> Compute(
        IReadOnlyList<BatchStep> steps, IReadOnlyList<BatchStep>? onFailure = null)
        => DagStatusLayout.Compute(steps, onFailure ?? Array.Empty<BatchStep>());

    // ── sequential: distinct columns, strictly increasing x by ColStride ─────────

    [Fact]
    public void Sequential_NodesOccupyDistinctColumns_XIncreasesByColStride()
    {
        var steps = new[] { Job("a", 0), Approval("b", 1), Job("c", 2) };

        var map = Compute(steps);

        map.Should().HaveCount(3);
        map["a"].X.Should().Be(StartX);
        map["b"].X.Should().Be(StartX + ColStride);
        map["c"].X.Should().Be(StartX + 2 * ColStride);

        // No two top-level columns collide horizontally (gap == ColStride >> card width 230).
        var xs = new[] { map["a"].X, map["b"].X, map["c"].X };
        xs.Distinct().Should().HaveCount(3, "each top-level step is its own column");
        for (int i = 1; i < xs.Length; i++)
            (xs[i] - xs[i - 1]).Should().Be(ColStride);
    }

    [Fact]
    public void Sequential_AllNodesShareBaselineY()
    {
        var steps = new[] { Job("a", 0), Job("b", 1), Job("c", 2) };

        var map = Compute(steps);

        // Single nodes are centred on MidY ⇒ identical top-left Y across the row (LTR flow).
        map["a"].Y.Should().Be(map["b"].Y);
        map["b"].Y.Should().Be(map["c"].Y);
    }

    // ── parallel: children share ONE column, vertically spaced, centred on MidY ────

    [Fact]
    public void ParallelGroup_ChildrenShareColumn_SpacedAtLeastChildStride_CentredOnMidY()
    {
        var steps = new[]
        {
            Job("up", 0),
            Parallel("pg", 1, Job("c1", 0), Job("c2", 1), Job("c3", 2)),
            Job("down", 2),
        };

        var map = Compute(steps);

        // The container step itself has NO node (children render instead).
        map.ContainsKey("pg").Should().BeFalse("the ParallelGroup container renders no node — its children do");
        map.Should().HaveCount(5, "up + 3 children + down");

        // All three children share the SAME column (== column index 1).
        double colX = StartX + ColStride;
        map["c1"].X.Should().Be(colX);
        map["c2"].X.Should().Be(colX);
        map["c3"].X.Should().Be(colX);

        // Vertically spaced ≥ ChildStride apart (no overlap for ~130px cards).
        var centres = new[] { map["c1"].Y, map["c2"].Y, map["c3"].Y }.OrderBy(v => v).ToArray();
        for (int i = 1; i < centres.Length; i++)
            (centres[i] - centres[i - 1]).Should().BeGreaterThanOrEqualTo(ChildStride);

        // Centred around MidY: the mean of the children's top-left Y equals the single-node baseline.
        double meanChildY = (map["c1"].Y + map["c2"].Y + map["c3"].Y) / 3.0;
        meanChildY.Should().BeApproximately(map["up"].Y, 0.001,
            "the fan-out is centred on the same baseline as the single spine nodes");

        // The group advances the column by ONE: "down" is the column AFTER the group's column.
        map["down"].X.Should().Be(StartX + 2 * ColStride);
    }

    [Fact]
    public void ParallelGroup_SingleChild_CentredOnMidY()
    {
        var steps = new[] { Parallel("pg", 0, Job("only", 0)) };

        var map = Compute(steps);

        map.Should().HaveCount(1);
        map["only"].X.Should().Be(StartX);
        map["only"].Y.Should().Be(MidY - 130 / 2.0, "single child centres exactly on MidY (NodeH=130)");
    }

    // ── onFailure: a lower lane, left→right, clear of the spine ───────────────────

    [Fact]
    public void OnFailure_StepsOnLowerLane_LeftToRight_BelowMainFlow()
    {
        var steps = new[] { Job("a", 0), Job("b", 1) };
        var onFailure = new[] { Job("f1", 0), Job("f2", 1) };

        var map = Compute(steps, onFailure);

        map.Should().HaveCount(4);
        // Lower lane: failure Y strictly greater than main-flow Y.
        map["f1"].Y.Should().BeGreaterThan(map["a"].Y, "the OnFailure lane sits below the main flow");
        map["f2"].Y.Should().Be(map["f1"].Y, "the lane is a single horizontal row");
        // Left→right, CONTINUING the column counter from the 2-column spine (a=col0, b=col1) so the dashed
        // compensation edge flows right-and-down from its source instead of looping back across the top.
        map["f1"].X.Should().Be(StartX + 2 * ColStride);
        map["f2"].X.Should().Be(StartX + 3 * ColStride);
    }

    [Fact]
    public void OnFailure_ClearsTallestParallelFanOut()
    {
        // A wide fan-out (4 children) plus an onFailure lane — the lane must clear the lowest child.
        var steps = new[]
        {
            Parallel("pg", 0, Job("c1", 0), Job("c2", 1), Job("c3", 2), Job("c4", 3)),
        };
        var onFailure = new[] { Job("f1", 0) };

        var map = Compute(steps, onFailure);

        double lowestChildBottom = new[] { map["c1"].Y, map["c2"].Y, map["c3"].Y, map["c4"].Y }.Max() + 130;
        map["f1"].Y.Should().BeGreaterThan(lowestChildBottom,
            "the OnFailure lane clears the bottom of the tallest fan-out");
    }

    // ── edge cases: PG-first / PG-last / empty ───────────────────────────────────

    [Fact]
    public void ParallelGroupFirst_ChildrenInColumnZero()
    {
        var steps = new[]
        {
            Parallel("pg", 0, Job("c1", 0), Job("c2", 1)),
            Job("after", 1),
        };

        var map = Compute(steps);

        map["c1"].X.Should().Be(StartX);
        map["c2"].X.Should().Be(StartX);
        map["after"].X.Should().Be(StartX + ColStride, "the lone group still consumes exactly one column");
    }

    [Fact]
    public void ParallelGroupLast_ChildrenInFinalColumn()
    {
        var steps = new[]
        {
            Job("first", 0),
            Parallel("pg", 1, Job("c1", 0), Job("c2", 1)),
        };

        var map = Compute(steps);

        map["first"].X.Should().Be(StartX);
        map["c1"].X.Should().Be(StartX + ColStride);
        map["c2"].X.Should().Be(StartX + ColStride);
    }

    [Fact]
    public void Empty_ReturnsEmptyMap()
    {
        var map = Compute(Array.Empty<BatchStep>());
        map.Should().BeEmpty();
    }

    [Fact]
    public void OrderRespected_NotInsertionOrder()
    {
        // Steps supplied out of Order ⇒ columns follow Order, not list position.
        var steps = new[] { Job("third", 2), Job("first", 0), Job("second", 1) };

        var map = Compute(steps);

        map["first"].X.Should().Be(StartX);
        map["second"].X.Should().Be(StartX + ColStride);
        map["third"].X.Should().Be(StartX + 2 * ColStride);
    }

    // ── compensation lane: one node per compensator, in the parent's column, below the spine ──

    private static BatchStep JobWithComp(string id, int order, string compJob = "Undo", string name = "JobX") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
        Compensation = new CompensationStepData { JobName = compJob },
    };

    [Fact]
    public void Compensator_EmitsNode_InParentColumn_BelowSpine()
    {
        var steps = new[] { Job("a", 0), JobWithComp("b", 1) };

        var map = Compute(steps);

        var compId = CompensationStepIds.For("b");
        map.Should().ContainKey(compId, "a step with a compensator emits a compensation-lane node");
        map[compId].X.Should().Be(map["b"].X, "the compensator sits in its parent's column");
        map[compId].Y.Should().BeGreaterThan(map["b"].Y, "the compensation lane is below the spine");
    }

    [Fact]
    public void Compensator_OnlyEmittedForStepsThatHaveOne()
    {
        var steps = new[] { Job("a", 0), JobWithComp("b", 1), Job("c", 2) };

        var map = Compute(steps);

        map.Should().ContainKey(CompensationStepIds.For("b"));
        map.Should().NotContainKey(CompensationStepIds.For("a"), "no compensator ⇒ no compensation node");
        map.Should().NotContainKey(CompensationStepIds.For("c"));
    }

    [Fact]
    public void CompensationLane_ShiftsOnFailureLaneDown()
    {
        // The compensation lane extends the content bounds, so the OnFailure lane (whose Y derives from the
        // deepest node) shifts down automatically — the two lower lanes never collide.
        var withComp = Compute(new[] { JobWithComp("a", 0) }, new[] { Job("f1", 0) });
        var noComp = Compute(new[] { Job("a", 0) }, new[] { Job("f1", 0) });

        var compId = CompensationStepIds.For("a");
        withComp[compId].Y.Should().BeLessThan(withComp["f1"].Y,
            "the compensation lane sits above the OnFailure lane");
        withComp["f1"].Y.Should().BeGreaterThan(noComp["f1"].Y,
            "adding a compensator pushes the OnFailure lane further down (content-derived)");
    }

    [Fact]
    public void NoCompensators_OnFailureLanePlacement_Unchanged()
    {
        // Additive guard: a definition with no compensators lays out exactly as before the feature.
        var steps = new[] { Job("a", 0), Job("b", 1) };
        var onFailure = new[] { Job("f1", 0) };

        var map = Compute(steps, onFailure);

        map.Keys.Should().NotContain(k => k.EndsWith(":comp", StringComparison.Ordinal),
            "no compensators ⇒ no compensation-lane nodes");
        map["f1"].Y.Should().Be(map["a"].Y + 300, "the OnFailure lane keeps its pre-feature floor (FailureLaneDy)");
    }
}
