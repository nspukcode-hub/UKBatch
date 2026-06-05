using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Validation;
using Xunit;

namespace UKBatch.Core.Tests.Validation;

/// <summary>
/// S13 invariants: defined-enum checks + WaitMajority count guard + nested-parallel rejection.
/// </summary>
public class BatchDefinitionValidatorTests
{
    private static BatchDefinition MinimalValid() => new()
    {
        Id = "b1",
        Name = "minimal",
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
            },
        },
    };

    [Fact]
    public void Validate_MinimalDefinition_Succeeds()
    {
        var def = MinimalValid();
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyId_Fails()
    {
        var def = MinimalValid() with { Id = "" };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Id");
    }

    [Fact]
    public void Validate_EmptyName_Fails()
    {
        var def = MinimalValid() with { Name = "" };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Name");
    }

    [Fact]
    public void Validate_EmptySteps_Fails()
    {
        var def = MinimalValid() with { Steps = Array.Empty<BatchStep>() };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps");
    }

    [Fact]
    public void Validate_UndefinedFailurePolicy_Fails()
    {
        var def = MinimalValid() with { FailurePolicy = (BatchFailurePolicy)999 };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "FailurePolicy");
    }

    [Fact]
    public void Validate_NestedParallelGroup_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "outer",
                    Order = 0,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps = new[]
                        {
                            new BatchStep
                            {
                                StepId = "inner",
                                Order = 0,
                                StepType = BatchStepType.ParallelGroup, // nested!
                                ParallelGroup = new ParallelGroupData
                                {
                                    JoinPolicy = ParallelJoinPolicy.WaitAll,
                                    Steps = new[]
                                    {
                                        new BatchStep { StepId = "j1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                                        new BatchStep { StepId = "j2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                                    },
                                },
                            },
                            new BatchStep { StepId = "j2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };

        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("Nested ParallelGroup", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Validate_ParallelGroupWithOneChild_Fails()
    {
        var def = MinimalValid() with
        {
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
                            new BatchStep { StepId = "j", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains(">=2 children", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WaitMajorityWith2Children_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitMajority,
                        Steps = new[]
                        {
                            new BatchStep { StepId = "j1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                            new BatchStep { StepId = "j2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Message.Contains("WaitMajority requires >=3 children", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_WaitMajorityWith3Children_Succeeds()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitMajority,
                        Steps = new[]
                        {
                            new BatchStep { StepId = "j1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                            new BatchStep { StepId = "j2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                            new BatchStep { StepId = "j3", Order = 2, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_UndefinedJoinPolicy_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = (ParallelJoinPolicy)999,
                        Steps = new[]
                        {
                            new BatchStep { StepId = "j1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                            new BatchStep { StepId = "j2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath.Contains("JoinPolicy", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_JobStepMissingJobPayload_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = null },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_ApprovalGateMissingTitle_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "g",
                    Order = 0,
                    StepType = BatchStepType.ApprovalGate,
                    Approval = new ApprovalGateConfig
                    {
                        Title = "",
                        AllowedRoles = new[] { "admin" },
                        OnTimeout = ApprovalTimeoutAction.Fail,
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
    }

    [Fact]
    public void Validate_OnFailureStepBlankJobName_Fails()
    {
        // Compensation steps run via the same dispatch as the main sequence, so a blank JobName must
        // be rejected at validation rather than failing silently at runtime.
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            OnFailureSteps = new[]
            {
                new BatchStep
                {
                    StepId = "comp1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "   " },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "OnFailureSteps[0].Job.JobName");
    }

    [Fact]
    public void Validate_OnFailureStepMissingJobPayload_Fails()
    {
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            OnFailureSteps = new[]
            {
                new BatchStep { StepId = "comp1", Order = 0, StepType = BatchStepType.Job, Job = null },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "OnFailureSteps[0].Job");
    }

    [Fact]
    public void Validate_ValidOnFailureSteps_Succeeds()
    {
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            OnFailureSteps = new[]
            {
                new BatchStep
                {
                    StepId = "comp1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "rollback" },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void Validate_EmptyOnFailureSteps_Succeeds()
    {
        // The default empty compensation list stays valid — the new loop must not regress this.
        var def = MinimalValid() with { OnFailureSteps = Array.Empty<BatchStep>() };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }
}
