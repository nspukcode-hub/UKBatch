using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models.Editor;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for <see cref="EditorEdges.Build"/>. No Blazor, no bunit. THE riskiest logic
/// in the feature: it derives the typed visual edge set (main-flow Sequential chain + the OnFailure
/// compensation branch from the spine's TRAILING top-level node).
/// These lock the exact emitted edge SET
/// (From→To, Kind), the documented ParallelGroup-collapse divergence from
/// <see cref="UKBatch.Dashboard.Models.DagStatus.DagStatusEdges"/>, and the orphan/empty guards.
/// </summary>
/// <remarks>
/// Mirrors the style of <see cref="DagStatusEdgesTests"/> (its read-only sibling): a compact edge
/// tuple + <c>BeEquivalentTo</c> set-equality. The KEY divergence asserted here is that the editor
/// renders a <c>ParallelGroup</c> as a SINGLE node, so the OnFailure branch anchors on the group's
/// OWN <c>StepId</c> — never its children (the editor never fans a PG out on-canvas). That single id
/// is exactly the one <c>BuildGraph</c>/<c>ToFailureNodeSpec</c> render, so the edge resolves.
/// </remarks>
public sealed class EditorEdgesTests
{
    // ── helpers (mirror DagStatusEdgesTests / DagViewTests) ──────────────────────

    private static WizardStepDraft Job(string id, string jobName = "JobX") => new()
    {
        StepId = id,
        StepType = BatchStepType.Job,
        JobName = jobName,
    };

    private static WizardStepDraft Approval(string id, string title = "Confirm") => new()
    {
        StepId = id,
        StepType = BatchStepType.ApprovalGate,
        ApprovalTitle = title,
    };

    private static WizardStepDraft Parallel(string id, params WizardStepDraft[] children) => new()
    {
        StepId = id,
        StepType = BatchStepType.ParallelGroup,
        JoinPolicy = ParallelJoinPolicy.WaitAll,
        Children = children.ToList(),
    };

    private static WizardStepDraft Decision(string id, params DecisionBranchDraft[] branches) => new()
    {
        StepId = id,
        StepType = BatchStepType.Decision,
        DecisionBranches = branches.ToList(),
    };

    private static DecisionBranchDraft Branch(string id, string? key, string jobName = "JobX") => new()
    {
        StepId = id,
        JobName = jobName,
        // A null key = the else/default branch; a set key = a conditional branch.
        When = key is null ? null : new ConditionDraft
        {
            ParameterKey = key,
            Operator = ConditionOperator.GreaterThan,
            Value = "1000",
        },
    };

    // A compact projection of an edge for set-equality assertions. NB: drops Label + BranchAccent — the
    // decision tests below assert those with ContainSingle(predicate) instead.
    private static (string From, string To, string Kind) Tuple(EditorEdge e)
        => (e.FromStepId, e.ToStepId, e.Kind);

    private static IReadOnlyList<EditorEdge> BuildFor(
        IReadOnlyList<WizardStepDraft> steps, IReadOnlyList<WizardStepDraft>? onFailure = null)
        => EditorEdges.Build(steps, onFailure ?? Array.Empty<WizardStepDraft>());

    // ── main flow: Sequential chain ──────────────────────────────────────────────

    [Fact]
    public void Build_Sequential_MainFlowChain()
    {
        var steps = new[] { Job("J1", "A"), Job("J2", "B"), Job("J3", "C") };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("J1", "J2", "Sequential"),
            ("J2", "J3", "Sequential"),
        }, "a 3-step main flow emits consecutive Sequential edges, no onFailure");
    }

    // ── onFailure branch: anchors on the LAST top-level node ─────────────────────

    [Fact]
    public void Build_OnFailure_BranchFromTrailingSpineNode()
    {
        var steps = new[] { Job("J1", "A"), Job("J2", "B") };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("J1", "J2", "Sequential"),
            ("J2", "F1", "OnFailure"),   // branch anchors on the LAST top-level spine node
        }, "the OnFailure branch originates from the spine's trailing top-level node, not the first");
    }

    [Fact]
    public void Build_OnFailureChain()
    {
        var steps = new[] { Job("J1", "A"), Job("J2", "B") };
        var onFailure = new[] { Job("F1", "Comp1"), Job("F2", "Comp2") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        edges.Where(e => e.Kind == "OnFailure").Should().BeEquivalentTo(new[]
        {
            ("J2", "F1", "OnFailure"),   // spine exit → first compensation step
            ("F1", "F2", "OnFailure"),   // then a node→node compensation chain
        }, "multiple onFailure steps chain after the spine exit (J(last)→F1, F1→F2)");
    }

    // ── DOCUMENTED divergence: spine ends in a ParallelGroup → anchor = PG's OWN id ──

    [Fact]
    public void Build_SpineEndsInParallelGroup_BranchAnchorsOnPgStepId()
    {
        var steps = new[]
        {
            Job("J1", "First"),
            Parallel("PG", Job("A", "A"), Job("B", "B")),
        };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        var failure = edges.Where(e => e.Kind == "OnFailure").ToList();

        failure.Should().BeEquivalentTo(new[]
        {
            ("PG", "F1", "OnFailure"),   // anchor = the ParallelGroup's OWN StepId (editor collapses PG to ONE node)
        }, "the editor renders a ParallelGroup as a SINGLE node — the OnFailure branch anchors on the " +
           "group's own StepId, NOT its children. This is the DOCUMENTED divergence from DagStatusEdges " +
           "(which fans a trailing PG out to its children). That id is exactly what BuildGraph renders, " +
           "so the edge resolves on-canvas.");

        failure.Should().NotContain(e => e.From == "A" || e.From == "B",
            "the editor must NEVER originate the OnFailure branch from a PG child (no on-canvas fan-out)");
    }

    // ── orphan / single-step / no-onFailure guards ───────────────────────────────

    [Fact]
    public void Build_EmptySteps_WithOnFailure_NoEdges()
    {
        var onFailure = new[] { Job("F1", "Orphan") };

        var edges = BuildFor(Array.Empty<WizardStepDraft>(), onFailure);

        edges.Should().BeEmpty(
            "an empty spine has no node to anchor the OnFailure branch on (orphan guard) — zero edges");
    }

    [Fact]
    public void Build_SingleStep_OnFailure()
    {
        var steps = new[] { Job("J1", "Solo") };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("J1", "F1", "OnFailure"),   // the lone spine node IS the trailing node → branch anchors on it
        }, "a single spine step + one compensation step emits exactly the OnFailure anchor edge (no Sequential)");
    }

    [Fact]
    public void Build_NoOnFailure_NoOnFailureEdges()
    {
        var steps = new[] { Job("J1", "A"), Job("J2", "B") };

        var edges = BuildFor(steps);

        edges.Where(e => e.Kind == "OnFailure").Should().BeEmpty(
            "no compensation steps ⇒ no OnFailure edges");
        edges.Should().OnlyContain(e => e.Kind == "Sequential", "only the main-flow chain remains");
    }

    [Fact]
    public void Build_SingleStep_NoOnFailure_NoEdges()
    {
        var edges = BuildFor(new[] { Job("only", "Solo") });
        edges.Should().BeEmpty("a single step has no successor and no compensation ⇒ no edges");
    }

    [Fact]
    public void Build_EmptyEverything_NoEdges()
    {
        var edges = BuildFor(Array.Empty<WizardStepDraft>());
        edges.Should().BeEmpty("no steps, no onFailure ⇒ no edges");
    }

    // ── approval on the spine threads like a job node (real endpoints) ───────────

    [Fact]
    public void Build_ApprovalOnSpine_ThreadsAsSequentialNode_AndAnchorsOnFailure()
    {
        var steps = new[] { Job("j", "Run"), Approval("ap", "Confirm") };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("j", "ap", "Sequential"),     // approval is a single spine node
            ("ap", "F1", "OnFailure"),     // the trailing approval node anchors the branch
        }, "an ApprovalGate is a single spine node — it threads Sequentially and can anchor the OnFailure branch");
    }

    // ── determinism + no input side effects ──────────────────────────────────────

    [Fact]
    public void Build_IsDeterministic_AndHasNoSideEffectsOnInputs()
    {
        var steps = new[]
        {
            Job("J1", "First"),
            Parallel("PG", Job("A", "A"), Job("B", "B")),
        };
        var onFailure = new[] { Job("F1", "Comp1"), Job("F2", "Comp2") };

        var first = EditorEdges.Build(steps, onFailure).Select(Tuple).ToList();
        var second = EditorEdges.Build(steps, onFailure).Select(Tuple).ToList();

        second.Should().Equal(first, "Build is a pure function — same input ⇒ same ordered output");

        // Inputs were not mutated (the draft lists + child lists intact).
        steps.Select(s => s.StepId).Should().Equal("J1", "PG");
        steps[1].Children.Select(c => c.StepId).Should().Equal("A", "B");
        onFailure.Select(s => s.StepId).Should().Equal("F1", "F2");
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        var steps = new[] { Job("a") };

        ((Action)(() => EditorEdges.Build(null!, Array.Empty<WizardStepDraft>())))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => EditorEdges.Build(steps, null!)))
            .Should().Throw<ArgumentNullException>();
    }

    // ── per-step compensator edges ────────────────────────────────────────────────

    [Fact]
    public void Build_StepWithCompensator_EmitsParentToDerivedIdEdge()
    {
        var withComp = Job("s1", "DoWork");
        withComp.Compensation = new CompensationDraft { JobName = "UndoWork" };
        var steps = new[] { withComp, Job("s2", "Next") };

        var edges = EditorEdges.Build(steps, Array.Empty<WizardStepDraft>()).Select(Tuple).ToList();

        edges.Should().Contain(("s1", CompensationStepIds.For("s1"), "OnFailure"),
            "a compensator renders as its own node hanging off the step it undoes — parent → derived id");
        edges.Should().Contain(("s1", "s2", "Sequential"), "the main flow is unaffected");
        edges.Should().HaveCount(2, "no batch-level chain here — one sequential + one compensator edge");
    }

    [Fact]
    public void Build_CompensatorEdges_CoexistWithFailureChain_AnchoredDifferently()
    {
        var withComp = Job("s1", "DoWork");
        withComp.Compensation = new CompensationDraft { JobName = "UndoWork" };
        var steps = new[] { withComp, Job("s2", "Last") };
        var chain = new[] { Job("f1", "NotifyOps") };

        var edges = EditorEdges.Build(steps, chain).Select(Tuple).ToList();

        edges.Should().Contain(("s1", CompensationStepIds.For("s1"), "OnFailure"),
            "the per-step compensator anchors on ITS OWN step");
        edges.Should().Contain(("s2", "f1", "OnFailure"),
            "the batch-level chain still anchors on the spine EXIT — the two mechanisms stay visually distinct");
    }

    [Fact]
    public void Build_NoCompensators_EmitsNoDerivedIdEdges()
    {
        var steps = new[] { Job("s1"), Job("s2") };

        var edges = EditorEdges.Build(steps, Array.Empty<WizardStepDraft>()).Select(Tuple).ToList();

        edges.Should().OnlyContain(e => !e.To.EndsWith(CompensationStepIds.Suffix),
            "compensator edges appear only when a step declares a compensator");
    }

    // ── decision fan-out: diamond → branch cards → re-convergence ─────────────────

    [Fact]
    public void Build_Decision_FansOutToBranches_WithLabelAndAccent()
    {
        var steps = new[]
        {
            Job("j1", "First"),
            Decision("dec", Branch("b1", "amount"), Branch("b2", null)),
        };

        var edges = BuildFor(steps);

        edges.Should().ContainSingle(e =>
            e.FromStepId == "j1" && e.ToStepId == "dec" && e.Kind == "Sequential",
            "the step before a decision connects to its diamond, not to a branch");

        var fanOut = edges.Where(e => e.Kind == "Decision").ToList();
        fanOut.Should().HaveCount(2, "the diamond fans out one edge per branch");
        fanOut.Should().ContainSingle(e =>
            e.FromStepId == "dec" && e.ToStepId == "b1" && e.Label == "amount > 1000" && e.BranchAccent == "1",
            "a conditional branch's edge carries its condition text + the first palette slot");
        fanOut.Should().ContainSingle(e =>
            e.FromStepId == "dec" && e.ToStepId == "b2" && e.Label == "else" && e.BranchAccent == BranchAccents.Else,
            "the else branch's edge is labelled else and takes the neutral accent, never a palette slot");
    }

    [Fact]
    public void Build_Decision_NextStepReconvergesFromEveryBranch()
    {
        var steps = new[]
        {
            Decision("dec", Branch("b1", "amount"), Branch("b2", null)),
            Job("j2", "After"),
        };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().Contain(("b1", "j2", "Sequential"));
        edges.Should().Contain(("b2", "j2", "Sequential"),
            "the next step re-converges from EVERY branch — whichever one wins must reach it");
        edges.Should().NotContain(e => e.From == "dec" && e.To == "j2",
            "routing goes THROUGH the branches — the diamond has no direct edge to the next step");
    }

    [Fact]
    public void Build_TrailingDecision_OnFailureAnchorsOnBranches_NotTheDiamond()
    {
        var steps = new[]
        {
            Job("j1", "First"),
            Decision("dec", Branch("b1", "amount"), Branch("b2", null)),
        };
        var onFailure = new[] { Job("F1", "Comp"), Job("F2", "Comp2") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();
        var failure = edges.Where(e => e.Kind == "OnFailure").ToList();

        failure.Should().Contain(("b1", "F1", "OnFailure"));
        failure.Should().Contain(("b2", "F1", "OnFailure"),
            "a trailing decision exits through its branch cards, so the failure chain fans in from all of them");
        failure.Should().NotContain(e => e.From == "dec",
            "the diamond is not the spine exit once it fans out — its branches are");
        failure.Should().Contain(("F1", "F2", "OnFailure"),
            "the chain still runs node→node after the anchor (only the anchor fans in)");
    }

    [Fact]
    public void Build_ZeroBranchDecision_ThreadsAsSingleSpineNode()
    {
        // Reachable mid-edit: the decision dialog lets the operator remove the last branch (only save-time
        // validation rejects it). An empty exit set would silently strand everything downstream.
        var steps = new[]
        {
            Job("j1", "First"),
            Decision("dec"),
            Job("j2", "After"),
        };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("j1", "dec", "Sequential"),
            ("dec", "j2", "Sequential"),
            ("j2", "F1", "OnFailure"),
        }, "a branch-less decision threads as ONE spine node — the chain and the onFailure anchor stay intact");
        edges.Should().NotContain(e => e.Kind == "Decision", "there is no branch to fan out to");
    }

    [Fact]
    public void Build_DecisionCompensator_HangsOffTheDiamond_NotTheBranches()
    {
        var decision = Decision("dec", Branch("b1", "amount"), Branch("b2", null));
        decision.Compensation = new CompensationDraft { JobName = "Undo" };

        var edges = BuildFor(new[] { decision }).Select(Tuple).ToList();

        var compId = CompensationStepIds.For("dec");
        edges.Should().Contain(("dec", compId, "OnFailure"),
            "a decision compensates as ONE unit — its compensator hangs off the diamond");
        edges.Should().ContainSingle(e => e.To == compId,
            "exactly one compensator edge: fanning in from the branches (which sit a column RIGHT of the " +
            "diamond) would sweep the edge back leftwards across the canvas");
    }

    [Fact]
    public void Build_ConsecutiveDecisions_CrossProductBetweenBranchSets()
    {
        var steps = new[]
        {
            Decision("d1", Branch("a1", "x"), Branch("a2", null)),
            Decision("d2", Branch("b1", "y"), Branch("b2", null)),
        };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().Contain(("a1", "d2", "Sequential"));
        edges.Should().Contain(("a2", "d2", "Sequential"),
            "the second diamond is entered from EVERY branch of the first — its entry is the diamond alone");
        edges.Where(e => e.Kind == "Decision").Should().HaveCount(4, "each diamond fans out to its own 2 branches");
    }

    [Fact]
    public void Build_NoDecision_OnlySequentialAndOnFailureKinds()
    {
        // Zero-regression pin: a batch with no decision must emit exactly the edge kinds it always did.
        var withComp = Job("s1", "DoWork");
        withComp.Compensation = new CompensationDraft { JobName = "UndoWork" };
        var steps = new[] { withComp, Parallel("PG", Job("A"), Job("B")), Approval("ap") };
        var onFailure = new[] { Job("F1", "Comp") };

        var edges = BuildFor(steps, onFailure);

        edges.Should().OnlyContain(e => e.Kind == "Sequential" || e.Kind == "OnFailure",
            "no decision ⇒ no Decision-kind edge ever appears");
        edges.Should().OnlyContain(e => e.BranchAccent == null && e.Label == null,
            "accent + label are decision-only — a decision-free batch carries neither");
    }
}
