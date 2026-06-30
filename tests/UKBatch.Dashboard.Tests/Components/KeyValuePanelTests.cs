using System.Text.Json;
using Bunit;
using FluentAssertions;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Unit tests for <see cref="KeyValuePanel"/> — the read-only key/value card used to surface job outputs
/// and run forwarded state. The panel renders nothing when its source is null or empty (so an execution or
/// run that recorded none shows no extra card) and formats values that may have been read back from a JSON
/// column as <see cref="JsonElement"/> rather than their original CLR type.
/// </summary>
public sealed class KeyValuePanelTests : TestContext
{
    [Fact]
    public void Renders_Card_WhenItemsPresent()
    {
        var items = new Dictionary<string, object?> { ["orderId"] = "8264", ["count"] = 3 };

        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.Title, "Outputs"));

        cut.Find(".surface-card__title").TextContent.Should().Be("Outputs");
        cut.Markup.Should().Contain("orderId").And.Contain("8264");
        cut.Markup.Should().Contain("count").And.Contain("3");
    }

    [Fact]
    public void RendersNothing_WhenItemsNull()
    {
        // The single most important invariant: a page embedding the panel is unchanged when there is no data.
        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, (IReadOnlyDictionary<string, object?>?)null)
            .Add(c => c.Title, "Outputs"));

        cut.Markup.Trim().Should().BeEmpty("a null source renders no card at all");
    }

    [Fact]
    public void RendersNothing_WhenItemsEmpty()
    {
        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, new Dictionary<string, object?>())
            .Add(c => c.Title, "Outputs"));

        cut.Markup.Trim().Should().BeEmpty("an empty source renders no card at all");
    }

    [Fact]
    public void Formats_JsonElement_Values_FromJsonColumn()
    {
        // Values round-tripped through a JSON column deserialize as JsonElement, not the original CLR type.
        // A scalar element must render as its text; a structured element as JSON — not the element kind.
        var doc = JsonDocument.Parse("""{ "scalar": 42, "nested": { "a": 1 } }""");
        var items = new Dictionary<string, object?>
        {
            ["scalar"] = doc.RootElement.GetProperty("scalar"),
            ["nested"] = doc.RootElement.GetProperty("nested"),
        };

        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.Title, "Forwarded outputs"));

        cut.Markup.Should().Contain("42", "a scalar JsonElement renders as its value");
        cut.Markup.Should().Contain("\"a\"").And.Contain("1", "a structured JsonElement renders as JSON, not its kind");
        cut.Markup.Should().NotContain("ValueKind", "the element kind must never leak into the markup");
    }

    [Fact]
    public void Renders_CustomHeaders()
    {
        var items = new Dictionary<string, object?> { ["k"] = "v" };

        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.Title, "Outputs")
            .Add(c => c.KeyHeader, "Output")
            .Add(c => c.ValueHeader, "Result"));

        var headers = cut.FindAll(".data-table__header");
        headers.Should().HaveCount(2);
        headers[0].TextContent.Should().Be("Output");
        headers[1].TextContent.Should().Be("Result");
    }

    [Fact]
    public void Renders_MutedDash_ForNullValue()
    {
        var items = new Dictionary<string, object?> { ["empty"] = null };

        var cut = RenderComponent<KeyValuePanel>(p => p
            .Add(c => c.Items, items)
            .Add(c => c.Title, "Outputs"));

        cut.Find(".data-table__cell--muted").TextContent.Should().Be("—");
    }
}
