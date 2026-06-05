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
}
