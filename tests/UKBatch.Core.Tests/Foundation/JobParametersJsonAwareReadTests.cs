using System.Text.Json;
using FluentAssertions;
using UKBatch.Abstractions.Jobs;
using Xunit;

namespace UKBatch.Core.Tests.Foundation;

/// <summary>
/// Pins the JSON-aware typed reads on <see cref="JobParameters"/>. A value produced locally is a boxed CLR
/// value read back on the fast path (unchanged behavior). A value that crossed a service boundary or was
/// rehydrated from a JSON-backed store arrives as a <see cref="JsonElement"/>; the typed readers then
/// deserialize it into the requested type. Both axes must behave identically, and a genuinely incompatible
/// value must still fail (false / throw) exactly as before — the zero-regression guarantee for the
/// pre-existing boxed-CLR readers.
/// </summary>
public class JobParametersJsonAwareReadTests
{
    /// <summary>A small POCO to prove object-shaped JsonElement deserialization (and case-insensitive matching).</summary>
    private sealed record Order
    {
        public int OrderId { get; init; }
        public string? Region { get; init; }
    }

    private static JobParameters With(string key, object? value)
        => new(new Dictionary<string, object?> { [key] = value });

    /// <summary>Parses a JSON fragment to the single <see cref="JsonElement"/> a cross-service value arrives as.</summary>
    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    // ===== boxed CLR value: the fast path, unchanged behavior =====

    [Fact]
    public void TryGet_BoxedInt_ReturnsValue()
    {
        var p = With("orderId", 5);

        p.TryGet<int>("orderId", out var value).Should().BeTrue();
        value.Should().Be(5);
    }

    [Fact]
    public void GetRequired_BoxedInt_ReturnsValue()
    {
        With("orderId", 5).GetRequired<int>("orderId").Should().Be(5);
    }

    [Fact]
    public void GetOrDefault_BoxedInt_ReturnsValue()
    {
        With("orderId", 5).GetOrDefault<int>("orderId").Should().Be(5);
    }

    [Fact]
    public void TryGet_BoxedWrongType_ReturnsFalse()
    {
        // A boxed string cannot satisfy a TryGet<int> — it is not assignable and not a JsonElement, so the
        // non-throwing reader returns false. This is the pre-existing behavior that must not regress.
        var p = With("orderId", "not-a-number");

        p.TryGet<int>("orderId", out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void GetRequired_BoxedWrongType_Throws()
    {
        // A boxed string requested as int falls through to the (T)raw cast, which throws InvalidCastException
        // — unchanged behavior for an incompatible boxed CLR value.
        var act = () => With("orderId", "not-a-number").GetRequired<int>("orderId");

        act.Should().Throw<InvalidCastException>();
    }

    // ===== JsonElement of a scalar: cross-service / resumed value =====

    [Fact]
    public void TryGet_JsonElementNumber_ReadsAsInt()
    {
        var p = With("orderId", Json("5"));

        p.TryGet<int>("orderId", out var value).Should().BeTrue("a JSON number deserializes into int");
        value.Should().Be(5);
    }

    [Fact]
    public void TryGet_JsonElementString_ReadsAsString()
    {
        var p = With("region", Json("\"EU\""));

        p.TryGet<string>("region", out var value).Should().BeTrue();
        value.Should().Be("EU");
    }

    [Fact]
    public void TryGet_JsonElementBool_ReadsAsBool()
    {
        var p = With("flag", Json("true"));

        p.TryGet<bool>("flag", out var value).Should().BeTrue();
        value.Should().BeTrue();
    }

    [Fact]
    public void GetRequired_JsonElementScalars_Succeed()
    {
        With("orderId", Json("42")).GetRequired<int>("orderId").Should().Be(42);
        With("region", Json("\"US\"")).GetRequired<string>("region").Should().Be("US");
        With("flag", Json("false")).GetRequired<bool>("flag").Should().BeFalse();
    }

    [Fact]
    public void GetOrDefault_JsonElementNumber_Succeeds()
    {
        With("orderId", Json("7")).GetOrDefault<int>("orderId").Should().Be(7);
    }

    // ===== JsonElement of an object: cross-service / resumed POCO =====

    [Fact]
    public void TryGet_JsonElementObject_DeserializesPoco()
    {
        var p = With("order", Json("""{"orderId":99,"region":"EU"}"""));

        p.TryGet<Order>("order", out var order).Should().BeTrue("a JSON object deserializes into the POCO");
        order!.OrderId.Should().Be(99);
        order.Region.Should().Be("EU");
    }

    [Fact]
    public void TryGet_JsonElementObject_PropertyMatchIsCaseInsensitive()
    {
        // The reader uses PropertyNameCaseInsensitive, so JSON written with different casing than the CLR
        // property names (e.g. a different serializer on the producing side) still binds.
        var p = With("order", Json("""{"ORDERID":1,"REGION":"AP"}"""));

        p.TryGet<Order>("order", out var order).Should().BeTrue("property matching is case-insensitive");
        order!.OrderId.Should().Be(1);
        order.Region.Should().Be("AP");
    }

    [Fact]
    public void GetRequired_JsonElementObject_DeserializesPoco()
    {
        var order = With("order", Json("""{"orderId":3,"region":"EU"}""")).GetRequired<Order>("order");

        order.OrderId.Should().Be(3);
        order.Region.Should().Be("EU");
    }

    // ===== JsonElement that cannot convert to T =====

    [Fact]
    public void TryGet_JsonElementIncompatibleWithType_ReturnsFalse()
    {
        // A JSON string cannot become an int — the deserialize throws JsonException internally, which the
        // non-throwing reader swallows and reports as a missing typed read.
        var p = With("orderId", Json("\"not-a-number\""));

        p.TryGet<int>("orderId", out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void GetRequired_JsonElementIncompatibleWithType_Throws()
    {
        // GetRequired does NOT swallow the JsonException — an incompatible required value is a hard error.
        var act = () => With("orderId", Json("\"not-a-number\"")).GetRequired<int>("orderId");

        act.Should().Throw<JsonException>();
    }

    [Fact]
    public void TryGet_JsonObjectRequestedAsScalar_ReturnsFalse()
    {
        var p = With("order", Json("""{"orderId":1}"""));

        p.TryGet<int>("order", out var value).Should().BeFalse("an object cannot be read as a bare int");
        value.Should().Be(0);
    }

    // ===== missing / null parity (unchanged) =====

    [Fact]
    public void TryGet_MissingKey_ReturnsFalse()
    {
        JobParameters.Empty.TryGet<int>("absent", out var value).Should().BeFalse();
        value.Should().Be(0);
    }

    [Fact]
    public void GetOrDefault_MissingKey_ReturnsSuppliedDefault()
    {
        JobParameters.Empty.GetOrDefault("absent", -1).Should().Be(-1);
    }

    [Fact]
    public void GetRequired_MissingKey_ThrowsKeyNotFound()
    {
        var act = () => JobParameters.Empty.GetRequired<int>("absent");

        act.Should().Throw<KeyNotFoundException>();
    }
}
