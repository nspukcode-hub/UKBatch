using FluentAssertions;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Builders;
using UKBatch.Core.Tests.Helpers;
using Xunit;

namespace UKBatch.Core.Tests.Builders;

/// <summary>
/// Fluent per-step compensator attachment. <c>CompensateWith&lt;TJob&gt;</c> /
/// <c>CompensateWith(string)</c> / <c>CompensateWithPartitioned&lt;TJob&gt;</c> populate
/// <see cref="BatchStep.Compensation"/> on top-level Job steps and (group-level) on ParallelGroup steps,
/// carrying the inner builder's OnService / WithParameters / WithMaxRetries / WithTimeout. Illegal
/// placements fail fast at build time: a compensator cannot itself have a compensator, a parallel-group
/// CHILD cannot carry one (the group is the atomic unit), and a failure-chain step cannot carry one
/// (no compensation of compensation).
/// </summary>
public class BatchBuilderCompensationTests
{
    private static readonly string CompJobName = typeof(SucceedingJob).FullName ?? typeof(SucceedingJob).Name;
    private static readonly string PartitionedJobName =
        typeof(CountingPartitionedJob).FullName ?? typeof(CountingPartitionedJob).Name;

    private static BatchDefinition Build(Action<BatchBuilder> configure)
    {
        var builder = new BatchBuilder(new UKBatchOptions());
        configure(builder);
        return builder.Build("id-1", "batch-1", DateTimeOffset.UtcNow);
    }

    // ===== job-step compensators =====

    [Fact]
    public void CompensateWith_Typed_PopulatesCompensation_WithTypeName()
    {
        var def = Build(b => b.RunJob("Primary", s => s.CompensateWith<SucceedingJob>()));

        var comp = def.Steps.Single().Compensation;
        comp.Should().NotBeNull();
        comp!.JobName.Should().Be(CompJobName, "the typed overload resolves the compensator by type name");
        comp.TargetService.Should().BeNull("no OnService was configured — the compensator is local");
        comp.Parameters.Should().BeNull();
        comp.MaxRetries.Should().BeNull();
        comp.TimeoutSeconds.Should().BeNull();
    }

    [Fact]
    public void CompensateWith_String_CarriesInnerBuilderConfiguration()
    {
        var def = Build(b => b.RunJob("Primary", s => s.CompensateWith("undo.job", c => c
            .OnService("billing")
            .WithParameters(new Dictionary<string, object?> { ["reason"] = "rollback" })
            .WithMaxRetries(2)
            .WithTimeout(30))));

        var comp = def.Steps.Single().Compensation!;
        comp.JobName.Should().Be("undo.job");
        comp.TargetService.Should().Be("billing", "the inner builder's OnService flows to the compensator");
        comp.Parameters.Should().ContainKey("reason").WhoseValue.Should().Be("rollback");
        comp.MaxRetries.Should().Be(2);
        comp.TimeoutSeconds.Should().Be(30);
    }

    [Fact]
    public void CompensateWithPartitioned_PopulatesCompensation_WithTypeName()
    {
        var def = Build(b => b.RunJob("Primary", s => s.CompensateWithPartitioned<CountingPartitionedJob>()));

        def.Steps.Single().Compensation!.JobName.Should().Be(PartitionedJobName,
            "the partitioned overload resolves the compensator by the partitioned job's type name");
    }

    [Fact]
    public void CompensateWith_TypedRunJobStep_PopulatesCompensation()
    {
        var def = Build(b => b.RunJob<SucceedingJob>(s => s.CompensateWith("undo.job")));

        def.Steps.Single().Compensation!.JobName.Should().Be("undo.job",
            "typed main steps carry compensators exactly like string-named steps");
    }

    // ===== group-level compensators =====

    [Fact]
    public void ParallelGroup_CompensateWith_Typed_PopulatesGroupStepCompensation()
    {
        var def = Build(b => b.ThenInParallel(g => g
            .RunJob("child-1")
            .RunJob("child-2")
            .CompensateWith<SucceedingJob>()));

        var groupStep = def.Steps.Single();
        groupStep.StepType.Should().Be(BatchStepType.ParallelGroup);
        groupStep.Compensation.Should().NotBeNull("the group-level compensator lands on the GROUP step");
        groupStep.Compensation!.JobName.Should().Be(CompJobName);
        groupStep.ParallelGroup!.Steps.Should().OnlyContain(c => c.Compensation == null,
            "children never carry a compensator — the group compensates as one unit");
    }

    [Fact]
    public void ParallelGroup_CompensateWith_String_CarriesInnerBuilderConfiguration()
    {
        var def = Build(b => b.ThenInParallel(g => g
            .RunJob("child-1")
            .RunJob("child-2")
            .CompensateWith("undo.group", c => c.OnService("shipping").WithMaxRetries(1))));

        var comp = def.Steps.Single().Compensation!;
        comp.JobName.Should().Be("undo.group");
        comp.TargetService.Should().Be("shipping");
        comp.MaxRetries.Should().Be(1);
    }

    [Fact]
    public void ParallelGroup_CompensateWithPartitioned_PopulatesGroupStepCompensation()
    {
        var def = Build(b => b.ThenInParallel(g => g
            .RunJob("child-1")
            .RunJob("child-2")
            .CompensateWithPartitioned<CountingPartitionedJob>()));

        def.Steps.Single().Compensation!.JobName.Should().Be(PartitionedJobName);
    }

    // ===== fail-fast placements =====

    [Fact]
    public void CompensateWith_NestedCompensator_Throws()
    {
        var act = () => Build(b => b.RunJob("Primary", s => s.CompensateWith("undo.job", c => c.CompensateWith("undo.undo"))));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*compensator cannot itself have a compensator*",
                "the saga unwind is acyclic — there is no compensation of compensation");
    }

    [Fact]
    public void ParallelChild_Compensator_Throws()
    {
        var act = () => Build(b => b.ThenInParallel(g => g
            .RunJob("child-1", s => s.CompensateWith("undo.child"))
            .RunJob("child-2")));

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*children cannot have compensators*",
                "the group is the atomic unit of compensation; a child-level compensator must fail fast");
    }

    [Fact]
    public void ParallelChild_TypedOverload_Compensator_Throws()
    {
        var act = () => Build(b => b.ThenInParallel(g => g
            .RunJob<SucceedingJob>(s => s.CompensateWith("undo.child"))
            .RunJob("child-2")));

        act.Should().Throw<InvalidOperationException>().WithMessage("*children cannot have compensators*");
    }

    [Fact]
    public void OnFailureStep_Compensator_Throws()
    {
        var act = () => Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.RunJob("chain.step", s => s.CompensateWith("undo.chain")));
        });

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*chain steps cannot have compensators*",
                "the failure chain already IS the failure response — compensating it would recurse");
    }

    [Fact]
    public void OnFailureStep_TypedOverload_Compensator_Throws()
    {
        var act = () => Build(b =>
        {
            b.RunJob("Primary");
            b.OnFailure(f => f.RunJob<SucceedingJob>(s => s.CompensateWith("undo.chain")));
        });

        act.Should().Throw<InvalidOperationException>().WithMessage("*chain steps cannot have compensators*");
    }
}
