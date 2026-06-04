using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Inline approve/reject in the live RunDetail node inspector
/// (<see cref="DagNodeDetailPanel"/>). The panel stays PRESENTATIONAL: it renders the decision UI
/// only for a live pending gate and raises <c>OnApprove</c> / <c>OnReject</c>; the parent owns the
/// REST call + error handling. These tests pin the panel contract in isolation.
/// </summary>
public sealed class DagNodeDetailPanelDecisionTests : TestContext
{
    public DagNodeDetailPanelDecisionTests()
    {
        // The panel injects IJSRuntime (Copy JSON). No JS call fires in these tests, but Loose mode
        // matches the production graceful-degradation posture and avoids STRICT-mode surprises.
        JSInterop.Mode = Bunit.JSRuntimeMode.Loose;
    }

    private static BatchStep Gate(string id = "gate-1", string title = "Release approval") => new()
    {
        StepId = id,
        Order = 0,
        StepType = BatchStepType.ApprovalGate,
        Approval = new ApprovalGateConfig
        {
            Title = title,
            AllowedRoles = new[] { "ops" },
            OnTimeout = ApprovalTimeoutAction.Fail,
        },
    };

    private static BatchStep Job(string id = "job-1", string name = "DoWork") => new()
    {
        StepId = id,
        Order = 0,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = name },
    };

    // ── pending gate → decision section renders ─────────────────────────────────────────

    [Fact]
    public void PendingGate_RendersDecisionSection_ApproveAndReject()
    {
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1"));

        cut.Markup.Should().Contain("Decision", "the decision section heads the gate content for a live gate");
        // Approve button + a reason input + Reject button.
        cut.FindAll("button.btn--primary").Should().NotBeEmpty();
        cut.FindAll("input.form-field__input").Should().HaveCount(1);
        cut.FindAll("button.btn--danger").Should().NotBeEmpty();
    }

    [Fact]
    public async Task ApproveClick_FiresOnApprove()
    {
        var approved = false;
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1")
            .Add(d => d.OnApprove, () => { approved = true; }));

        await cut.Find("button.btn--primary").ClickAsync(new());

        approved.Should().BeTrue("clicking Approve raises OnApprove");
    }

    // ── reject reason validation ────────────────────────────────────────────────────────

    [Fact]
    public async Task RejectClick_EmptyReason_DoesNotFire_ButtonDisabled()
    {
        var rejectedReason = (string?)null;
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1")
            .Add(d => d.OnReject, (string r) => { rejectedReason = r; }));

        var rejectBtn = cut.Find("button.btn--danger");
        rejectBtn.HasAttribute("disabled").Should().BeTrue("an empty reason disables Reject");
        cut.Markup.Should().Contain("Reason is required");

        // Even if forced, a disabled-state click must not raise OnReject.
        await rejectBtn.ClickAsync(new());
        rejectedReason.Should().BeNull();
    }

    [Fact]
    public async Task RejectClick_WithReason_FiresOnRejectWithThatReason()
    {
        var rejectedReason = (string?)null;
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1")
            .Add(d => d.OnReject, (string r) => { rejectedReason = r; }));

        cut.Find("input.form-field__input").Input("budget exceeded");
        cut.Find("button.btn--danger").HasAttribute("disabled").Should().BeFalse("a valid reason enables Reject");

        await cut.Find("button.btn--danger").ClickAsync(new());

        rejectedReason.Should().Be("budget exceeded", "OnReject carries the typed reason");
    }

    // ── busy + error surfacing ──────────────────────────────────────────────────────────

    [Fact]
    public void DecisionBusy_DisablesBothButtons()
    {
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1")
            .Add(d => d.DecisionBusy, true));

        cut.Find("button.btn--primary").HasAttribute("disabled").Should().BeTrue();
        cut.Find("button.btn--danger").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void DecisionError_RendersErrorLine()
    {
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1")
            .Add(d => d.DecisionError, "Not authorized to decide this gate (role mismatch)."));

        var err = cut.Find("span.form-field__error");
        err.TextContent.Should().Contain("Not authorized to decide this gate (role mismatch).");
    }

    // ── no decision section when not pending ────────────────────────────────────────────

    [Fact]
    public void Gate_NoPendingId_NoDecisionSection()
    {
        // Same gate, but no pending id (static view / already resolved) → exactly the read-only layout.
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.Completed));

        cut.Markup.Should().NotContain("Decision", "no decision UI without a pending approval id");
        cut.FindAll("button.btn--primary").Should().BeEmpty();
        cut.FindAll("button.btn--danger").Should().BeEmpty();
        // The read-only gate content is still present.
        cut.Markup.Should().Contain("Approval gate").And.Contain("Allowed roles");
    }

    [Fact]
    public void Gate_PendingIdButStatusNotAwaiting_NoDecisionSection()
    {
        // Defense in depth: an id without the AwaitingApproval status must NOT show the decision UI
        // (e.g. the gate just resolved but the id map hasn't been rebuilt yet).
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate())
            .Add(d => d.Status, JobStatus.Completed)
            .Add(d => d.PendingApprovalId, "appr-1"));

        cut.Markup.Should().NotContain("Decision");
        cut.FindAll("button.btn--primary").Should().BeEmpty();
    }

    [Fact]
    public void SwitchingGate_ResetsRejectReason()
    {
        // The same panel instance is reused across node selections (same render-tree position). A
        // half-typed reason on one gate must NOT carry over when the inspector switches to another gate.
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Gate("gate-A"))
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-A"));

        cut.Find("input.form-field__input").Input("reason for A");
        cut.Find("button.btn--danger").HasAttribute("disabled").Should().BeFalse();

        // Re-render with a DIFFERENT gate selected.
        cut.SetParametersAndRender(p => p
            .Add(d => d.Step, Gate("gate-B"))
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-B"));

        cut.Find("input.form-field__input").GetAttribute("value").Should().BeNullOrEmpty(
            "the reject reason resets when the inspected step changes");
        cut.Find("button.btn--danger").HasAttribute("disabled").Should().BeTrue(
            "an empty reason re-disables Reject for the new gate");
    }

    [Fact]
    public void NonGateStep_NeverRendersDecisionSection()
    {
        // A job node with the same params must ignore the decision inputs entirely.
        var cut = RenderComponent<DagNodeDetailPanel>(p => p
            .Add(d => d.Step, Job())
            .Add(d => d.Status, JobStatus.AwaitingApproval)
            .Add(d => d.PendingApprovalId, "appr-1"));

        cut.Markup.Should().NotContain("Decision");
        cut.FindAll("button.btn--danger").Should().BeEmpty();
    }
}
