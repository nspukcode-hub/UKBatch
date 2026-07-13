using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Unit tests for the run-if condition evaluator: every operator against boxed-CLR and JsonElement values
/// (the local vs cross-service/resumed shapes), plus the invariant-culture and missing-key edge cases.
/// </summary>
public sealed class StepConditionEvaluatorTests
{
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    private static bool Eval(string key, ConditionOperator op, string? value, params (string Key, object? Value)[] parameters)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in parameters)
        {
            dict[k] = v;
        }
        var condition = new StepCondition { ParameterKey = key, Operator = op, Value = value };
        return StepConditionEvaluator.Evaluate(condition, new JobParameters(dict));
    }

    // ---- Equals / NotEquals ----

    [Fact]
    public void Equals_NumericValue_BoxedInt_MatchesStringComparand()
    {
        Eval("amount", ConditionOperator.Equals, "500", ("amount", 500)).Should().BeTrue();
        Eval("amount", ConditionOperator.Equals, "500.0", ("amount", 500)).Should().BeTrue();
        Eval("amount", ConditionOperator.Equals, "501", ("amount", 500)).Should().BeFalse();
    }

    [Fact]
    public void Equals_NumericValue_JsonElement_MatchesStringComparand()
    {
        Eval("amount", ConditionOperator.Equals, "500", ("amount", Json(500))).Should().BeTrue();
        Eval("amount", ConditionOperator.Equals, "12.5", ("amount", Json(12.5))).Should().BeTrue();
    }

    [Fact]
    public void Equals_StringValue_OrdinalMatch()
    {
        Eval("tier", ConditionOperator.Equals, "premium", ("tier", "premium")).Should().BeTrue();
        Eval("tier", ConditionOperator.Equals, "Premium", ("tier", "premium")).Should().BeFalse(); // ordinal, case-sensitive
        Eval("tier", ConditionOperator.Equals, "premium", ("tier", Json("premium"))).Should().BeTrue();
    }

    [Fact]
    public void Equals_BoolValue_MatchesBoolComparand()
    {
        Eval("ok", ConditionOperator.Equals, "true", ("ok", true)).Should().BeTrue();
        Eval("ok", ConditionOperator.Equals, "false", ("ok", true)).Should().BeFalse();
        Eval("ok", ConditionOperator.Equals, "true", ("ok", Json(true))).Should().BeTrue();
    }

    [Fact]
    public void NotEquals_IsInverseOfEquals()
    {
        Eval("tier", ConditionOperator.NotEquals, "basic", ("tier", "premium")).Should().BeTrue();
        Eval("tier", ConditionOperator.NotEquals, "premium", ("tier", "premium")).Should().BeFalse();
    }

    // ---- Ordering operators ----

    [Theory]
    [InlineData(ConditionOperator.GreaterThan, 1000, "1000", false)]
    [InlineData(ConditionOperator.GreaterThan, 1001, "1000", true)]
    [InlineData(ConditionOperator.GreaterThanOrEqual, 1000, "1000", true)]
    [InlineData(ConditionOperator.LessThan, 999, "1000", true)]
    [InlineData(ConditionOperator.LessThan, 1000, "1000", false)]
    [InlineData(ConditionOperator.LessThanOrEqual, 1000, "1000", true)]
    public void Ordering_BoxedInt(ConditionOperator op, int value, string comparand, bool expected)
    {
        Eval("amount", op, comparand, ("amount", value)).Should().Be(expected);
    }

    [Fact]
    public void Ordering_JsonElementNumber()
    {
        Eval("amount", ConditionOperator.GreaterThan, "1000", ("amount", Json(1500))).Should().BeTrue();
        Eval("amount", ConditionOperator.LessThan, "1000", ("amount", Json(500.5))).Should().BeTrue();
    }

    [Fact]
    public void Ordering_NonNumericValue_IsNotMet()
    {
        // A non-numeric left side cannot be ordered — the condition is "not met" (step skipped), not an error.
        Eval("tier", ConditionOperator.GreaterThan, "1000", ("tier", "premium")).Should().BeFalse();
        Eval("amount", ConditionOperator.GreaterThan, "notanumber", ("amount", 5000)).Should().BeFalse();
    }

    // ---- Presence operators ----

    [Fact]
    public void Exists_And_NotExists()
    {
        Eval("invoiceId", ConditionOperator.Exists, null, ("invoiceId", "INV-1")).Should().BeTrue();
        Eval("invoiceId", ConditionOperator.Exists, null).Should().BeFalse();
        Eval("invoiceId", ConditionOperator.NotExists, null).Should().BeTrue();
        Eval("invoiceId", ConditionOperator.NotExists, null, ("invoiceId", "INV-1")).Should().BeFalse();
    }

    [Fact]
    public void Exists_TreatsPresentKey_AsExisting_EvenWhenValueIsNull()
    {
        // A key present with a null value still "exists" for Exists, but a JSON-null cannot satisfy a
        // value-inspecting operator.
        Eval("k", ConditionOperator.Exists, null, ("k", null)).Should().BeTrue();
        Eval("k", ConditionOperator.NotExists, null, ("k", null)).Should().BeFalse();
    }

    // ---- Boolean operators ----

    [Fact]
    public void IsTrue_And_IsFalse()
    {
        Eval("approved", ConditionOperator.IsTrue, null, ("approved", true)).Should().BeTrue();
        Eval("approved", ConditionOperator.IsTrue, null, ("approved", false)).Should().BeFalse();
        Eval("approved", ConditionOperator.IsFalse, null, ("approved", false)).Should().BeTrue();
        Eval("approved", ConditionOperator.IsFalse, null, ("approved", true)).Should().BeFalse();
        Eval("approved", ConditionOperator.IsTrue, null, ("approved", Json(true))).Should().BeTrue();
        Eval("approved", ConditionOperator.IsTrue, null, ("approved", "true")).Should().BeTrue();
    }

    [Fact]
    public void IsTrue_MissingKey_IsNotMet()
    {
        Eval("approved", ConditionOperator.IsTrue, null).Should().BeFalse();
        Eval("approved", ConditionOperator.IsFalse, null).Should().BeFalse();
    }

    // ---- Contains ----

    [Fact]
    public void Contains_Substring_Ordinal()
    {
        Eval("status", ConditionOperator.Contains, "error", ("status", "fatal-error-503")).Should().BeTrue();
        Eval("status", ConditionOperator.Contains, "error", ("status", "ok")).Should().BeFalse();
        Eval("status", ConditionOperator.Contains, "error", ("status", Json("has error inside"))).Should().BeTrue();
    }

    // ---- Missing key / JSON null ----

    [Fact]
    public void ComparisonOnMissingKey_IsNotMet()
    {
        Eval("nope", ConditionOperator.Equals, "x").Should().BeFalse();
        Eval("nope", ConditionOperator.GreaterThan, "1").Should().BeFalse();
        Eval("nope", ConditionOperator.Contains, "x").Should().BeFalse();
    }

    [Fact]
    public void JsonNullValue_IsNotMet_ForValueOperators()
    {
        var nullElement = JsonDocument.Parse("null").RootElement;
        Eval("k", ConditionOperator.Equals, "x", ("k", nullElement)).Should().BeFalse();
        Eval("k", ConditionOperator.IsTrue, null, ("k", nullElement)).Should().BeFalse();
        // But the key is present, so Exists still holds.
        Eval("k", ConditionOperator.Exists, null, ("k", nullElement)).Should().BeTrue();
    }

    // ---- InvariantCulture (tr-TR decimal-comma trap) ----

    [Fact]
    public void NumericParse_IsCultureInvariant()
    {
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); // decimal separator is ','
            // Comparand "12.5" must parse with '.' as the decimal point regardless of thread culture.
            Eval("x", ConditionOperator.GreaterThan, "12.5", ("x", 13.0)).Should().BeTrue();
            Eval("x", ConditionOperator.Equals, "12.5", ("x", 12.5)).Should().BeTrue();
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    // ---- Unknown / future operator ----

    [Fact]
    public void UnknownOperator_IsNotMet()
    {
        // A value outside the defined enum (a future operator read by an older runtime) must skip the step,
        // never run it on an unverifiable condition.
        Eval("x", (ConditionOperator)999, "1", ("x", 1)).Should().BeFalse();
    }
}
