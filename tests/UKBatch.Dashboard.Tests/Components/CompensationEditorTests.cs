using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Dashboard.Components.Shared.Editor;
using UKBatch.Dashboard.Configuration;
using UKBatch.Dashboard.Models;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Bunit render/interaction contract for <see cref="CompensationEditor"/> and the
/// <c>StepDraftEditor.AllowCompensation</c> threading. A compensator is edited only on a top-level Job /
/// ParallelGroup step; it is never offered on a parallel-group child or an ApprovalGate.
/// </summary>
public sealed class CompensationEditorTests : TestContext
{
    private static readonly IReadOnlyList<JobCatalogEntry> Catalog = new[]
    {
        new JobCatalogEntry("CancelOrder", "billing"),
        new JobCatalogEntry("LocalUndo", null),
    };

    private static readonly IReadOnlyList<UKBatchServiceDescriptor> Svcs = new[]
    {
        new UKBatchServiceDescriptor { Name = "billing", BaseUrl = new Uri("http://billing/api/") },
    };

    private static WizardStepDraft JobDraft() => new()
    {
        StepId = "s1",
        StepType = BatchStepType.Job,
        JobName = "PlaceOrder",
    };

    // ── toggle materialises / clears the compensator draft ───────────────────────

    [Fact]
    public void Toggle_Off_RendersToggleOnly_NoCompensatorFields()
    {
        var step = JobDraft();

        var cut = RenderComponent<CompensationEditor>(p => p.Add(c => c.Step, step));

        cut.Markup.Should().Contain("Add compensator");
        cut.Find("input[type=checkbox]").HasAttribute("checked").Should().BeFalse("no compensator yet");
        cut.Markup.Should().NotContain("Target service", "the compensator fields are hidden until enabled");
    }

    [Fact]
    public void Toggle_On_MaterialisesDraft_AndFiresOnCompensationChanged()
    {
        var step = JobDraft();
        var enabledFired = 0;

        var cut = RenderComponent<CompensationEditor>(p => p
            .Add(c => c.Step, step)
            .Add(c => c.OnCompensationChanged, () => { enabledFired++; }));

        cut.Find("input[type=checkbox]").Change(true);

        step.Compensation.Should().NotBeNull("enabling the toggle materialises the compensator draft");
        enabledFired.Should().Be(1, "enabling a compensator fires OnCompensationChanged so the parent flips the policy");
        cut.Markup.Should().Contain("Target service", "the compensator fields appear once enabled");
    }

    [Fact]
    public void Toggle_Off_ClearsDraft()
    {
        var step = JobDraft();
        step.Compensation = new CompensationDraft { JobName = "Undo" };

        var cut = RenderComponent<CompensationEditor>(p => p.Add(c => c.Step, step));
        cut.Find("input[type=checkbox]").Change(false);

        step.Compensation.Should().BeNull("disabling the toggle clears the compensator draft");
    }

    // ── TargetService is FREELY editable here (cross-service compensators) ────────

    [Fact]
    public void EnabledCompensator_ExposesEditableTargetServiceDropdown()
    {
        var step = JobDraft();
        step.Compensation = new CompensationDraft { JobName = "Undo" };

        var cut = RenderComponent<CompensationEditor>(p => p
            .Add(c => c.Step, step)
            .Add(c => c.Services, Svcs));

        cut.Markup.Should().Contain("Target service", "a compensator MAY run cross-service, so the target is editable");
        var options = cut.FindAll("select.form-field__select option").Select(o => o.TextContent).ToList();
        options.Should().Contain("Local (this service)");
        options.Should().Contain("billing", "the configured services populate the target dropdown");
    }

    // ── catalog picker sets JobName + TargetService together ─────────────────────

    [Fact]
    public void CatalogPick_SetsJobNameAndTargetService_Together()
    {
        var step = JobDraft();
        step.Compensation = new CompensationDraft();

        var cut = RenderComponent<CompensationEditor>(p => p
            .Add(c => c.Step, step)
            .Add(c => c.JobCatalog, Catalog)
            .Add(c => c.Services, Svcs));

        // The first catalog <select> is the job picker; index 0 is "CancelOrder @ billing".
        cut.FindAll("select.form-field__select").First().Change("0");

        step.Compensation!.JobName.Should().Be("CancelOrder", "the picker sets the compensator job name");
        step.Compensation.TargetService.Should().Be("billing",
            "a cross-service compensator picks up the advertised service (unlike the local-only chain UI)");
    }

    // ── StepDraftEditor.AllowCompensation gating ─────────────────────────────────

    [Fact]
    public void StepDraftEditor_Job_AllowCompensationTrue_RendersToggle()
    {
        var cut = RenderComponent<StepDraftEditor>(p => p
            .Add(e => e.Step, JobDraft())
            .Add(e => e.AllowCompensation, true));

        cut.Markup.Should().Contain("Add compensator", "a top-level Job offers a compensator when allowed");
    }

    [Fact]
    public void StepDraftEditor_Job_AllowCompensationFalse_NoToggle()
    {
        var cut = RenderComponent<StepDraftEditor>(p => p
            .Add(e => e.Step, JobDraft())
            .Add(e => e.AllowCompensation, false));

        cut.Markup.Should().NotContain("Add compensator",
            "the FailurePolicy pane / chain editors pass AllowCompensation=false — no compensator editor there");
    }

    [Fact]
    public void StepDraftEditor_ApprovalGate_AllowCompensationTrue_NoToggle()
    {
        var gate = new WizardStepDraft
        {
            StepId = "g1",
            StepType = BatchStepType.ApprovalGate,
            ApprovalTitle = "Confirm",
        };

        var cut = RenderComponent<StepDraftEditor>(p => p
            .Add(e => e.Step, gate)
            .Add(e => e.AllowCompensation, true));

        cut.Markup.Should().NotContain("Add compensator", "an ApprovalGate step never offers a compensator");
    }

    [Fact]
    public void StepDraftEditor_ParallelGroup_AllowCompensationTrue_RendersOneGroupLevelToggle_NotPerChild()
    {
        var group = new WizardStepDraft { StepId = "pg", StepType = BatchStepType.ParallelGroup };
        group.Children.Add(new WizardStepDraft { StepId = "c1", StepType = BatchStepType.Job, JobName = "A" });
        group.Children.Add(new WizardStepDraft { StepId = "c2", StepType = BatchStepType.Job, JobName = "B" });

        var cut = RenderComponent<StepDraftEditor>(p => p
            .Add(e => e.Step, group)
            .Add(e => e.AllowCompensation, true));

        // Exactly ONE compensator toggle at the group level — the child editors recurse with
        // AllowCompensation=false, so a parallel child never offers its own compensator.
        System.Text.RegularExpressions.Regex.Matches(cut.Markup, "Add compensator").Count.Should().Be(1,
            "a ParallelGroup gets exactly one group-level compensator toggle, never one per child");
    }
}
