using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Models;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for the decision render pipeline: the diamond + branch-node fan-out in
/// <see cref="DagLayout"/>, the structural diamond→branch + re-convergence edges in
/// <see cref="DagStatusEdges"/>, the LTR column placement in <see cref="DagStatusLayout"/>, the shared
/// label/step projections in <see cref="DecisionNodes"/>, and the diamond status overlay in
/// <see cref="DecisionStatusReconciler"/>. No Blazor, no bunit.
/// </summary>
public sealed class DagDecisionTests
{
    private static readonly string[] B1B2 = { "b1", "b2" };

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static BatchStep Job(string id, int order, string name = "JobX") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
    };

    private static DecisionBranch Branch(string id, StepCondition? when, string jobName, string? target = null, string? label = null) => new()
    {
        StepId = id,
        Label = label,
        When = when,
        Job = new JobStepData { JobName = jobName, TargetService = target },
    };

    private static StepCondition Gt(string key, string value) => new()
    {
        ParameterKey = key,
        Operator = ConditionOperator.GreaterThan,
        Value = value,
    };

    private static BatchStep Decision(string id, int order, params DecisionBranch[] branches) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Decision,
        Decision = new DecisionStepData { Branches = branches },
    };

    // ── DagLayout ────────────────────────────────────────────────────────────────

    [Fact]
    public void DagLayout_Decision_EmitsDiamondAndBranchNodes()
    {
        var steps = new[]
        {
            Job("j", 0, "First"),
            Decision("dec", 1,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
            Job("j2", 2, "After"),
        };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        // j + diamond + 2 branch nodes + j2 = 5 nodes (the fan-in join is a synthetic anchor, no node).
        layout.Nodes.Should().HaveCount(5);
        var diamond = layout.Nodes.Single(n => n.StepId == "dec");
        diamond.Kind.Should().Be(DagNodeKind.Decision, "the routing diamond is a Decision-kind node");
        var branchNodes = layout.Nodes.Where(n => n.GroupId == "dec").ToList();
        branchNodes.Should().HaveCount(2, "each branch renders as its own job node keyed by the branch id");
        branchNodes.Select(n => n.StepId).Should().BeEquivalentTo(B1B2);
        branchNodes.Should().AllSatisfy(n => n.Kind.Should().Be(DagNodeKind.Job));
    }

    [Fact]
    public void DagLayout_Decision_EmitsLabelledDiamondToBranchEdges()
    {
        var steps = new[]
        {
            Decision("dec", 0,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
        };

        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var decisionEdges = layout.Edges.Where(e => e.Kind == DagEdgeKind.Decision).ToList();
        // 2 fan-out (diamond→branch, labelled) + 2 fan-in (branch→join, unlabelled).
        decisionEdges.Should().HaveCount(4);
        var fanOut = decisionEdges.Where(e => e.FromStepId == "dec").ToList();
        fanOut.Should().HaveCount(2, "the diamond fans out to each branch");
        fanOut.Single(e => e.ToStepId == "b1").Label.Should().Be("amount > 1000");
        fanOut.Single(e => e.ToStepId == "b2").Label.Should().Be("else", "the null-condition branch is labelled else");
        // Fan-in edges re-converge from each branch to the synthetic join (null destination).
        var fanIn = decisionEdges.Where(e => e.ToStepId is null).ToList();
        fanIn.Select(e => e.FromStepId).Should().BeEquivalentTo(B1B2);
        fanIn.Should().AllSatisfy(e => e.Label.Should().BeNull("the re-convergence edges carry no label"));
    }

    // ── DagStatusEdges (live canvas structural topology) ──────────────────────────

    [Fact]
    public void DagStatusEdges_Decision_EmitsDiamondBranchAndReconvergence()
    {
        var steps = new[]
        {
            Job("j", 0, "First"),
            Decision("dec", 1,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
            Job("j2", 2, "After"),
        };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var edges = DagStatusEdges.Build(steps, Array.Empty<BatchStep>(), layout);

        // prev → diamond (Sequential).
        edges.Should().ContainSingle(e => e.FromStepId == "j" && e.ToStepId == "dec" && e.Kind == "Sequential" && !e.IsFanIn);
        // diamond → each branch (Decision, labelled).
        edges.Should().ContainSingle(e => e.FromStepId == "dec" && e.ToStepId == "b1" && e.Kind == "Decision" && e.Label == "amount > 1000");
        edges.Should().ContainSingle(e => e.FromStepId == "dec" && e.ToStepId == "b2" && e.Kind == "Decision" && e.Label == "else");
        // branch → next (re-convergence): keys off the source (IsFanIn) because the previous step fanned out.
        edges.Should().ContainSingle(e => e.FromStepId == "b1" && e.ToStepId == "j2" && e.IsFanIn);
        edges.Should().ContainSingle(e => e.FromStepId == "b2" && e.ToStepId == "j2" && e.IsFanIn);
        // NO direct diamond → next edge (routing goes through the branches).
        edges.Should().NotContain(e => e.FromStepId == "dec" && e.ToStepId == "j2");
    }

    [Fact]
    public void DagStatusEdges_TrailingDecision_HasNoOutboundFromBranches()
    {
        var steps = new[]
        {
            Job("j", 0, "First"),
            Decision("dec", 1,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
        };
        var layout = DagLayout.Compute(steps, Array.Empty<BatchStep>());

        var edges = DagStatusEdges.Build(steps, Array.Empty<BatchStep>(), layout);

        edges.Should().NotContain(e => e.FromStepId == "b1" || e.FromStepId == "b2",
            "a trailing decision's branches have no successor → no re-convergence edges");
        edges.Where(e => e.Kind == "Decision").Should().HaveCount(2, "only the two diamond→branch fan-out edges");
    }

    [Fact]
    public void DagStatusEdges_OnFailureAfterTrailingDecision_OriginatesFromBranches()
    {
        // When the spine ends in a decision, the onFailure chain originates from the branch exit set (real
        // rendered nodes), never the diamond's own id — mirroring the trailing-ParallelGroup rule.
        var steps = new[]
        {
            Job("j", 0, "First"),
            Decision("dec", 1,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
        };
        var onFailure = new[] { Job("f0", 0, "Comp") };
        var layout = DagLayout.Compute(steps, onFailure);

        var edges = DagStatusEdges.Build(steps, onFailure, layout);

        var failure = edges.Where(e => e.Kind == "OnFailure").ToList();
        failure.Select(e => e.FromStepId).Should().BeEquivalentTo(B1B2,
            "the onFailure chain fans in from every branch of a trailing decision, not the diamond id");
        failure.Should().AllSatisfy(e => e.ToStepId.Should().Be("f0"));
        failure.Should().NotContain(e => e.FromStepId == "dec");
    }

    [Fact]
    public void DagStatusEdges_DecisionLevelCompensator_FansInFromEveryBranch()
    {
        var decision = Decision("dec", 0,
            Branch("b1", Gt("amount", "1000"), "Express"),
            Branch("b2", null, "Standard")) with
        {
            Compensation = new CompensationStepData { JobName = "Undo" },
        };
        var layout = DagLayout.Compute(new[] { decision }, Array.Empty<BatchStep>());

        var edges = DagStatusEdges.Build(new[] { decision }, Array.Empty<BatchStep>(), layout);

        var compId = CompensationStepIds.For("dec");
        var compEdges = edges.Where(e => e.Kind == "Compensation").ToList();
        compEdges.Select(e => e.FromStepId).Should().BeEquivalentTo(B1B2,
            "a decision compensator fans in from every branch (the diamond's own id is not a fan-out source here)");
        compEdges.Should().AllSatisfy(e => e.ToStepId.Should().Be(compId));
    }

    // ── DagStatusLayout (LTR column placement) ────────────────────────────────────

    [Fact]
    public void DagStatusLayout_Decision_PlacesBranchesInNextColumn()
    {
        var steps = new[]
        {
            Job("j", 0, "First"),
            Decision("dec", 1,
                Branch("b1", Gt("amount", "1000"), "Express"),
                Branch("b2", null, "Standard")),
            Job("j2", 2, "After"),
        };

        var pos = DagStatusLayout.Compute(steps, Array.Empty<BatchStep>());

        // j at column 0, diamond at column 1, branches at column 2, j2 at column 3 (decision spans two columns).
        pos["j"].X.Should().Be(DagStatusLayout.StartX);
        pos["dec"].X.Should().Be(DagStatusLayout.StartX + DagStatusLayout.ColStride);
        pos["b1"].X.Should().Be(DagStatusLayout.StartX + 2 * DagStatusLayout.ColStride, "branches sit one column right of the diamond");
        pos["b2"].X.Should().Be(DagStatusLayout.StartX + 2 * DagStatusLayout.ColStride);
        pos["j2"].X.Should().Be(DagStatusLayout.StartX + 3 * DagStatusLayout.ColStride, "the next step clears both decision columns");
        // Branch nodes stack vertically (distinct Y).
        pos["b1"].Y.Should().NotBe(pos["b2"].Y);
    }

    // ── DecisionNodes (shared projections) ────────────────────────────────────────

    [Fact]
    public void DecisionNodes_BranchLabel_FormatsConditionOrElse()
    {
        DecisionNodes.BranchLabel(Branch("b", Gt("amount", "1000"), "J")).Should().Be("amount > 1000");
        DecisionNodes.BranchLabel(Branch("b", null, "J")).Should().Be("else");
        DecisionNodes.BranchLabel(Branch("b", Gt("amount", "1000"), "J", label: "big order"))
            .Should().Be("big order", "an explicit label wins over the condition text");
    }

    [Fact]
    public void DecisionNodes_BranchAsStep_ProjectsJobKeyedByBranchId()
    {
        var decision = Decision("dec", 3, Branch("b1", Gt("amount", "1000"), "Express", target: "shipping"));
        var branch = decision.Decision!.Branches[0];

        var step = DecisionNodes.BranchAsStep(decision, branch);

        step.StepId.Should().Be("b1", "the branch node is keyed by the branch id (== JobExecution.BatchStepId)");
        step.StepType.Should().Be(BatchStepType.Job);
        step.Order.Should().Be(3, "the branch carries the decision's order");
        step.Job!.JobName.Should().Be("Express");
        step.Job.TargetService.Should().Be("shipping");
        step.Condition.Should().Be(branch.When, "the branch's routing condition rides onto the synthesized step");
    }

    // ── DecisionStatusReconciler (diamond overlay) ────────────────────────────────

    private static Dictionary<string, JobStatus> Map(params (string Id, JobStatus Status)[] entries)
    {
        var m = new Dictionary<string, JobStatus>(StringComparer.Ordinal);
        foreach (var (id, s) in entries) m[id] = s;
        return m;
    }

    [Fact]
    public void Reconciler_WinnerCompleted_LosersSkipped_DiamondCompleted()
    {
        var steps = new[] { Decision("dec", 0, Branch("b1", Gt("amount", "1000"), "A"), Branch("b2", null, "B")) };
        var map = Map(("b1", JobStatus.Completed), ("b2", JobStatus.Skipped));

        DecisionStatusReconciler.Apply(steps, map);

        map["dec"].Should().Be(JobStatus.Completed, "a completed winner (losers skipped) paints the diamond decided");
    }

    [Fact]
    public void Reconciler_WinnerRunning_DiamondRunning()
    {
        var steps = new[] { Decision("dec", 0, Branch("b1", Gt("amount", "1000"), "A"), Branch("b2", null, "B")) };
        var map = Map(("b1", JobStatus.Running), ("b2", JobStatus.Skipped));

        DecisionStatusReconciler.Apply(steps, map);

        map["dec"].Should().Be(JobStatus.Running);
    }

    [Fact]
    public void Reconciler_WinnerFailed_DiamondFailed()
    {
        var steps = new[] { Decision("dec", 0, Branch("b1", Gt("amount", "1000"), "A"), Branch("b2", null, "B")) };
        var map = Map(("b1", JobStatus.Failed), ("b2", JobStatus.Skipped));

        DecisionStatusReconciler.Apply(steps, map);

        map["dec"].Should().Be(JobStatus.Failed, "a failed chosen branch fails the decision");
    }

    [Fact]
    public void Reconciler_AllSkipped_DiamondSkipped()
    {
        var steps = new[] { Decision("dec", 0, Branch("b1", Gt("amount", "1000"), "A"), Branch("b2", Gt("amount", "5000"), "B")) };
        var map = Map(("b1", JobStatus.Skipped), ("b2", JobStatus.Skipped));

        DecisionStatusReconciler.Apply(steps, map);

        map["dec"].Should().Be(JobStatus.Skipped, "no match and no else → the decision routed nowhere");
    }

    [Fact]
    public void Reconciler_NoBranchRowsYet_DiamondLeftUnset()
    {
        var steps = new[] { Decision("dec", 0, Branch("b1", Gt("amount", "1000"), "A"), Branch("b2", null, "B")) };
        var map = Map();   // no branch has a row yet

        DecisionStatusReconciler.Apply(steps, map);

        map.Should().NotContainKey("dec", "before any branch runs the diamond has no status (renders not-started)");
    }

    [Fact]
    public void Reconciler_IgnoresNonDecisionSteps_AndIsIdempotent()
    {
        var steps = new[] { Job("j", 0, "Plain"), Decision("dec", 1, Branch("b1", null, "A")) };
        var map = Map(("j", JobStatus.Completed), ("b1", JobStatus.Completed));

        DecisionStatusReconciler.Apply(steps, map);
        var first = new Dictionary<string, JobStatus>(map, StringComparer.Ordinal);
        DecisionStatusReconciler.Apply(steps, map);

        map.Should().BeEquivalentTo(first, "re-applying the same map is idempotent");
        map["j"].Should().Be(JobStatus.Completed, "a plain job status is untouched by the decision overlay");
        map["dec"].Should().Be(JobStatus.Completed);
    }
}
