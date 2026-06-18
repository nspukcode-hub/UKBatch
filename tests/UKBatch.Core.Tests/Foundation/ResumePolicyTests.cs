using FluentAssertions;
using UKBatch.Abstractions.Models;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Verifies how each <see cref="ResumePolicy"/> mode resolves the executor's start step index from a
/// recorded cursor, plus the negative-index guard on <see cref="ResumePolicy.RestartFrom"/>.
/// </summary>
public class ResumePolicyTests
{
    [Fact]
    public void ResumeForward_WithCursor_StartsAtCursor()
    {
        ResumePolicy.ResumeForward.ResolveStartIndex(2).Should().Be(2);
    }

    [Fact]
    public void ResumeForward_NullCursor_StartsAtZero()
    {
        // A run with no recorded cursor (created before resume, or never advanced) replays from the start.
        ResumePolicy.ResumeForward.ResolveStartIndex(null).Should().Be(0);
    }

    [Fact]
    public void RestartAll_IgnoresCursor_StartsAtZero()
    {
        ResumePolicy.RestartAll.ResolveStartIndex(2).Should().Be(0);
        ResumePolicy.RestartAll.ResolveStartIndex(null).Should().Be(0);
    }

    [Fact]
    public void RestartFrom_StartsAtGivenIndex_IgnoringCursor()
    {
        ResumePolicy.RestartFrom(2).ResolveStartIndex(5).Should().Be(2);
        ResumePolicy.RestartFrom(2).ResolveStartIndex(null).Should().Be(2);
    }

    [Fact]
    public void RestartFrom_Zero_IsAllowed()
    {
        ResumePolicy.RestartFrom(0).ResolveStartIndex(3).Should().Be(0);
    }

    [Fact]
    public void RestartFrom_NegativeIndex_Throws()
    {
        var act = () => ResumePolicy.RestartFrom(-1);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ResumePolicy_IsValueEquatable()
    {
        // A readonly record struct: same mode + index compare equal (used by table-style assertions).
        ResumePolicy.RestartFrom(3).Should().Be(ResumePolicy.RestartFrom(3));
        ResumePolicy.ResumeForward.Should().Be(ResumePolicy.ResumeForward);
        ResumePolicy.ResumeForward.Should().NotBe(ResumePolicy.RestartAll);
    }
}
