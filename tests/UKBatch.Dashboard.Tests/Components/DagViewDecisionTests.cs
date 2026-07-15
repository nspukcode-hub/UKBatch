using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// bunit render assertions for a decision step in <see cref="DagView"/> (the SVG wizard-preview renderer):
/// the diamond renders as a RECTANGLE (never an SVG polygon — the transform-displacement lesson), its
/// branch jobs render as their own nodes, the diamond→branch edges carry a condition label, a losing
/// branch paints skipped, and clicking a branch resolves to that branch's job.
/// </summary>
public sealed class DagViewDecisionTests : TestContext
{
    public DagViewDecisionTests()
    {
        // Graceful-degradation posture (the module import is a no-op under bunit's mocked JSInterop).
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private static BatchStep Job(string id, int order, string name = "JobX") => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
    };

    private static BatchStep Decision(string id, int order, params DecisionBranch[] branches) => new()
    {
        StepId = id,
        Order = order,
        StepType = BatchStepType.Decision,
        Decision = new DecisionStepData { Branches = branches },
    };

    private static DecisionBranch Branch(string id, StepCondition? when, string jobName) => new()
    {
        StepId = id,
        When = when,
        Job = new JobStepData { JobName = jobName },
    };

    private static StepCondition Gt(string key, string value) => new()
    {
        ParameterKey = key,
        Operator = ConditionOperator.GreaterThan,
        Value = value,
    };

    private static BatchStep[] SampleDecision() =>
    [
        Job("j", 0, "Prepare"),
        Decision("dec", 1,
            Branch("b1", Gt("amount", "1000"), "Express"),
            Branch("b2", null, "Standard")),
        Job("j2", 2, "Notify"),
    ];

    [Fact]
    public void Decision_RendersDiamondAsRectangle_WithCallSplitIcon_NoPolygon()
    {
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        var diamond = cut.Find("div.dag-node--decision");
        (diamond.GetAttribute("class") ?? string.Empty).Should().Contain("dag-node",
            "the decision node is a rectangle variant of dag-node, not a separate shape");
        cut.Markup.Should().Contain("call_split", "the diamond carries the call_split (routing) icon");
        cut.FindAll("polygon").Should().BeEmpty("the decision node is a rectangle — no SVG polygon diamond");
    }

    [Fact]
    public void Decision_RendersBranchJobNodes()
    {
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        // j + diamond + 2 branch nodes + j2 = 5 foreignObject nodes.
        cut.FindAll("foreignObject").Should().HaveCount(5);
        cut.Markup.Should().Contain("Express").And.Contain("Standard", "each branch job renders as its own node");
    }

    [Fact]
    public void Decision_RendersLabelledBranchEdges()
    {
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>()));

        var labels = cut.FindAll("text.dag-edge__label");
        // Two labelled diamond→branch edges; the fan-in re-convergence edges carry no label.
        labels.Should().HaveCount(2);
        labels.Select(l => l.TextContent).Should().Contain("amount > 1000").And.Contain("else");

        // The amber decision edge style is applied to the fan-out/fan-in connectors.
        cut.FindAll("path.dag-edge--decision").Should().NotBeEmpty("decision branch edges get the amber decision style");
    }

    [Fact]
    public void Decision_LiveStatus_LoserBranchPaintsSkipped()
    {
        var statusMap = new Dictionary<string, JobStatus>(StringComparer.Ordinal)
        {
            ["b1"] = JobStatus.Completed,   // the winner
            ["b2"] = JobStatus.Skipped,     // the loser
        };

        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add(d => d.StatusByStepId, statusMap));

        cut.FindAll("div.dag-node--completed").Should().NotBeEmpty("the winning branch is green");
        cut.FindAll("div.dag-node--skipped").Should().HaveCount(1, "the losing branch is greyed skipped");
    }

    [Fact]
    public async Task Decision_BranchClick_ResolvesToBranchJobStep()
    {
        BatchStep? selected = null;
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        // Find the branch node whose title contains its job name and click it.
        var branchNode = cut.FindAll("div.dag-node")
            .First(n => n.TextContent.Contains("Express", StringComparison.Ordinal));
        await branchNode.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("b1", "clicking a branch resolves to that branch's synthesized job step");
        selected.StepType.Should().Be(BatchStepType.Job);
        selected.Job!.JobName.Should().Be("Express");
    }

    [Fact]
    public void Decision_ClickingDiamond_ResolvesToDecisionStep()
    {
        BatchStep? selected = null;
        var cut = RenderComponent<DagView>(p => p
            .Add(d => d.Steps, SampleDecision())
            .Add(d => d.OnFailureSteps, Array.Empty<BatchStep>())
            .Add<BatchStep>(d => d.OnNodeSelected, s => { selected = s; }));

        cut.Find("div.dag-node--decision").Click();

        selected.Should().NotBeNull();
        selected!.StepId.Should().Be("dec");
        selected.StepType.Should().Be(BatchStepType.Decision);
    }
}
