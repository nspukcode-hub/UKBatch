using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Dashboard.Models;
using Xunit;

namespace UKBatch.Dashboard.Tests.Models;

/// <summary>
/// Unit tests for <see cref="DictionaryDisplay"/> — the single source of truth for formatting persisted
/// dictionary values (job outputs, run forwarded state) for read-only display. The values may have been
/// read back from a JSON column as <see cref="JsonElement"/>, so the formatter must handle that, not just
/// the original CLR types.
/// </summary>
public sealed class DictionaryDisplayTests
{
    [Fact]
    public void Format_Null_ReturnsEmpty()
        => DictionaryDisplay.Format(null).Should().BeEmpty();

    [Fact]
    public void Format_String_ReturnsVerbatim()
        => DictionaryDisplay.Format("hello").Should().Be("hello");

    [Fact]
    public void Format_Number_UsesInvariantCulture()
    {
        // A real CLR double (not from JSON) must format invariantly — never a comma decimal under tr-TR.
        var prior = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            DictionaryDisplay.Format(12.5).Should().Be("12.5");
        }
        finally
        {
            CultureInfo.CurrentCulture = prior;
        }
    }

    [Fact]
    public void Format_JsonStringElement_ReturnsInnerString()
    {
        var element = JsonDocument.Parse("\"forwarded\"").RootElement;
        DictionaryDisplay.Format(element).Should().Be("forwarded");
    }

    [Fact]
    public void Format_JsonNumberElement_ReturnsRawText()
    {
        var element = JsonDocument.Parse("8264").RootElement;
        DictionaryDisplay.Format(element).Should().Be("8264");
    }

    [Fact]
    public void Format_JsonObjectElement_ReturnsCompactJson()
    {
        var element = JsonDocument.Parse("""{"a":1,"b":"x"}""").RootElement;

        var result = DictionaryDisplay.Format(element);

        result.Should().Contain("\"a\"").And.Contain("1");
        result.Should().Contain("\"b\"").And.Contain("x");
        result.Should().NotContain("ValueKind", "the element kind must never appear in the output");
    }

    [Fact]
    public void Format_JsonArrayElement_ReturnsJson()
    {
        var element = JsonDocument.Parse("[1,2,3]").RootElement;
        DictionaryDisplay.Format(element).Should().Contain("1").And.Contain("2").And.Contain("3");
    }

    [Fact]
    public void Format_JsonNullElement_ReturnsEmpty()
    {
        var element = JsonDocument.Parse("null").RootElement;
        DictionaryDisplay.Format(element).Should().BeEmpty();
    }
}
