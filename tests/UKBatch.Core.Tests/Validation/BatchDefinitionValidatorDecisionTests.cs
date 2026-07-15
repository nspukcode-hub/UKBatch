using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Validation;
using Xunit;

namespace UKBatch.Core.Tests.Validation;

/// <summary>
/// Validator rules for a <see cref="BatchStepType.Decision"/> step: a non-null payload with at least one
/// branch, each branch a non-blank id (not ending in the reserved compensator suffix) and a non-blank job
/// name, at most one else (unconditional) branch which must be last, the shared run-if condition-shape rules
/// on each branch's <c>When</c>, and — the resume-durability-critical one — branch ids participating in the
/// definition's step-id uniqueness space so a branch id can never collide with a top-level step id (which
/// would map a skipped-loser row to the wrong step and corrupt a Compensate unwind).
/// </summary>
public class BatchDefinitionValidatorDecisionTests
{
    private static BatchDefinition WithDecision(params DecisionBranch[] branches) => new()
    {
        Id = "b1",
        Name = "dec",
        Source = BatchSource.Code,
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
        Steps = new[]
        {
            new BatchStep
            {
                StepId = "dec",
                Order = 0,
                StepType = BatchStepType.Decision,
                Decision = new DecisionStepData { Branches = branches },
            },
        },
    };

    private static DecisionBranch Branch(string stepId, string jobName, StepCondition? when) => new()
    {
        StepId = stepId,
        When = when,
        Job = new JobStepData { JobName = jobName },
    };

    private static StepCondition Cond(ConditionOperator op, string? value = null) =>
        new() { ParameterKey = "amount", Operator = op, Value = value };

    [Fact]
    public void Validate_ValidDecision_Succeeds()
    {
        var def = WithDecision(
            Branch("b0", "express", Cond(ConditionOperator.GreaterThan, "1000")),
            Branch("b1", "standard", when: null));   // else, last
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_DecisionPayloadNull_Fails()
    {
        var def = new BatchDefinition
        {
            Id = "b1",
            Name = "dec",
            Source = BatchSource.Code,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
            Steps = new[]
            {
                new BatchStep { StepId = "dec", Order = 0, StepType = BatchStepType.Decision, Decision = null },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision");
    }

    [Fact]
    public void Validate_ZeroBranches_Fails()
    {
        var def = WithDecision();
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches");
    }

    [Fact]
    public void Validate_BlankBranchStepId_Fails()
    {
        var def = WithDecision(Branch("   ", "j", when: null));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[0].StepId");
    }

    [Fact]
    public void Validate_BranchStepIdEndingInReservedCompensatorSuffix_Fails()
    {
        // A branch id ending in ":comp" would collide with a derived compensator id and corrupt the resume
        // skip/dedupe mapping — reject it up front.
        var def = WithDecision(Branch("ship:comp", "j", when: null));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[0].StepId"
            && e.Message.Contains(":comp", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BlankBranchJobName_Fails()
    {
        var def = WithDecision(Branch("b0", "   ", when: null));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[0].Job.JobName");
    }

    [Fact]
    public void Validate_TwoElseBranches_Fails()
    {
        var def = WithDecision(
            Branch("b0", "a", when: null),   // else
            Branch("b1", "b", when: null));  // second else — illegal
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[1].When"
            && e.Message.Contains("at most one else", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ElseNotLast_Fails()
    {
        var def = WithDecision(
            Branch("b0", "a", when: null),                                     // else — but not last
            Branch("b1", "b", Cond(ConditionOperator.GreaterThan, "1000")));   // conditional after the else
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[1].When"
            && e.Message.Contains("must be the last branch", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BranchConditionMissingComparand_Fails()
    {
        // A comparison operator with no comparand is a broken guard — the same rule the step-level condition
        // applies runs on a branch's When.
        var def = WithDecision(
            Branch("b0", "a", Cond(ConditionOperator.GreaterThan, value: null)),
            Branch("b1", "b", when: null));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[0].When.Value");
    }

    [Fact]
    public void Validate_BranchConditionNonNumericOrderingComparand_Fails()
    {
        var def = WithDecision(
            Branch("b0", "a", Cond(ConditionOperator.GreaterThan, "notanumber")),
            Branch("b1", "b", when: null));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Decision.Branches[0].When.Value");
    }

    [Fact]
    public void Validate_DuplicateBranchIds_Fails()
    {
        var def = WithDecision(
            Branch("dup", "a", Cond(ConditionOperator.GreaterThan, "1000")),
            Branch("dup", "b", when: null));   // same id as branch 0
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId" && e.Message.Contains("dup", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BranchIdCollidesWithTopLevelStepId_Fails()
    {
        // The resume-durability-critical rule: a branch id sharing a top-level step id would map the loser's
        // Skipped row to that top-level index and wrongly exclude it from a Compensate unwind — silent saga
        // corruption on a REST-controlled topology. Branch ids share the definition's uniqueness space.
        var def = new BatchDefinition
        {
            Id = "b1",
            Name = "dec",
            Source = BatchSource.Code,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
            Steps = new[]
            {
                new BatchStep { StepId = "shared", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                new BatchStep
                {
                    StepId = "dec",
                    Order = 1,
                    StepType = BatchStepType.Decision,
                    Decision = new DecisionStepData
                    {
                        Branches = new[]
                        {
                            Branch("shared", "a", Cond(ConditionOperator.GreaterThan, "1000")),   // collides with the top-level Job id
                            Branch("b1", "b", when: null),
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId" && e.Message.Contains("shared", StringComparison.Ordinal),
            "a branch id colliding with a top-level step id must be rejected");
    }
}
