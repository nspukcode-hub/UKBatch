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
    public void Validate_StepIdEndingInReservedCompensatorSuffix_Fails()
    {
        // A real step id must not end with the compensator suffix: that namespace is reserved for the
        // derived id a compensator's execution rows and dashboard node carry. A REST-supplied definition
        // reusing it would make the parent-vs-compensator derivation ambiguous.
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep { StepId = "cleanup:comp", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].StepId"
            && e.Message.Contains(":comp", StringComparison.Ordinal));
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

    // ===== per-step compensator placement rules =====

    private static CompensationStepData Compensator(string jobName = "undo") => new() { JobName = jobName };

    [Fact]
    public void Validate_CompensationOnTopLevelJob_Succeeds()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "j" },
                    Compensation = Compensator(),
                },
            },
        };
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue(
            "a top-level Job step is a legal compensator carrier");
    }

    [Fact]
    public void Validate_CompensationOnTopLevelParallelGroup_Succeeds()
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
                            new BatchStep { StepId = "c1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                    Compensation = Compensator(),
                },
            },
        };
        BatchDefinitionValidator.Validate(def).IsValid.Should().BeTrue(
            "a top-level ParallelGroup compensates as one unit and may carry a group-level compensator");
    }

    [Fact]
    public void Validate_CompensationOnApprovalGate_Rejected()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "gate",
                    Order = 0,
                    StepType = BatchStepType.ApprovalGate,
                    Approval = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "ops" } },
                    Compensation = Compensator(),
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Compensation"
            && e.Message.Contains("ApprovalGate", StringComparison.Ordinal),
            "an approval gate has no work to undo — a compensator on it is rejected at its own path");
    }

    [Fact]
    public void Validate_CompensationOnParallelChild_Rejected()
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
                            new BatchStep
                            {
                                StepId = "c1",
                                Order = 0,
                                StepType = BatchStepType.Job,
                                Job = new JobStepData { JobName = "j" },
                                Compensation = Compensator(),   // illegal: children are not compensation units
                            },
                            new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "j" } },
                        },
                    },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].ParallelGroup.Steps[0].Compensation"
            && e.Message.Contains("not allowed here", StringComparison.Ordinal),
            "a hand-built/REST definition placing a compensator on a parallel CHILD must be rejected — " +
            "silently ignoring it would fake wired-up cleanup");
    }

    [Fact]
    public void Validate_CompensationOnOnFailureStep_Rejected()
    {
        var def = MinimalValid() with
        {
            FailurePolicy = BatchFailurePolicy.Compensate,
            OnFailureSteps = new[]
            {
                new BatchStep
                {
                    StepId = "chain1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "rollback" },
                    Compensation = Compensator(),   // illegal: no compensation of compensation
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "OnFailureSteps[0].Compensation"
            && e.Message.Contains("not allowed here", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BlankCompensatorJobName_Rejected()
    {
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "j" },
                    Compensation = Compensator("   "),
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "Steps[0].Compensation.JobName",
            "a blank compensator job name would only surface as a silent runtime failure mid-unwind");
    }

    [Fact]
    public void Validate_DerivedCompensatorId_CollidesWithManualStepId_Rejected()
    {
        // A compensator's execution rows are correlated by the derived id "{parent}:comp". A hand-built
        // definition declaring a REAL step with that exact id would make the correlation ambiguous.
        var def = MinimalValid() with
        {
            Steps = new[]
            {
                new BatchStep
                {
                    StepId = "s1",
                    Order = 0,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "j" },
                    Compensation = Compensator(),
                },
                new BatchStep
                {
                    StepId = CompensationStepIds.For("s1"),
                    Order = 1,
                    StepType = BatchStepType.Job,
                    Job = new JobStepData { JobName = "j" },
                },
            },
        };
        var result = BatchDefinitionValidator.Validate(def);
        result.IsValid.Should().BeFalse();
        result.Errors.Should().Contain(e => e.PropertyPath == "StepId"
            && e.Message.Contains(CompensationStepIds.For("s1"), StringComparison.Ordinal),
            "the derived compensator id lives in the same uniqueness space as declared step ids");
    }
}
