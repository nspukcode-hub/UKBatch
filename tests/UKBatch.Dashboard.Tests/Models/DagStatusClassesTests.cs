using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Models;
using UKBatch.Dashboard.Models.DagStatus;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pure-C# unit tests for <see cref="DagStatusClasses"/>.
/// Pins the JobStatus → <c>data-status</c> token table (ported verbatim from
/// <c>DagView.StatusClass</c> / <c>DagView.EdgeStatusClass</c>) and the FOUR edge-keying cases
/// (Sequential=dest, fan-out=dest, fan-in=source/child, OnFailure=neutral).
/// </summary>
public sealed class DagStatusClassesTests
{
    private static DagLayoutNode Node(string stepId) => new()
    {
        StepId = stepId,
        Kind = DagNodeKind.Job,
        Title = stepId,
        X = 0,
        Y = 0,
        Width = 200,
        Height = 80,
    };

    private static StatusEdge Edge(string from, string to, string kind, bool fanIn) => new()
    {
        FromStepId = from,
        ToStepId = to,
        Kind = kind,
        IsFanIn = fanIn,
    };

    private static Dictionary<string, JobStatus> Map(params (string, JobStatus)[] entries)
    {
        var d = new Dictionary<string, JobStatus>(StringComparer.Ordinal);
        foreach (var (k, v) in entries) d[k] = v;
        return d;
    }

    // ── NodeClass: static mode / not-started / status families ───────────────────

    [Fact]
    public void NodeClass_StaticMode_ReturnsEmpty()
    {
        DagStatusClasses.NodeClass(Node("s1"), statusByStepId: null)
            .Should().BeEmpty("null map ⇒ static mode ⇒ no status styling (mirrors DagView.StatusClass)");
    }

    [Fact]
    public void NodeClass_LiveMode_NoEntry_ReturnsMuted()
    {
        DagStatusClasses.NodeClass(Node("s1"), Map())
            .Should().Be("muted", "live mode + no entry ⇒ not started yet ⇒ muted");
    }

    [Theory]
    [InlineData(JobStatus.Running, "running")]
    [InlineData(JobStatus.Retrying, "running")]
    [InlineData(JobStatus.AwaitingApproval, "running")]
    [InlineData(JobStatus.Completed, "completed")]
    [InlineData(JobStatus.Failed, "failed")]
    [InlineData(JobStatus.Cancelled, "cancelled")]
    [InlineData(JobStatus.Cancelling, "cancelled")]
    public void NodeClass_StatusFamilies_MapVerbatimWithDagView(JobStatus status, string expected)
    {
        DagStatusClasses.NodeClass(Node("s1"), Map(("s1", status)))
            .Should().Be(expected,
                $"Running/Retrying/AwaitingApproval→running; Completed; Failed; Cancelled/Cancelling→cancelled (DagView parity)");
    }

    [Theory]
    [InlineData(JobStatus.Pending)]
    [InlineData(JobStatus.Scheduled)]
    public void NodeClass_UntintedFamilies_ReturnEmpty(JobStatus status)
    {
        DagStatusClasses.NodeClass(Node("s1"), Map(("s1", status)))
            .Should().BeEmpty("Pending/Scheduled have no tint — default neutral style wins (DagView `_ => \"\"`)");
    }

    // ── EdgeClass: the FOUR cases ──────────────────────────────────

    [Fact]
    public void EdgeClass_StaticMode_ReturnsEmpty()
    {
        DagStatusClasses.EdgeClass(Edge("a", "b", "Sequential", false), statusByStepId: null)
            .Should().BeEmpty("static topology mode ⇒ no edge tint");
    }

    [Fact]
    public void EdgeClass_Sequential_KeysOffDestination()
    {
        // a Completed, b Running → the a→b edge colors by its DESTINATION (b = Running).
        var map = Map(("a", JobStatus.Completed), ("b", JobStatus.Running));
        DagStatusClasses.EdgeClass(Edge("a", "b", "Sequential", false), map)
            .Should().Be("running", "Sequential edge keys off the destination");
    }

    [Fact]
    public void EdgeClass_FanOut_KeysOffDestination()
    {
        // fan-out (IsFanIn=false): prev→child, keyed off the child (destination).
        var map = Map(("J", JobStatus.Completed), ("A", JobStatus.Running));
        DagStatusClasses.EdgeClass(Edge("J", "A", "Parallel", fanIn: false), map)
            .Should().Be("running", "fan-out (IsFanIn=false) keys off the destination/child");
    }

    [Fact]
    public void EdgeClass_FanIn_KeysOffSourceChild()
    {
        // the decisive case: child Completed, successor NOT started. The fan-in edge MUST
        // resolve to the CHILD's status (the honest "this branch finished" signal), NOT the destination.
        var map = Map(("A", JobStatus.Completed));   // successor "J2" deliberately absent (not started)
        DagStatusClasses.EdgeClass(Edge("A", "J2", "Parallel", fanIn: true), map)
            .Should().Be("completed",
                "fan-in (IsFanIn=true) keys off the source/child — a finished branch stays green even though the join hasn't fired");
    }

    [Fact]
    public void EdgeClass_FanInVsFanOut_SameNodesDifferentKey()
    {
        // Same child Completed + successor Running: fan-OUT into the child reads the child (Completed);
        // fan-IN out of the child reads the child too (Completed) — but a fan-in to a Running successor
        // must IGNORE the successor. Pin both directions resolve off the child, never the successor.
        var map = Map(("A", JobStatus.Completed), ("J2", JobStatus.Running));

        DagStatusClasses.EdgeClass(Edge("A", "J2", "Parallel", fanIn: true), map)
            .Should().Be("completed", "fan-in ignores the (Running) successor, reads the (Completed) child");

        DagStatusClasses.EdgeClass(Edge("J", "A", "Parallel", fanIn: false), Map(("A", JobStatus.Completed)))
            .Should().Be("completed", "fan-out reads its destination child");
    }

    [Fact]
    public void EdgeClass_OnFailure_KeyNotStarted_ReturnsEmpty_NeutralDashedRed()
    {
        // OnFailure edges key off their destination (IsFanIn=false); a not-started compensation node ⇒
        // empty ⇒ the kind class (dashed-red) wins. Never tinted.
        DagStatusClasses.EdgeClass(Edge("A", "F0", "OnFailure", false), Map())
            .Should().BeEmpty("OnFailure edge into a not-started node stays neutral (dashed-red kind class wins)");
    }

    [Fact]
    public void EdgeClass_KeyNodeNotStarted_ReturnsEmpty()
    {
        // Destination not started ⇒ empty (honest "not fired yet" — no source fallback for non-fan-in).
        var map = Map(("a", JobStatus.Completed));   // "b" absent
        DagStatusClasses.EdgeClass(Edge("a", "b", "Sequential", false), map)
            .Should().BeEmpty("a not-started destination keeps the edge grey (no source fallback for Sequential)");
    }

    [Fact]
    public void EdgeClass_NullArgument_Throws()
    {
        ((Action)(() => DagStatusClasses.EdgeClass(null!, Map())))
            .Should().Throw<ArgumentNullException>();
        ((Action)(() => DagStatusClasses.NodeClass(null!, Map())))
            .Should().Throw<ArgumentNullException>();
    }
}
