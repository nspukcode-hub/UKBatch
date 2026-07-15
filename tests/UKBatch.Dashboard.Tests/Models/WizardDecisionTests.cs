using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Round-trip fidelity for a decision step through <see cref="WizardStepDraft.ToBatchStep"/> /
/// <see cref="WizardStepDraft.FromBatchStep"/>. Loading a builder-authored decision and re-saving it must
/// preserve every branch (condition, job, cross-service target, else) — the earlier <c>default</c> arm
/// marked a decision unsupported and blocked editing, so an edit would otherwise strip it.
/// </summary>
public sealed class WizardDecisionTests
{
    private static BatchStep Decision(params DecisionBranch[] branches) => new()
    {
        StepId = "dec",
        Order = 0,
        StepType = BatchStepType.Decision,
        Decision = new DecisionStepData { Branches = branches },
    };

    private static DecisionBranch Branch(string id, StepCondition? when, string jobName, string? target = null, string? label = null) => new()
    {
        StepId = id,
        Label = label,
        When = when,
        Job = new JobStepData { JobName = jobName, TargetService = target },
    };

    private static StepCondition Gt(string key, string value) => new()
    {
        ParameterKey = key,
        Operator = ConditionOperator.GreaterThan,
        Value = value,
    };

    [Fact]
    public void FromBatchStep_Decision_RehydratesBranches()
    {
        var step = Decision(
            Branch("b1", Gt("amount", "1000"), "Ship.Express", target: "shipping", label: "big"),
            Branch("b2", null, "Ship.Standard"));

        var draft = WizardStepDraft.FromBatchStep(step);

        draft.StepType.Should().Be(BatchStepType.Decision);
        draft.IsUnsupported.Should().BeFalse("the wizard understands decision steps now");
        draft.DecisionBranches.Should().HaveCount(2);
        draft.DecisionBranches[0].StepId.Should().Be("b1");
        draft.DecisionBranches[0].Label.Should().Be("big");
        draft.DecisionBranches[0].JobName.Should().Be("Ship.Express");
        draft.DecisionBranches[0].TargetService.Should().Be("shipping");
        draft.DecisionBranches[0].When!.ParameterKey.Should().Be("amount");
        draft.DecisionBranches[0].When!.Operator.Should().Be(ConditionOperator.GreaterThan);
        draft.DecisionBranches[0].When!.Value.Should().Be("1000");
        draft.DecisionBranches[1].When.Should().BeNull("the else branch has no condition");
        draft.DecisionBranches[1].JobName.Should().Be("Ship.Standard");
    }

    [Fact]
    public void Decision_RoundTrip_PreservesBranchesByteForByte()
    {
        var original = Decision(
            Branch("b1", Gt("amount", "1000"), "Ship.Express", target: "shipping", label: "big"),
            Branch("b2", null, "Ship.Standard"));

        var reprojected = WizardStepDraft.FromBatchStep(original).ToBatchStep(0);

        reprojected.Decision.Should().BeEquivalentTo(original.Decision,
            "a decision must survive an edit-load + re-save unchanged");
    }

    [Fact]
    public void ToBatchStep_Decision_EmptyBranches_EmitsEmptyDecisionPayload_DoesNotThrow()
    {
        var draft = new WizardStepDraft { StepType = BatchStepType.Decision };

        var act = () => draft.ToBatchStep(0);
        act.Should().NotThrow("the projection runs during render — a throw here tears down the circuit");

        var step = draft.ToBatchStep(0);
        step.Decision.Should().NotBeNull("a decision step always emits a payload (the validator flags 0 branches)");
        step.Decision!.Branches.Should().BeEmpty();
    }

    [Fact]
    public void ToBatchStep_Decision_BlankConditionKey_ProjectsElse()
    {
        // A branch whose condition has a blank parameter key is the else/default (mirrors BuildCondition):
        // the projection must NOT emit a half-formed condition.
        var draft = new WizardStepDraft
        {
            StepType = BatchStepType.Decision,
            DecisionBranches =
            {
                new DecisionBranchDraft
                {
                    StepId = "b1",
                    JobName = "Fallback",
                    When = new ConditionDraft { ParameterKey = "   ", Operator = ConditionOperator.Exists },
                },
            },
        };

        var step = draft.ToBatchStep(0);

        step.Decision!.Branches.Should().ContainSingle();
        step.Decision.Branches[0].When.Should().BeNull("a blank condition key projects to the else branch");
        step.Decision.Branches[0].Job.JobName.Should().Be("Fallback");
    }

    [Fact]
    public void ToBatchStep_Decision_RoundTripsDecisionLevelCompensatorAndCondition()
    {
        // A decision may carry a decision-level compensator + a run-if guard on the whole decision.
        var draft = new WizardStepDraft
        {
            StepType = BatchStepType.Decision,
            DecisionBranches = { new DecisionBranchDraft { StepId = "b1", JobName = "A", When = null } },
            Compensation = new CompensationDraft { JobName = "UndoDecision" },
            Condition = new ConditionDraft { ParameterKey = "region", Operator = ConditionOperator.Equals, Value = "EU" },
        };

        var step = draft.ToBatchStep(0);

        step.Compensation!.JobName.Should().Be("UndoDecision", "a decision compensates as one unit");
        step.Condition!.ParameterKey.Should().Be("region", "the whole decision can be guarded by a run-if condition");
    }
}
