using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Validation;
using Xunit;

namespace UKBatch.Core.Tests.Validation;

/// <summary>
/// Validator rules for run-if <see cref="StepCondition"/>: a non-blank ParameterKey, a defined operator,
/// a comparand for the comparison operators (but not for presence/boolean ones), and the top-level-only
/// placement rule (rejected on parallel children and OnFailure steps).
/// </summary>
public class BatchDefinitionValidatorConditionTests
{
    private static BatchDefinition WithTopLevelCondition(StepCondition condition) => new()
    {
        Id = "b1",
        Name = "cond",
        Source = BatchSource.Code,
        FailurePolicy = BatchFailurePolicy.StopOnFailure,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Version = 1,
        Steps = new[]
        {
            new BatchStep
            {
                StepId = "s1",
                Order = 0,
                StepType = BatchStepType.Job,
                Job = new JobStepData { JobName = "j" },
                Condition = condition,
            },
        },
    };

    [Fact]
    public void ComparisonOperator_WithValue_Succeeds()
    {
        var def = WithTopLevelCondition(new StepCondition
        {
            ParameterKey = "amount",
            Operator = ConditionOperator.GreaterThan,
            Value = "1000",
        });
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
    }

    [Fact]
    public void ComparisonOperator_MissingValue_Fails()
    {
        var def = WithTopLevelCondition(new StepCondition
        {
            ParameterKey = "amount",
            Operator = ConditionOperator.GreaterThan,
            Value = null,
        });
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Condition.Value");
    }

    [Fact]
    public void BlankParameterKey_Fails()
    {
        var def = WithTopLevelCondition(new StepCondition
        {
            ParameterKey = "   ",
            Operator = ConditionOperator.Exists,
        });
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Condition.ParameterKey");
    }

    [Theory]
    [InlineData(ConditionOperator.Exists)]
    [InlineData(ConditionOperator.NotExists)]
    [InlineData(ConditionOperator.IsTrue)]
    [InlineData(ConditionOperator.IsFalse)]
    public void PresenceAndBooleanOperators_NeedNoValue_Succeed(ConditionOperator op)
    {
        var def = WithTopLevelCondition(new StepCondition { ParameterKey = "k", Operator = op, Value = null });
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(ConditionOperator.GreaterThan)]
    [InlineData(ConditionOperator.GreaterThanOrEqual)]
    [InlineData(ConditionOperator.LessThan)]
    [InlineData(ConditionOperator.LessThanOrEqual)]
    public void OrderingOperator_NonNumericValue_Fails(ConditionOperator op)
    {
        // An ordering operator with a non-numeric comparand would evaluate false on every run — a guard that
        // silently never fires. The validator must reject it up front.
        var def = WithTopLevelCondition(new StepCondition { ParameterKey = "amount", Operator = op, Value = "notanumber" });
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Condition.Value");
    }

    [Fact]
    public void OrderingOperator_NumericValue_Succeeds()
    {
        var def = WithTopLevelCondition(new StepCondition { ParameterKey = "amount", Operator = ConditionOperator.GreaterThan, Value = "1000.5" });
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
    }

    [Fact]
    public void UndefinedOperator_Fails()
    {
        var def = WithTopLevelCondition(new StepCondition
        {
            ParameterKey = "k",
            Operator = (ConditionOperator)999,
            Value = "x",
        });
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Condition.Operator");
    }

    [Fact]
    public void Condition_OnTopLevelApprovalGate_Succeeds()
    {
        // Unlike compensation, a condition IS allowed on an ApprovalGate (a conditional approval).
        var def = new BatchDefinition
        {
            Id = "b1",
            Name = "cond",
            Source = BatchSource.Code,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ApprovalGate,
                    Approval = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "ops" } },
                    Condition = new StepCondition { ParameterKey = "amount", Operator = ConditionOperator.GreaterThan, Value = "10000" },
                },
            },
        };
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
    }

    [Fact]
    public void Condition_OnParallelChild_Fails()
    {
        var def = new BatchDefinition
        {
            Id = "b1",
            Name = "cond",
            Source = BatchSource.Code,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps = new[]
                        {
                            new BatchStep
                            {
                                StepId = "c1",
                                Order = 0,
                                StepType = BatchStepType.Job,
                                Job = new JobStepData { JobName = "a" },
                                Condition = new StepCondition { ParameterKey = "k", Operator = ConditionOperator.Exists },
                            },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "b" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].ParallelGroup.Steps[0].Condition");
    }

    [Fact]
    public void Condition_OnOnFailureStep_Fails()
    {
        var def = new BatchDefinition
        {
            Id = "b1",
            Name = "cond",
            Source = BatchSource.Code,
            FailurePolicy = BatchFailurePolicy.Compensate,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Version = 1,
            Steps = new[]
            {
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
            },
            OnFailureSteps = new[]
            {
                new BatchStep
                {
                    StepId = "comp1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "rollback" },
                    Condition = new StepCondition { ParameterKey = "k", Operator = ConditionOperator.Exists },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "OnFailureSteps[0].Condition");
    }
}
