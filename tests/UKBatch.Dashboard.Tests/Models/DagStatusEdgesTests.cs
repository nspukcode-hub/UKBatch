using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for <see cref="DagStatusEdges.Build"/>.
/// No Blazor, no bunit. This is the riskiest logic in the live-status canvas: it replaces an older
/// "scan the Sequential <c>DagLayoutEdge</c> whose <c>FromStepId==groupStepId</c>" collapse,
/// which silently broke on consecutive ParallelGroups and on
/// onFailure-after-trailing-ParallelGroup. The five-topology set + edge cases lock the exact
/// emitted edge SET (From→To, Kind, IsFanIn).
/// </summary>
public sealed class DagStatusEdgesTests
{
    // ── helpers (mirror DagLayoutTests / DagViewTests) ───────────────────────────

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

    private static DagLayout Layout(IReadOnlyList<BatchStep> steps, IReadOnlyList<BatchStep> onFailure)
        => DagLayout.Compute(steps, onFailure);

    // A compact projection of an edge for set-equality assertions.
    private static (string From, string To, string Kind, bool FanIn) Tuple(StatusEdge e)
        => (e.FromStepId, e.ToStepId, e.Kind, e.IsFanIn);

    private static IReadOnlyList<StatusEdge> BuildFor(
        IReadOnlyList<BatchStep> steps, IReadOnlyList<BatchStep>? onFailure = null)
    {
        var of = onFailure ?? Array.Empty<BatchStep>();
        return DagStatusEdges.Build(steps, of, Layout(steps, of));
    }

    // ── (a) J → PG{A,B} → J2: fan-out + fan-in ──────────────────────────────────

    [Fact]
    public void Build_JobParallelJob_FanOutAndFanIn()
    {
        var steps = new[]
        {
            Job("J", 0, "First"),
            Parallel("PG", 1, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
            Job("J2", 2, "Last"),
        };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("J", "A", "Parallel", false),   // fan-out (dest-keyed)
            ("J", "B", "Parallel", false),   // fan-out
            ("A", "J2", "Parallel", true),   // fan-in (source/child-keyed)
            ("B", "J2", "Parallel", true),   // fan-in
        }, "J fans out to both children; both children fan in to J2 — fan-out IsFanIn=false, fan-in IsFanIn=true");
    }

    // ── (b) J → PG{A,B}: children have NO outbound (trailing group) ─────────────

    [Fact]
    public void Build_ParallelGroupLast_ChildrenHaveNoOutbound()
    {
        var steps = new[]
        {
            Job("J", 0, "First"),
            Parallel("PG", 1, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
        };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("J", "A", "Parallel", false),
            ("J", "B", "Parallel", false),
        }, "a trailing ParallelGroup's children have no successor → no outbound edges");

        edges.Should().NotContain(e => e.From == "A" || e.From == "B",
            "no child may originate an edge when the group is the last step");
    }

    // ── (c) PG{A,B} → J: children have NO inbound (leading group) ───────────────

    [Fact]
    public void Build_ParallelGroupFirst_NoInbound()
    {
        var steps = new[]
        {
            Parallel("PG", 0, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
            Job("J", 1, "After"),
        };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("A", "J", "Parallel", true),    // fan-in
            ("B", "J", "Parallel", true),
        }, "a leading ParallelGroup's children have no predecessor → no inbound; both fan in to J");

        edges.Should().NotContain(e => e.To == "A" || e.To == "B",
            "no edge may terminate at a child when the group is the first step");
    }

    // ── (d) PG1{A,B} → PG2{X,Y}: consecutive groups → cross-product ─────────────
    // THE case the old DagLayoutEdge-scan broke.

    [Fact]
    public void Build_ConsecutiveParallelGroups_EmitsCrossProduct()
    {
        var steps = new[]
        {
            Parallel("PG1", 0, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
            Parallel("PG2", 1, new[] { Job("X", 0, "X"), Job("Y", 1, "Y") }),
        };

        var edges = BuildFor(steps).ToList();

        // The contract: every PG2 child depends on ALL PG1 children — the full 2×2
        // cross-product, no dangling, no missing. (The old Sequential `DagLayoutEdge`-scan found nothing
        // here and the children dangled.) From→To + Kind are the load-bearing invariant.
        edges.Select(e => (e.FromStepId, e.ToStepId, e.Kind)).Should().BeEquivalentTo(new[]
        {
            ("A", "X", "Parallel"),
            ("A", "Y", "Parallel"),
            ("B", "X", "Parallel"),
            ("B", "Y", "Parallel"),
        }, "consecutive PGs emit the full cross-product — no dangling, no missing children");

        edges.Should().HaveCount(4, "exactly the 2×2 cross-product — the old Sequential-scan found nothing here");

        // PG→PG status-keying: the previous step WAS a ParallelGroup, so these edges are flagged IsFanIn
        // (keyed off the source/child — "that upstream parallel branch finished"). This is the walk's
        // `prevWasParallelGroup` discriminator and is deliberate — locked here so a future change
        // to the keying is a visible, intentional edit (not a silent edge-tint regression).
        edges.Should().AllSatisfy(e => e.IsFanIn.Should().BeTrue(
            "a PG→PG edge keys status off its source child (prevWasParallelGroup ⇒ IsFanIn)"));
    }

    // ── (e) Steps end in PG{A,B}, OnFailure=[F0,F1]: origin = children ──────────
    // The other case the old approach broke (group's own non-rendered stepId dangled).

    [Fact]
    public void Build_OnFailureAfterTrailingParallelGroup_OriginatesFromChildren()
    {
        var steps = new[]
        {
            Job("J", 0, "First"),
            Parallel("PG", 1, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
        };
        var onFailure = new[] { Job("F0", 0, "Comp0"), Job("F1", 1, "Comp1") };

        var edges = BuildFor(steps, onFailure).Select(Tuple).ToList();

        var failureEdges = edges.Where(e => e.Kind == "OnFailure").ToList();

        failureEdges.Should().BeEquivalentTo(new[]
        {
            ("A", "F0", "OnFailure", false),  // origin = the spine's TRUE exit nodes (children), NOT "PG"
            ("B", "F0", "OnFailure", false),
            ("F0", "F1", "OnFailure", false), // chained node→node down the compensation branch
        }, "onFailure originates from the children (real rendered nodes), NEVER the group's stepId");

        failureEdges.Should().NotContain(e => e.From == "PG",
            "the ParallelGroup's own (non-rendered) stepId must NEVER be an edge origin");
    }

    // ── edge cases: empty Steps / empty OnFailure / determinism ──────────────────

    [Fact]
    public void Build_EmptySteps_NoSpineEdges()
    {
        var edges = BuildFor(Array.Empty<BatchStep>());
        edges.Should().BeEmpty("no steps ⇒ no spine edges");
    }

    [Fact]
    public void Build_EmptyOnFailure_NoFailureEdges()
    {
        var steps = new[] { Job("J", 0, "Only") };

        var edges = BuildFor(steps);

        edges.Where(e => e.Kind == "OnFailure").Should().BeEmpty("no onFailure steps ⇒ no failure edges");
    }

    [Fact]
    public void Build_SingleJob_NoEdges()
    {
        var edges = BuildFor(new[] { Job("only", 0, "Solo") });
        edges.Should().BeEmpty("a single step has no successor ⇒ no edges");
    }

    [Fact]
    public void Build_SequentialThreeJobs_TwoSequentialEdges()
    {
        var steps = new[] { Job("a", 0, "A"), Job("b", 1, "B"), Job("c", 2, "C") };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("a", "b", "Sequential", false),
            ("b", "c", "Sequential", false),
        }, "a sequential spine emits dest-keyed Sequential edges, never IsFanIn");
    }

    [Fact]
    public void Build_IsDeterministic_AndHasNoSideEffectsOnInputs()
    {
        var steps = new[]
        {
            Job("J", 0, "First"),
            Parallel("PG", 1, new[] { Job("A", 0, "A"), Job("B", 1, "B") }),
            Job("J2", 2, "Last"),
        };
        var onFailure = new[] { Job("F0", 0, "Comp") };

        var layout = Layout(steps, onFailure);
        var first = DagStatusEdges.Build(steps, onFailure, layout).Select(Tuple).ToList();
        var second = DagStatusEdges.Build(steps, onFailure, layout).Select(Tuple).ToList();

        second.Should().Equal(first, "Build is a pure function — same input ⇒ same ordered output");

        // Inputs were not mutated (Order, child lists intact).
        steps.Select(s => s.Order).Should().Equal(0, 1, 2);
        steps[1].ParallelGroup!.Steps.Select(c => c.StepId).Should().Equal("A", "B");
    }

    [Fact]
    public void Build_NullArguments_Throw()
    {
        var steps = new[] { Job("a", 0) };
        var layout = Layout(steps, Array.Empty<BatchStep>());

        ((Action)(() => DagStatusEdges.Build(null!, Array.Empty<BatchStep>(), layout)))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => DagStatusEdges.Build(steps, null!, layout)))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => DagStatusEdges.Build(steps, Array.Empty<BatchStep>(), null!)))
            .Should().Throw<ArgumentNullException>();
    }

    // ── bonus: approval on the spine threads like a job node (real endpoints) ────

    [Fact]
    public void Build_ApprovalOnSpine_ThreadsAsSequentialNode()
    {
        var steps = new[] { Job("j", 0, "Run"), Approval("ap", 1, "Confirm"), Job("j2", 2, "After") };

        var edges = BuildFor(steps).Select(Tuple).ToList();

        edges.Should().BeEquivalentTo(new[]
        {
            ("j", "ap", "Sequential", false),
            ("ap", "j2", "Sequential", false),
        }, "an ApprovalGate is a single spine node — both endpoints are its real StepId");
    }
}
