using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Api.Batches;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Locks the <see cref="BatchWizardModel"/> ⇄ request/DTO Metadata carry. The visual
/// Editor relies on this: it serializes layout hints into <c>Metadata</c> then projects to a
/// create/update request on Save. The create path silently dropped Metadata (<c>= null</c>) until the
/// — these tests pin all three projection directions so it can't regress.
/// </summary>
public sealed class BatchWizardModelTests
{
    private static Dictionary<string, object?> Hints() =>
        new(StringComparer.Ordinal) { ["dashboard.layoutHints"] = "{\"s1\":{\"x\":10,\"y\":20}}" };

    [Fact]
    public void ToCreateRequest_CarriesMetadata_SoVisualEditorLayoutPersistsOnFirstSave()
    {
        var hints = Hints();
        var model = new BatchWizardModel { Name = "b", Metadata = hints };

        model.ToCreateRequest(createdBy: null).Metadata.Should().BeSameAs(hints,
 "create-mode Save MUST carry layout hints — dropping them lost every dragged " +
            "node position on the first save of a visual-editor-created batch");
    }

    [Fact]
    public void ToCreateRequest_NullMetadata_StaysNull_ForWizardCreate()
    {
        // The Wizard never sets Metadata on create; it must still project null (no behavior change).
        new BatchWizardModel { Name = "b" }.ToCreateRequest(createdBy: null).Metadata.Should().BeNull();
    }

    [Fact]
    public void ToUpdateRequest_CarriesMetadata()
    {
        var hints = Hints();
        var model = new BatchWizardModel { Id = "id", Version = 3, Name = "b", Metadata = hints };

        model.ToUpdateRequest().Metadata.Should().BeSameAs(hints,
 "edit-mode Save carries hints so a layout change persists in one write");
    }

    [Fact]
    public void FromDefinition_CarriesMetadata()
    {
        var hints = Hints();
        var dto = new BatchDefinitionDto
        {
            Id = "id", Name = "b", Source = BatchSource.Dashboard, Version = 1,
            Steps = [], FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow, Metadata = hints,
        };

        BatchWizardModel.FromDefinition(dto).Metadata.Should().BeSameAs(hints,
 "edit-load hydrates Metadata so a subsequent save round-trips operator-set hints ");
    }

    [Fact]
    public void FromDefinition_ApprovalGateTimeout_RoundTripsIntoDraft()
    {
        // A persisted gate with a 30s timeout must hydrate the editable seconds field on edit-load, so
        // the operator sees (and keeps) the value they configured — not a blank that silently drops it.
        var dto = new BatchDefinitionDto
        {
            Id = "id", Name = "b", Source = BatchSource.Dashboard, Version = 1,
            FailurePolicy = BatchFailurePolicy.StopOnFailure,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Steps = new[]
            {
                new BatchStep
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
                },
            },
        };

        var draft = BatchWizardModel.FromDefinition(dto).Steps.Single();

        draft.TimeoutSecondsApproval.Should().Be(30, "edit-load must not lose the configured gate timeout");
        draft.OnTimeout.Should().Be(ApprovalTimeoutAction.AutoApprove);
    }

    // ── compensation policy helpers ────────────
    // EnsureCompensatePolicy (flip-on-first-add) + ShouldWarnEmptyCompensate (warn-don't-revert) are
    // the two pure helpers extracted from the Editor's drop/remove handlers so they are unit-testable.

    [Fact]
    public void EnsureCompensatePolicy_FromStopOnFailure_FlipsToCompensate_ReturnsTrue()
    {
        var model = new BatchWizardModel { FailurePolicy = BatchFailurePolicy.StopOnFailure };

        var flipped = model.EnsureCompensatePolicy();

        flipped.Should().BeTrue("the first compensation step flips StopOnFailure→Compensate (caller notifies)");
        model.FailurePolicy.Should().Be(BatchFailurePolicy.Compensate,
            "the policy is now Compensate so the onFailure steps actually run");
    }

    [Fact]
    public void EnsureCompensatePolicy_AlreadyCompensate_NoOp_ReturnsFalse()
    {
        var model = new BatchWizardModel { FailurePolicy = BatchFailurePolicy.Compensate };

        var flipped = model.EnsureCompensatePolicy();

        flipped.Should().BeFalse("idempotent: already Compensate ⇒ no flip, no second notification");
        model.FailurePolicy.Should().Be(BatchFailurePolicy.Compensate, "policy unchanged");
    }

    [Theory]
    // (onFailureCount, policy, expectedWarn)
    [InlineData(0, BatchFailurePolicy.Compensate, true)]      // empty + Compensate ⇒ warn (server degrades on save)
    [InlineData(2, BatchFailurePolicy.Compensate, false)]     // non-empty + Compensate ⇒ no warn (valid)
    [InlineData(0, BatchFailurePolicy.StopOnFailure, false)]  // empty + StopOnFailure ⇒ no warn (consistent)
    [InlineData(1, BatchFailurePolicy.StopOnFailure, false)]  // steps present but policy not Compensate ⇒ no warn
    public void ShouldWarnEmptyCompensate_OnlyWhenEmptyAndCompensate(
        int onFailureCount, BatchFailurePolicy policy, bool expectedWarn)
    {
        var model = new BatchWizardModel { FailurePolicy = policy };
        for (int i = 0; i < onFailureCount; i++)
        {
            model.OnFailureSteps.Add(new WizardStepDraft { StepType = BatchStepType.Job });
        }

        model.ShouldWarnEmptyCompensate().Should().Be(expectedWarn,
            "warn ONLY when OnFailureSteps is empty AND the policy is Compensate (server-degrade-on-save signal)");
    }

    [Fact]
    public void ShouldWarnEmptyCompensate_DoesNotMutate()
    {
        // warn-don't-revert: the canvas warns but must NOT auto-revert the operator's explicit
        // Compensate policy — removing the last compensation step keeps the policy as-is.
        var model = new BatchWizardModel { FailurePolicy = BatchFailurePolicy.Compensate };

        var warned = model.ShouldWarnEmptyCompensate();

        warned.Should().BeTrue("empty + Compensate ⇒ the helper reports a warning");
        model.FailurePolicy.Should().Be(BatchFailurePolicy.Compensate,
            "ShouldWarnEmptyCompensate is non-mutating — it warns, it does NOT revert the policy");
        model.OnFailureSteps.Should().BeEmpty("the helper does not touch the step list either");
    }
}
