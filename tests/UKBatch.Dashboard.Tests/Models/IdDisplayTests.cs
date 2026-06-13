using FluentAssertions;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Unit tests for <see cref="IdDisplay"/> — the shared id abbreviation rule. UUIDv7 ids
/// embed a millisecond timestamp in their FIRST 12 hex characters, so two runs created
/// within the same ~65-second window share their first 8 characters and a head prefix
/// cannot tell them apart. The abbreviation must therefore keep the random TAIL.
/// </summary>
public sealed class IdDisplayTests
{
    [Fact]
    public void Shorten_LongHexId_ReturnsEllipsisPlusLastEightChars()
    {
        IdDisplay.Shorten("0190163d86947ccea9e5fde16bf9ccba").Should().Be("…6bf9ccba");
    }

    [Fact]
    public void Shorten_NeighbouringUuidV7Ids_RemainDistinguishable()
    {
        // Same leading timestamp block (the failure mode of head truncation) — only the
        // random tail differs, and the abbreviation must surface that difference.
        var first = IdDisplay.Shorten("0190163d86947ccea9e5fde16bf9ccba");
        var second = IdDisplay.Shorten("0190163d86947b11a3c2e07d4a1291c4");

        first.Should().NotBe(second);
        first.Should().Be("…6bf9ccba");
        second.Should().Be("…4a1291c4");
    }

    [Theory]
    [InlineData("")]
    [InlineData("a1b2")]
    [InlineData("a1b2c3d4")] // exactly the tail length — abbreviating would not shorten it
    public void Shorten_ShortId_ReturnsVerbatim(string id)
    {
        IdDisplay.Shorten(id).Should().Be(id);
    }

    [Fact]
    public void Shorten_NineCharId_StillAbbreviates()
    {
        // One past the threshold: same display width as verbatim, but the rule stays simple
        // (single length cutoff) rather than special-casing widths.
        IdDisplay.Shorten("a1b2c3d4e").Should().Be("…1b2c3d4e");
    }
}
