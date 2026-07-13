using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Locks the render-safe parameter projection in <see cref="WizardStepDraft.ToBatchStep"/>. The
/// conversion runs on the render path (the Review step and the visual editor canvas project drafts to
/// preview the DAG), so it must NEVER throw on the editor's raw rows — a thrown exception there tears
/// down the Blazor circuit and loses the unsaved batch. Blank-key rows are dropped; duplicate keys are
/// last-wins (dictionary semantics).
/// </summary>
public sealed class WizardStepDraftTests
{
    private static KeyValuePair<string, string> Param(string key, string value) => new(key, value);

    private static WizardStepDraft JobWith(params KeyValuePair<string, string>[] pairs) => new()
    {
        StepId = "s1",
        StepType = BatchStepType.Job,
        JobName = "Echo",
        Parameters = pairs.ToList(),
    };

    private static WizardStepDraft JobWithNoParameters() => new()
    {
        StepId = "s1",
        StepType = BatchStepType.Job,
        JobName = "Echo",
    };

    [Fact]
    public void ToBatchStep_DuplicateKeys_DoesNotThrow_LastValueWins()
    {
        var draft = JobWith(
            Param("k", "first"),
            Param("k", "second"));

        var act = () => draft.ToBatchStep(0);

        act.Should().NotThrow("the conversion runs during render — a throw here tears down the circuit");
        var step = draft.ToBatchStep(0);
        step.Job!.Parameters.Should().NotBeNull();
        step.Job!.Parameters.Should().ContainKey("k");
        step.Job!.Parameters!["k"].Should().Be("second", "duplicate keys resolve last-wins (dictionary semantics)");
        step.Job!.Parameters!.Should().HaveCount(1, "the two duplicate rows collapse to one key");
    }

    [Fact]
    public void ToBatchStep_BlankKeyRows_AreDropped()
    {
        var draft = JobWith(
            Param("real", "value"),
            Param("", "orphan-value"),
            Param("   ", "whitespace-key"));

        var step = draft.ToBatchStep(0);

        step.Job!.Parameters.Should().NotBeNull();
        step.Job!.Parameters!.Keys.Should().ContainSingle().Which.Should().Be("real",
            "blank/whitespace keys are dropped — they are just empty editor rows");
        step.Job!.Parameters!["real"].Should().Be("value");
    }

    [Fact]
    public void ToBatchStep_AllRowsBlank_ProjectsNullParameters()
    {
        var draft = JobWith(
            Param("", ""),
            Param("", ""));

        var step = draft.ToBatchStep(0);

        step.Job!.Parameters.Should().BeNull(
            "a step whose only rows are empty editor rows emits no Parameters (same as adding none)");
    }

    [Fact]
    public void ToBatchStep_NoParameters_ProjectsNullParameters()
    {
        var draft = JobWithNoParameters();

        var step = draft.ToBatchStep(0);

        step.Job!.Parameters.Should().BeNull("an empty parameter list emits null Parameters");
    }

    [Fact]
    public void ToBatchStep_DistinctKeys_AllPreserved()
    {
        var draft = JobWith(
            Param("a", "1"),
            Param("b", "2"));

        var step = draft.ToBatchStep(0);

        step.Job!.Parameters.Should().NotBeNull();
        step.Job!.Parameters!.Should().HaveCount(2);
        step.Job!.Parameters!["a"].Should().Be("1");
        step.Job!.Parameters!["b"].Should().Be("2");
    }

    // ── approval-gate timeout binding round-trip ─────────────────────────────────────────
    // The editor binds the timeout field to TimeoutSecondsApproval; these pin that the value survives
    // the projection to ApprovalGateConfig and back, so a configured timeout is never lost.

    private static WizardStepDraft GateDraft(int? timeoutSeconds, ApprovalTimeoutAction onTimeout) => new()
    {
        StepId = "gate-1",
        StepType = BatchStepType.ApprovalGate,
        ApprovalTitle = "Confirm",
        AllowedRoles = { "ops" },
        TimeoutSecondsApproval = timeoutSeconds,
        OnTimeout = onTimeout,
    };

    [Fact]
    public void ToBatchStep_ApprovalTimeoutSeconds_ProjectsToTimeoutAfter()
    {
        var draft = GateDraft(timeoutSeconds: 30, ApprovalTimeoutAction.AutoApprove);

        var step = draft.ToBatchStep(0);

        step.Approval!.TimeoutAfter.Should().Be(TimeSpan.FromSeconds(30),
            "the bound timeout must reach ApprovalGateConfig.TimeoutAfter");
        step.Approval!.OnTimeout.Should().Be(ApprovalTimeoutAction.AutoApprove);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void ToBatchStep_ApprovalTimeoutNullOrZero_ProjectsNullTimeoutAfter(int? timeoutSeconds)
    {
        // Empty or zero means "no timeout" — projected as a null TimeoutAfter (indefinite wait).
        var draft = GateDraft(timeoutSeconds, ApprovalTimeoutAction.Fail);

        var step = draft.ToBatchStep(0);

        step.Approval!.TimeoutAfter.Should().BeNull("an empty/zero timeout maps to no timeout");
    }

    // ── compensation round-trip (the load-bearing edit-safety change) ────────────────────
    // Loading a builder-authored batch and re-saving it MUST preserve its compensators exactly; the
    // earlier FromBatchStep ignored Compensation, so an edit silently stripped every compensator.

    private static readonly string[] CompParamKeys = { "reason", "ref" };

    private static BatchStep JobWithCompensation(
        string jobName = "PlaceOrder",
        string compJob = "CancelOrder",
        string? compTarget = null) => new()
    {
        StepId = "s1",
        Order = 0,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = jobName },
        Compensation = new CompensationStepData
        {
            JobName = compJob,
            TargetService = compTarget,
            Parameters = new Dictionary<string, object?>(StringComparer.Ordinal) { ["reason"] = "rollback", ["ref"] = "42" },
            MaxRetries = 3,
            TimeoutSeconds = 90,
        },
    };

    [Fact]
    public void FromBatchStep_Job_RehydratesCompensation()
    {
        var draft = WizardStepDraft.FromBatchStep(JobWithCompensation(compTarget: "billing"));

        draft.Compensation.Should().NotBeNull("edit-load MUST NOT drop a builder-authored compensator");
        draft.Compensation!.JobName.Should().Be("CancelOrder");
        draft.Compensation.TargetService.Should().Be("billing", "a cross-service compensator's target round-trips");
        draft.Compensation.MaxRetries.Should().Be(3);
        draft.Compensation.TimeoutSeconds.Should().Be(90);
        draft.Compensation.Parameters.Select(p => p.Key).Should().BeEquivalentTo(CompParamKeys);
    }

    [Fact]
    public void Compensation_RoundTrip_Job_PreservesCompensatorByteForByte()
    {
        var original = JobWithCompensation(compTarget: "billing");

        // load → save must reproduce the compensator exactly (the lossy-edit regression guard).
        var reprojected = WizardStepDraft.FromBatchStep(original).ToBatchStep(0);

        reprojected.Compensation.Should().BeEquivalentTo(original.Compensation,
            "a compensator must survive an edit-load + re-save unchanged");
    }

    [Fact]
    public void Compensation_RoundTrip_ParallelGroup_PreservesGroupLevelCompensator()
    {
        var original = new BatchStep
        {
            StepId = "pg",
            Order = 0,
            StepType = BatchStepType.ParallelGroup,
            ParallelGroup = new ParallelGroupData
            {
                JoinPolicy = ParallelJoinPolicy.WaitAll,
                Steps = new[]
                {
                    new BatchStep { StepId = "c1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "A" } },
                    new BatchStep { StepId = "c2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = "B" } },
                },
            },
            Compensation = new CompensationStepData { JobName = "UndoBoth", TargetService = null, MaxRetries = 1 },
        };

        var reprojected = WizardStepDraft.FromBatchStep(original).ToBatchStep(0);

        reprojected.Compensation.Should().BeEquivalentTo(original.Compensation,
            "a group-level compensator must survive an edit-load + re-save unchanged");
    }

    [Fact]
    public void ToBatchStep_NoCompensation_EmitsNullCompensation()
    {
        // A step without a compensator must emit null (additive: a definition with no compensators is
        // byte-identical to before the feature).
        var step = JobWithNoParameters().ToBatchStep(0);

        step.Compensation.Should().BeNull("a step with no compensator draft emits no Compensation");
    }

    [Fact]
    public void ToBatchStep_BlankCompensatorJobName_EmitsNullCompensation()
    {
        // Render-safe: an enabled-but-unfinished compensator (blank job name) is dropped rather than
        // emitting an unrunnable step (mirrors the blank-parameter-row handling); the client validator
        // surfaces the blank up front so the operator finishes it before submit.
        var draft = JobWithNoParameters();
        draft.Compensation = new CompensationDraft { JobName = "   " };

        draft.ToBatchStep(0).Compensation.Should().BeNull("a blank compensator job name emits no Compensation");
    }

    [Fact]
    public void FromBatchStep_ApprovalGate_LeavesCompensationNull()
    {
        var gate = new BatchStep
        {
            StepId = "g1",
            Order = 0,
            StepType = BatchStepType.ApprovalGate,
            Approval = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "ops" }, OnTimeout = ApprovalTimeoutAction.Fail },
        };

        WizardStepDraft.FromBatchStep(gate).Compensation.Should().BeNull(
            "an ApprovalGate step never carries a compensator");
    }

    [Fact]
    public void FromBatchStep_ApprovalTimeoutAfter_HydratesTimeoutSeconds()
    {
        // Edit-load: a persisted 30s gate must round-trip back into the editable seconds field.
        var step = new BatchStep
        {
            StepId = "gate-1",
            Order = 0,
            StepType = BatchStepType.ApprovalGate,
            Approval = new ApprovalGateConfig
            {
                Title = "Confirm",
                AllowedRoles = new[] { "ops" },
                OnTimeout = ApprovalTimeoutAction.AutoApprove,
                TimeoutAfter = TimeSpan.FromSeconds(30),
            },
        };

        var draft = WizardStepDraft.FromBatchStep(step);

        draft.TimeoutSecondsApproval.Should().Be(30, "edit-load must not lose the configured timeout");
        draft.OnTimeout.Should().Be(ApprovalTimeoutAction.AutoApprove);
    }

    // ── run-if condition round-trip (mirrors the compensation tests: edit-load must not drop it) ──

    private static BatchStep JobWithCondition() => new()
    {
        StepId = "s1",
        Order = 0,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = "Ship" },
        Condition = new StepCondition { ParameterKey = "amount", Operator = ConditionOperator.GreaterThan, Value = "1000" },
    };

    [Fact]
    public void FromBatchStep_Job_RehydratesCondition()
    {
        var draft = WizardStepDraft.FromBatchStep(JobWithCondition());
        draft.Condition.Should().NotBeNull("edit-load MUST NOT drop a builder-authored condition");
        draft.Condition!.ParameterKey.Should().Be("amount");
        draft.Condition.Operator.Should().Be(ConditionOperator.GreaterThan);
        draft.Condition.Value.Should().Be("1000");
    }

    [Fact]
    public void Condition_RoundTrip_Job_PreservesByteForByte()
    {
        var original = JobWithCondition();
        var reprojected = WizardStepDraft.FromBatchStep(original).ToBatchStep(0);
        reprojected.Condition.Should().BeEquivalentTo(original.Condition,
            "a round-trip through the draft must not alter the condition");
    }

    [Fact]
    public void Condition_RoundTrip_ApprovalGate_PreservesCondition()
    {
        // Unlike compensation, a condition IS allowed on an ApprovalGate (a conditional approval), so it
        // must survive edit-load round-trip.
        var gate = new BatchStep
        {
            StepId = "g1",
            Order = 0,
            StepType = BatchStepType.ApprovalGate,
            Approval = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "ops" } },
            Condition = new StepCondition { ParameterKey = "amount", Operator = ConditionOperator.GreaterThanOrEqual, Value = "10000" },
        };
        var reprojected = WizardStepDraft.FromBatchStep(gate).ToBatchStep(0);
        reprojected.Condition.Should().BeEquivalentTo(gate.Condition);
    }

    [Fact]
    public void ToBatchStep_NoCondition_EmitsNullCondition()
    {
        var draft = new WizardStepDraft { StepType = BatchStepType.Job, JobName = "Ship" };
        draft.ToBatchStep(0).Condition.Should().BeNull("a step with no condition draft emits no Condition");
    }

    [Fact]
    public void ToBatchStep_BlankParameterKey_EmitsNullCondition()
    {
        var draft = new WizardStepDraft { StepType = BatchStepType.Job, JobName = "Ship" };
        draft.Condition = new ConditionDraft { ParameterKey = "   ", Operator = ConditionOperator.Exists };
        draft.ToBatchStep(0).Condition.Should().BeNull("a blank parameter key emits no Condition");
    }
}
