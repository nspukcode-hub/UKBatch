using FluentAssertions;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Builders;
using UKBatch.Core.Tests.Helpers;
using Xunit;

namespace UKBatch.Core.Tests.Builders;

/// <summary>
/// Fluent <see cref="BatchBuilder.Decide"/> / <see cref="DecisionBuilder"/> composition. A branch opens with
/// <c>When(...)</c> (a condition) or <c>Otherwise()</c> (the else/default) and closes with a <c>RunJob</c>
/// call; the builder projects to a <see cref="BatchStepType.Decision"/> step whose branch order is preserved.
/// A decision-level <c>RunIf</c> and <c>CompensateWith</c> ride onto the step. Illegal shapes fail fast at
/// build time: a second <c>Otherwise</c>, an else that is not last, a <c>RunJob</c> with no open branch, an
/// empty decision, and a branch job carrying its own compensator or run-if.
/// </summary>
public class DecisionBuilderTests
{
    private static readonly string JobName = typeof(SucceedingJob).FullName ?? typeof(SucceedingJob).Name;

    private static BatchDefinition Build(Action<BatchBuilder> configure)
    {
        var builder = new BatchBuilder(new UKBatchOptions());
        configure(builder);
        return builder.Build("id-1", "batch-1", DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Decide_ProjectsBranchesInOrder_WithConditionsAndElse()
    {
        var def = Build(b => b.Decide(d => d
            .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<SucceedingJob>()
            .When("amount", ConditionOperator.GreaterThan, 100).RunJob("standard")
            .Otherwise().RunJob("notify")));

        var step = def.Steps.Single();
        step.StepType.Should().Be(BatchStepType.Decision);
        var branches = step.Decision!.Branches;
        branches.Should().HaveCount(3);

        branches[0].When.Should().NotBeNull();
        branches[0].When!.ParameterKey.Should().Be("amount");
        branches[0].When!.Operator.Should().Be(ConditionOperator.GreaterThan);
        branches[0].When!.Value.Should().Be("1000", "the comparand is stored as a culture-invariant string");
        branches[0].Job.JobName.Should().Be(JobName, "the typed overload resolves the branch job by type name");

        branches[1].Job.JobName.Should().Be("standard");
        branches[2].When.Should().BeNull("Otherwise() opens the else/default branch");
        branches[2].Job.JobName.Should().Be("notify");

        branches.Select(br => br.StepId).Should().OnlyHaveUniqueItems().And.NotContainNulls();
    }

    [Fact]
    public void Decide_CrossServiceBranch_CarriesTargetService()
    {
        var def = Build(b => b.Decide(d => d
            .When("region", ConditionOperator.Equals, "EU").RunJob("remote", s => s.OnService("billing"))
            .Otherwise().RunJob<SucceedingJob>()));

        def.Steps.Single().Decision!.Branches[0].Job.TargetService.Should().Be("billing",
            "an inner OnService flows onto the branch job for a cross-service branch");
    }

    [Fact]
    public void Decide_RunIfAndCompensateWith_RideOntoTheStep()
    {
        var def = Build(b => b.Decide(d => d
            .When("tier", ConditionOperator.Equals, "gold").RunJob<SucceedingJob>()
            .Otherwise().RunJob<SucceedingJob>()
            .RunIf("enabled", ConditionOperator.IsTrue)
            .CompensateWith<SucceedingJob>()));

        var step = def.Steps.Single();
        step.Condition.Should().NotBeNull("the decision-level RunIf guards the whole decision");
        step.Condition!.ParameterKey.Should().Be("enabled");
        step.Compensation.Should().NotBeNull("the decision-level compensator rides onto the step");
        step.Compensation!.JobName.Should().Be(JobName);
    }

    [Fact]
    public void ThenDecide_IsAnAliasForDecide()
    {
        var def = Build(b => b
            .RunJob<SucceedingJob>()
            .ThenDecide(d => d
                .When("tier", ConditionOperator.Equals, "gold").RunJob<SucceedingJob>()
                .Otherwise().RunJob<SucceedingJob>()));

        def.Steps.Should().HaveCount(2);
        def.Steps[1].StepType.Should().Be(BatchStepType.Decision);
    }

    // ===== fail-fast shapes =====

    [Fact]
    public void Decide_EmptyDecision_Throws()
    {
        var act = () => Build(b => b.Decide(_ => { }));
        act.Should().Throw<InvalidOperationException>().WithMessage("*at least one branch*");
    }

    [Fact]
    public void Decide_RunJobWithNoOpenBranch_Throws()
    {
        var act = () => Build(b => b.Decide(d => d.RunJob<SucceedingJob>()));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Open a branch with When(...) or Otherwise() before RunJob*");
    }

    [Fact]
    public void Decide_OpenBranchLeftUnclosed_Throws()
    {
        // A When(...) opened but never closed with RunJob(...) is an incomplete branch.
        var act = () => Build(b => b.Decide(d => d.When("k", ConditionOperator.Exists)));
        act.Should().Throw<InvalidOperationException>().WithMessage("*last branch has no job*");
    }

    [Fact]
    public void Decide_OpeningASecondBranchWhileOneIsOpen_Throws()
    {
        var act = () => Build(b => b.Decide(d => d
            .When("k", ConditionOperator.Exists)
            .When("j", ConditionOperator.Exists)));   // second When before closing the first
        act.Should().Throw<InvalidOperationException>().WithMessage("*previous branch has no job*");
    }

    [Fact]
    public void Decide_SecondOtherwise_Throws()
    {
        var act = () => Build(b => b.Decide(d => d
            .Otherwise().RunJob<SucceedingJob>()
            .Otherwise().RunJob<SucceedingJob>()));
        act.Should().Throw<InvalidOperationException>().WithMessage("*at most one Otherwise*");
    }

    [Fact]
    public void Decide_OtherwiseNotLast_Throws()
    {
        var act = () => Build(b => b.Decide(d => d
            .Otherwise().RunJob<SucceedingJob>()
            .When("amount", ConditionOperator.GreaterThan, 1000).RunJob<SucceedingJob>()));
        act.Should().Throw<InvalidOperationException>().WithMessage("*Otherwise (else) branch must be the last*");
    }

    [Fact]
    public void Decide_BranchWithOwnCompensator_Throws()
    {
        var act = () => Build(b => b.Decide(d => d
            .When("tier", ConditionOperator.Equals, "gold").RunJob<SucceedingJob>(s => s.CompensateWith("undo"))
            .Otherwise().RunJob<SucceedingJob>()));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*branches cannot have their own compensator*",
                "the decision is the atomic compensation unit — a branch-level compensator must fail fast");
    }

    [Fact]
    public void Decide_BranchWithOwnRunIf_Throws()
    {
        var act = () => Build(b => b.Decide(d => d
            .When("tier", ConditionOperator.Equals, "gold").RunJob<SucceedingJob>(s => s.RunIf("k", ConditionOperator.Exists))
            .Otherwise().RunJob<SucceedingJob>()));
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*branches cannot have their own run-if condition*",
                "a branch's condition is its When(...) — a branch-level run-if must fail fast");
    }
}
