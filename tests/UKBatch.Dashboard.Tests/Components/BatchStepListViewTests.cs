using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Bunit contract for the read-only <see cref="BatchStepListView"/> compensator annotation: a step with a
/// compensator shows a one-line "compensated by {JobName}" note (plus " @ {TargetService}" when cross-service).
/// </summary>
public sealed class BatchStepListViewTests : TestContext
{
    private static BatchStep Job(string id, string name, CompensationStepData? comp = null) => new()
    {
        StepId = id,
        Order = 0,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
        Compensation = comp,
    };

    [Fact]
    public void Step_WithLocalCompensator_ShowsCompensatedByNote()
    {
        var steps = new[] { Job("s1", "PlaceOrder", new CompensationStepData { JobName = "CancelOrder" }) };

        var cut = RenderComponent<BatchStepListView>(p => p.Add(v => v.Steps, steps));

        cut.Markup.Should().Contain("compensated by CancelOrder",
            "a step with a compensator shows a one-line annotation");
        cut.Markup.Should().NotContain("compensated by CancelOrder @",
            "a local compensator shows no service suffix");
    }

    [Fact]
    public void Step_WithCrossServiceCompensator_ShowsServiceSuffix()
    {
        var steps = new[]
        {
            Job("s1", "PlaceOrder", new CompensationStepData { JobName = "CancelOrder", TargetService = "billing" }),
        };

        var cut = RenderComponent<BatchStepListView>(p => p.Add(v => v.Steps, steps));

        cut.Markup.Should().Contain("compensated by CancelOrder @ billing",
            "a cross-service compensator shows its target service");
    }

    [Fact]
    public void Step_WithoutCompensator_ShowsNoNote()
    {
        var steps = new[] { Job("s1", "PlaceOrder") };

        var cut = RenderComponent<BatchStepListView>(p => p.Add(v => v.Steps, steps));

        cut.Markup.Should().NotContain("compensated by", "a step without a compensator has no annotation");
    }
}
