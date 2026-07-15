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
/// Bunit render/interaction contract for <see cref="DecisionEditor"/>: a branch card per branch (condition
/// fields for a conditional branch, hidden for the else), add/remove/reorder, and the else-toggle rules
/// (at most one else, pinned last) enforced in the UI so the operator can't build an unreachable branch.
/// </summary>
public sealed class DecisionEditorTests : TestContext
{
    private static readonly IReadOnlyList<JobCatalogEntry> Catalog = new[]
    {
        new JobCatalogEntry("Express", "shipping"),
        new JobCatalogEntry("Standard", null),
    };

    private static readonly IReadOnlyList<UKBatchServiceDescriptor> Svcs = new[]
    {
        new UKBatchServiceDescriptor { Name = "shipping", BaseUrl = new Uri("http://shipping/api/") },
    };

    private static WizardStepDraft DecisionDraft(params DecisionBranchDraft[] branches) => new()
    {
        StepId = "dec",
        StepType = BatchStepType.Decision,
        DecisionBranches = branches.ToList(),
    };

    private static DecisionBranchDraft Cond(string id, string jobName = "Express") => new()
    {
        StepId = id,
        JobName = jobName,
        When = new ConditionDraft { ParameterKey = "amount", Operator = ConditionOperator.GreaterThan, Value = "1000" },
    };

    private static DecisionBranchDraft Else(string id, string jobName = "Standard") => new()
    {
        StepId = id,
        JobName = jobName,
        When = null,
    };

    private IRenderedComponent<DecisionEditor> Render(WizardStepDraft step) =>
        RenderComponent<DecisionEditor>(p => p
            .Add(c => c.Step, step)
            .Add(c => c.JobCatalog, Catalog)
            .Add(c => c.Services, Svcs));

    [Fact]
    public void RendersBranchRows_ConditionFieldsOnlyForConditionalBranches()
    {
        var cut = Render(DecisionDraft(Cond("b1"), Else("b2")));

        cut.FindAll(".decision-editor__branch").Should().HaveCount(2, "one card per branch");
        cut.Markup.Should().Contain("Else (default)", "the null-condition branch is labelled the else/default");
        // The conditional branch shows the Parameter key field; exactly one branch (the conditional) has it.
        cut.FindAll("input")
            .Count(i => (i.GetAttribute("placeholder") ?? string.Empty).Contains("amount", StringComparison.Ordinal))
            .Should().Be(1, "only the conditional branch renders the condition fields");
    }

    [Fact]
    public async Task AddBranch_InsertsBeforeTrailingElse()
    {
        var step = DecisionDraft(Cond("b1"), Else("b2"));
        var cut = Render(step);

        await cut.Find("button.btn--secondary").ClickAsync(new());

        step.DecisionBranches.Should().HaveCount(3);
        step.DecisionBranches[^1].When.Should().BeNull("the else branch stays pinned last after adding a branch");
        step.DecisionBranches[1].When.Should().NotBeNull("the new conditional branch is inserted before the else");
    }

    [Fact]
    public async Task RemoveBranch_DropsIt()
    {
        var step = DecisionDraft(Cond("b1"), Else("b2"));
        var cut = Render(step);

        await cut.Find("button[aria-label='Remove branch']").ClickAsync(new());

        step.DecisionBranches.Should().HaveCount(1);
    }

    [Fact]
    public void ElseCheckbox_DisabledOnNonLastBranch()
    {
        // [conditional, else]: the first branch's else checkbox is disabled — you cannot make a middle
        // branch the else (it would strand the branches after it), enforcing "else must be last" up front.
        var cut = Render(DecisionDraft(Cond("b1"), Else("b2")));

        var elseCheckboxes = cut.FindAll(".decision-editor__branch input[type=checkbox]");
        elseCheckboxes.Should().HaveCount(2);
        elseCheckboxes[0].HasAttribute("disabled").Should().BeTrue("a non-last branch cannot be toggled to else");
        elseCheckboxes[1].HasAttribute("disabled").Should().BeFalse("the trailing else branch keeps a usable toggle");
    }

    [Fact]
    public async Task ToggleElse_OnSingleBranch_NullsCondition()
    {
        var step = DecisionDraft(Cond("b1"));
        var cut = Render(step);

        // The single branch is last → its else checkbox is enabled; checking it nulls the condition.
        var checkbox = cut.Find(".decision-editor__branch input[type=checkbox]");
        checkbox.HasAttribute("disabled").Should().BeFalse();
        await checkbox.ChangeAsync(new Microsoft.AspNetCore.Components.ChangeEventArgs { Value = true });

        step.DecisionBranches[0].When.Should().BeNull("toggling else nulls the branch condition (it becomes the default)");
    }

    [Fact]
    public void EmptyBranches_ShowsAtLeastOneBranchHint()
    {
        var cut = Render(DecisionDraft());

        cut.Markup.Should().Contain("A decision needs at least one branch");
        cut.FindAll(".decision-editor__branch").Should().BeEmpty();
    }
}
