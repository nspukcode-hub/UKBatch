using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using UKBatch.Abstractions.Workers;
using Xunit;

namespace UKBatch.Api.Tests.Workers;

/// <summary>
/// — JsonStringEnumConverter consistency lock (the "3-places" lesson
/// applied to <see cref="WorkerStatus"/>). The worker heartbeat client AND the server's
/// <c>ConfigureHttpJsonOptions</c> both add a <see cref="JsonStringEnumConverter"/>, so
/// <see cref="WorkerBeatRequest.Status"/> / <see cref="WorkerInfo.Status"/> MUST cross the wire as a
/// string (<c>"Online"</c>), never the underlying integer. A mismatch between the two ends would
/// silently break beat deserialization (worker string ↔ server integer parse).
/// </summary>
public sealed class WorkerBeatJsonContractTests
{
    // Mirrors the system's serializer config: Web defaults + string enums (both ends).
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    [Fact]
    public void Serialize_WorkerBeatRequest_StatusIsStringNotInteger()
    {
        var beat = new WorkerBeatRequest
        {
            Name = "invoicing",
            Jobs = ["GenerateInvoice"],
            Tags = ["billing"],
            Status = WorkerStatus.Online,
        };

        var json = JsonSerializer.Serialize(beat, Options);

        json.Should().Contain("\"status\":\"Online\"", "WorkerStatus serializes as the string 'Online'");
        json.Should().NotContain("\"status\":0", "the integer form would break the string-enum contract");
    }

    [Fact]
    public void RoundTrip_WorkerBeatRequest_PreservesAllFields()
    {
        var original = new WorkerBeatRequest
        {
            Name = "shipping",
            Jobs = ["ShipOrder", "Track"],
            Tags = ["eu-west", "gpu"],
            Status = WorkerStatus.Offline,
            InFlight = 0,
            Capacity = 0,
        };

        var json = JsonSerializer.Serialize(original, Options);
        var restored = JsonSerializer.Deserialize<WorkerBeatRequest>(json, Options);

        restored.Should().BeEquivalentTo(original, "a full round-trip preserves every wire field");
    }

    [Fact]
    public void Deserialize_StringStatus_ParsesEnum()
    {
        // The shape a worker actually puts on the wire (string status).
        const string wire = """{ "name": "invoicing", "jobs": [], "tags": [], "status": "Offline", "inFlight": 0, "capacity": 0 }""";

        var beat = JsonSerializer.Deserialize<WorkerBeatRequest>(wire, Options);

        beat.Should().NotBeNull();
        beat!.Status.Should().Be(WorkerStatus.Offline, "the server parses the worker's string status");
    }

    [Fact]
    public void Serialize_WorkerInfo_StatusAndOnlineAreWireFriendly()
    {
        var info = new WorkerInfo
        {
            Name = "invoicing",
            Status = WorkerStatus.Online,
            LastSeenUtc = DateTimeOffset.UtcNow,
            Online = true,
        };

        var json = JsonSerializer.Serialize(info, Options);

        json.Should().Contain("\"status\":\"Online\"", "WorkerInfo.Status is also a string on the wire");
        json.Should().Contain("\"online\":true", "the live Online flag is a JSON boolean");
    }
}
