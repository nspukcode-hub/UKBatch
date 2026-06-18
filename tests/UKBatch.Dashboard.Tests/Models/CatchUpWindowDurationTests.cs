using FluentAssertions;
using UKBatch.Dashboard.Models.Wizard;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Pins the magnitude+unit ⇄ <see cref="TimeSpan"/> conversion the wizard uses for the schedule
/// catch-up window: empty/non-positive collapses to no window, and a stored window decomposes back into
/// the largest whole unit an operator would recognise (so an edit shows "6h", not "360m").
/// </summary>
public sealed class CatchUpWindowDurationTests
{
    [Theory]
    [InlineData(30, CatchUpWindowUnit.Minutes, 30)]      // 30 minutes
    [InlineData(6, CatchUpWindowUnit.Hours, 360)]        // 6 hours
    [InlineData(2, CatchUpWindowUnit.Days, 2880)]        // 2 days
    public void ToTimeSpan_PositiveMagnitude_ScalesByUnit(int value, CatchUpWindowUnit unit, int expectedMinutes)
    {
        CatchUpWindowDuration.ToTimeSpan(value, unit).Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(-5)]
    public void ToTimeSpan_NullOrNonPositive_ReturnsNull(int? value)
    {
        CatchUpWindowDuration.ToTimeSpan(value, CatchUpWindowUnit.Minutes).Should().BeNull(
            "empty / zero / negative means no catch-up window");
    }

    [Fact]
    public void FromTimeSpan_PrefersLargestWholeUnit()
    {
        CatchUpWindowDuration.FromTimeSpan(TimeSpan.FromDays(2)).Should().Be((2, CatchUpWindowUnit.Days));
        CatchUpWindowDuration.FromTimeSpan(TimeSpan.FromHours(6)).Should().Be((6, CatchUpWindowUnit.Hours));
        CatchUpWindowDuration.FromTimeSpan(TimeSpan.FromMinutes(90)).Should().Be((90, CatchUpWindowUnit.Minutes),
            "90 minutes is not a whole number of hours, so it stays in minutes");
    }

    [Theory]
    [InlineData(null)]
    public void FromTimeSpan_Null_ReturnsEmptyMinutes(TimeSpan? window)
    {
        CatchUpWindowDuration.FromTimeSpan(window).Should().Be(((int?)null, CatchUpWindowUnit.Minutes));
    }

    [Fact]
    public void RoundTrip_ToTimeSpanThenFromTimeSpan_PreservesValueAndUnit()
    {
        var window = CatchUpWindowDuration.ToTimeSpan(6, CatchUpWindowUnit.Hours);
        CatchUpWindowDuration.FromTimeSpan(window).Should().Be((6, CatchUpWindowUnit.Hours),
            "the wizard relies on this round-trip so an edit-load reproduces what the operator entered");
    }

    [Theory]
    [InlineData(null, "none")]
    [InlineData(30 * 60, "30m")]       // 30 minutes worth of seconds, as a TimeSpan below
    public void Describe_RendersHumanReadableOrNone(int? totalSeconds, string expected)
    {
        TimeSpan? window = totalSeconds is { } s ? TimeSpan.FromSeconds(s) : null;
        CatchUpWindowDuration.Describe(window).Should().Be(expected);
    }

    [Fact]
    public void Describe_FormatsLargestUnit()
    {
        CatchUpWindowDuration.Describe(TimeSpan.FromHours(6)).Should().Be("6h");
        CatchUpWindowDuration.Describe(TimeSpan.FromDays(2)).Should().Be("2d");
        CatchUpWindowDuration.Describe(TimeSpan.FromMinutes(45)).Should().Be("45m");
    }

    [Fact]
    public void FromTimeSpan_AbsurdlyLargeWindow_ClampsWithoutOverflow()
    {
        // A near-TimeSpan.MaxValue window (e.g. posted straight to the REST API, which only rejects
        // negatives) must not overflow the int magnitude cast and wrap to a garbage or negative value.
        var (value, _) = CatchUpWindowDuration.FromTimeSpan(TimeSpan.MaxValue);
        value.Should().NotBeNull();
        value!.Value.Should().BePositive("an oversized window clamps to a large positive magnitude, never wraps negative");
    }
}
