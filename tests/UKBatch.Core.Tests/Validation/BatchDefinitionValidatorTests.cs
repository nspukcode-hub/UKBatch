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

    private static BatchDefinition WithGate(ApprovalTimeoutAction onTimeout, TimeSpan? timeoutAfter) =>
        MinimalValid() with
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
                        Title = "Confirm",
                        AllowedRoles = new[] { "ops" },
                        OnTimeout = onTimeout,
                        TimeoutAfter = timeoutAfter,
                    },
                },
            },
        };

    [Theory]
    [InlineData(ApprovalTimeoutAction.AutoApprove)]
    [InlineData(ApprovalTimeoutAction.Hold)]
    public void Validate_ApprovalGate_OnTimeoutNotFail_NoTimeout_Fails(ApprovalTimeoutAction onTimeout)
    {
        // AutoApprove/Hold with no timeout leaves the gate waiting forever, contradicting the action.
        var def = WithGate(onTimeout, timeoutAfter: null);
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Approval.Timeout");
    }

    [Fact]
    public void Validate_ApprovalGate_OnTimeoutNotFail_ZeroTimeout_Fails()
    {
        // A zero/negative timeout is treated as no timeout, so the same rule applies.
        var def = WithGate(ApprovalTimeoutAction.AutoApprove, timeoutAfter: TimeSpan.Zero);
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Approval.Timeout");
    }

    [Fact]
    public void Validate_ApprovalGate_FailWithNoTimeout_Succeeds()
    {
        // Fail + no timeout is a legitimate indefinite wait that only ends on a manual reject.
        var def = WithGate(ApprovalTimeoutAction.Fail, timeoutAfter: null);
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }

    [Theory]
    [InlineData(ApprovalTimeoutAction.AutoApprove)]
    [InlineData(ApprovalTimeoutAction.Hold)]
    [InlineData(ApprovalTimeoutAction.Fail)]
    public void Validate_ApprovalGate_AnyActionWithTimeout_Succeeds(ApprovalTimeoutAction onTimeout)
    {
        // Any action paired with a real duration is valid — the action has a time to fire.
        var def = WithGate(onTimeout, timeoutAfter: TimeSpan.FromSeconds(30));
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }

    [Fact]
    public void ApprovalGateConfig_OnTimeoutOmitted_DefaultsToFail_AndValidates()
    {
        // OnTimeout is no longer required: a config that omits it compiles and defaults to Fail,
        // which is a legitimate indefinite wait with no timeout (so the definition is valid).
        var config = new ApprovalGateConfig
        {
            Title = "Confirm",
            AllowedRoles = new[] { "ops" },
        };
        config.OnTimeout.Should().Be(ApprovalTimeoutAction.Fail);

        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep { StepId = "g", Order = 0, StepType = BatchStepType.ApprovalGate, Approval = config },
            },
        };
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue();
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

    [Fact]
    public void Validate_DuplicateTopLevelStepId_Fails()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep { StepId = "dup", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "a" } },
                new BatchStep { StepId = "dup", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "b" } },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId" && e.Message.Contains("dup", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_TopLevelStepIdReusedByParallelChild_Fails()
    {
        // A top-level step and a ParallelGroup child share an id within one definition.
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep { StepId = "shared", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "a" } },
                new BatchStep
                {
                    StepId = "g",
                    Order = 1,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps = new[]
                        {
                            new BatchStep { StepId = "shared", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "c1" } },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "c2" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId" && e.Message.Contains("shared", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_OnFailureStepReusesMainStepId_Fails()
    {
        // A compensation step reuses a main-sequence step id within one definition.
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            Steps = new[]
            {
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "main" } },
            },
            OnFailureSteps = new[]
            {
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "rollback" } },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId" && e.Message.Contains("s1", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UniqueStepIdsAcrossAllRegions_Succeeds()
    {
        // Distinct ids across top-level steps, parallel children, and compensation steps — the rule
        // must not flag a legitimately unique definition.
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            Steps = new[]
            {
                new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "a" } },
                new BatchStep
                {
                    StepId = "g",
                    Order = 1,
                    StepType = BatchStepType.ParallelGroup,
                    ParallelGroup = new ParallelGroupData
                    {
                        JoinPolicy = ParallelJoinPolicy.WaitAll,
                        Steps = new[]
                        {
                            new BatchStep { StepId = "c1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "c1" } },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "c2" } },
                        },
                    },
                },
            },
            OnFailureSteps = new[]
            {
                new BatchStep { StepId = "comp1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "rollback" } },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeTrue();
    }
}
