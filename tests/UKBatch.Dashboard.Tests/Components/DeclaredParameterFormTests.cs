using System.Text.Json;
using Bunit;
using FluentAssertions;
using UKBatch.Abstractions.Jobs;
using UKBatch.Dashboard.Components.Shared;
using Xunit;

namespace UKBatch.Dashboard.Tests.Components;

/// <summary>
/// Unit tests for <see cref="DeclaredParameterForm"/> — the typed trigger form built from a job's declared
/// parameters. Covers per-kind rendering and parsing, required client validation, default pre-fill from
/// both a boxed CLR value and a <see cref="JsonElement"/> (incl. the datetime-local special case), and the
/// permissive escape-hatch JSON merge.
/// </summary>
public sealed class DeclaredParameterFormTests : TestContext
{
    private static JobParameterDescriptor Param(string name, ParameterValueKind kind, bool required = false, object? defaultValue = null)
        => new() { Name = name, Kind = kind, Required = required, DefaultValue = defaultValue };

    private (IRenderedComponent<DeclaredParameterForm> Cut, Func<Dictionary<string, object?>?> Values, Func<bool?> Valid) Render(
        params JobParameterDescriptor[] descriptors)
    {
        Dictionary<string, object?>? values = null;
        bool? valid = null;
        var cut = RenderComponent<DeclaredParameterForm>(p => p
            .Add(c => c.Parameters, descriptors)
            .Add(c => c.ValuesChanged, (Dictionary<string, object?> v) => values = v)
            .Add(c => c.IsValidChanged, (bool v) => valid = v));
        return (cut, () => values, () => valid);
    }

    [Fact]
    public void Renders_OneInputPerDeclaredParameter()
    {
        var (cut, _, _) = Render(
            Param("s", ParameterValueKind.String),
            Param("i", ParameterValueKind.Integer),
            Param("n", ParameterValueKind.Number),
            Param("b", ParameterValueKind.Boolean),
            Param("d", ParameterValueKind.DateTime),
            Param("o", ParameterValueKind.Object));

        cut.Find("#declared-param-s").GetAttribute("type").Should().Be("text");
        cut.Find("#declared-param-i").GetAttribute("type").Should().Be("number");
        cut.Find("#declared-param-b").GetAttribute("type").Should().Be("checkbox");
        cut.Find("#declared-param-d").GetAttribute("type").Should().Be("datetime-local");
        cut.Find("textarea#declared-param-o").Should().NotBeNull();
    }

    [Fact]
    public void RequiredField_Empty_IsInvalid_FilledIsValid()
    {
        var (cut, values, valid) = Render(Param("orderId", ParameterValueKind.String, required: true));

        valid().Should().BeFalse("a required field starts empty");

        cut.Find("#declared-param-orderId").Input("A-1");

        valid().Should().BeTrue();
        values().Should().ContainKey("orderId").WhoseValue.Should().Be("A-1");
    }

    [Fact]
    public void OptionalField_Empty_IsValid_AndOmittedFromValues()
    {
        var (_, values, valid) = Render(Param("note", ParameterValueKind.String));

        valid().Should().BeTrue();
        values().Should().NotContainKey("note", "a blank optional field is omitted so it never shadows a default");
    }

    [Fact]
    public void Integer_Parses_ToLong_AndRejectsNonNumeric()
    {
        var (cut, values, valid) = Render(Param("n", ParameterValueKind.Integer, required: true));

        cut.Find("#declared-param-n").Input("42");
        values()!["n"].Should().Be(42L);
        valid().Should().BeTrue();

        cut.Find("#declared-param-n").Input("abc");
        valid().Should().BeFalse("a non-numeric integer input is invalid");
        cut.Markup.Should().Contain("whole number");
    }

    [Fact]
    public void Number_Parses_WithInvariantCulture()
    {
        var (cut, values, _) = Render(Param("amount", ParameterValueKind.Number, required: true));

        cut.Find("#declared-param-amount").Input("12.5");
        values()!["amount"].Should().Be(12.5d);
    }

    [Fact]
    public void DateTime_Parses_ToDateTimeOffset()
    {
        var (cut, values, valid) = Render(Param("when", ParameterValueKind.DateTime, required: true));

        cut.Find("#declared-param-when").Input("2026-07-13T08:30");
        valid().Should().BeTrue();
        values()!["when"].Should().BeOfType<DateTimeOffset>();
    }

    [Fact]
    public void Object_Parses_ValidJson_AndRejectsInvalid()
    {
        var (cut, values, valid) = Render(Param("payload", ParameterValueKind.Object, required: true));

        cut.Find("#declared-param-payload").Input("""{ "a": 1 }""");
        valid().Should().BeTrue();
        values()!["payload"].Should().BeOfType<JsonElement>();

        cut.Find("#declared-param-payload").Input("{ not json");
        valid().Should().BeFalse();
    }

    [Fact]
    public void Boolean_Checkbox_ProducesBoolValue()
    {
        var (cut, values, _) = Render(Param("dryRun", ParameterValueKind.Boolean));

        cut.Find("#declared-param-dryRun").Change(true);
        values()!["dryRun"].Should().Be(true);

        cut.Find("#declared-param-dryRun").Change(false);
        values()!["dryRun"].Should().Be(false);
    }

    [Fact]
    public void Prefill_FromBoxedDefault_RendersValue()
    {
        var (cut, _, _) = Render(
            Param("s", ParameterValueKind.String, defaultValue: "hello"),
            Param("i", ParameterValueKind.Integer, defaultValue: 3));

        cut.Find("#declared-param-s").GetAttribute("value").Should().Be("hello");
        cut.Find("#declared-param-i").GetAttribute("value").Should().Be("3");
    }

    [Fact]
    public void Prefill_DateTime_FromBoxedAndJsonElement_UsesInputFormat()
    {
        var boxed = new DateTimeOffset(2026, 7, 13, 8, 30, 0, TimeSpan.Zero);
        var jsonDefault = JsonDocument.Parse("\"2026-07-13T08:30:00+00:00\"").RootElement;

        var (cut, _, _) = Render(
            Param("boxed", ParameterValueKind.DateTime, defaultValue: boxed),
            Param("json", ParameterValueKind.DateTime, defaultValue: jsonDefault));

        // datetime-local needs yyyy-MM-ddTHH:mm — not the general formatter's output.
        cut.Find("#declared-param-boxed").GetAttribute("value").Should().Be("2026-07-13T08:30");
        cut.Find("#declared-param-json").GetAttribute("value").Should().Be("2026-07-13T08:30");
    }

    [Fact]
    public void Prefill_Boolean_FromJsonElement()
    {
        var jsonTrue = JsonDocument.Parse("true").RootElement;
        var (cut, values, _) = Render(Param("flag", ParameterValueKind.Boolean, defaultValue: jsonTrue));

        cut.Find("#declared-param-flag").HasAttribute("checked").Should().BeTrue();
        values()!["flag"].Should().Be(true);
    }

    [Fact]
    public void EscapeHatch_MergesUndeclaredKeys_TypedWinsOnCollision()
    {
        var (cut, values, valid) = Render(Param("declared", ParameterValueKind.String));

        cut.Find("#declared-param-declared").Input("typed");
        cut.Find("#declared-param-extra").Input("""{ "extra": 1, "declared": "fromJson" }""");

        valid().Should().BeTrue();
        values().Should().ContainKey("extra");
        values()!["declared"].Should().Be("typed", "a typed field wins over the escape-hatch value on key collision");
    }

    [Fact]
    public void EscapeHatch_InvalidJson_IsInvalid()
    {
        var (cut, _, valid) = Render(Param("declared", ParameterValueKind.String));

        cut.Find("#declared-param-extra").Input("{ not json");

        valid().Should().BeFalse();
    }
}
