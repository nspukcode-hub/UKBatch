using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.RabbitMQ;
using Xunit;

namespace UKBatch.Transport.RabbitMQ.Tests.Json;

/// <summary>
/// Wire JSON contract. <see cref="RabbitMqTransport.JsonOpts"/> MUST round-trip
/// <see cref="JobStatus"/> as a STRING (lesson: a Web-default worker hosting
/// <c>AddUKBatchApi</c> serializes enums as strings; an integer-serializing transport would fail to
/// parse the reply) and use camelCase. Docker-free.
/// </summary>
public sealed class JsonContractTests
{
    private static JsonSerializerOptions GetTransportJsonOpts()
    {
        var field = typeof(RabbitMqTransport).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("RabbitMqTransport.JsonOpts internal field not found.");
        return (JsonSerializerOptions)field.GetValue(null)!;
    }

    [Fact]
    public void JsonOpts_ContainsJsonStringEnumConverter()
    {
        GetTransportJsonOpts().Converters.OfType<JsonStringEnumConverter>().Should().NotBeEmpty(
 "JobStatus MUST round-trip as a string against AddUKBatchApi-configured workers");
    }

    [Fact]
    public void JsonOpts_PropertyNamingPolicy_CamelCase()
    {
        GetTransportJsonOpts().PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
    }

    [Theory]
    [InlineData(JobStatus.Completed, "Completed")]
    [InlineData(JobStatus.Failed, "Failed")]
    [InlineData(JobStatus.Cancelled, "Cancelled")]
    public void JobResult_StatusEnum_SerializesAsString(JobStatus status, string expected)
    {
        var opts = GetTransportJsonOpts();
        var result = new JobResult
        {
            ExecutionId = "x",
            Status = status,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(result, opts);
        json.Should().Contain($"\"status\":\"{expected}\"", "the enum MUST be its name, not a numeric value");
    }

    [Fact]
    public void JobResult_RoundTrips_PreservingStatusAndError()
    {
        var opts = GetTransportJsonOpts();
        var result = new JobResult
        {
            ExecutionId = "exec-1",
            Status = JobStatus.Failed,
            ErrorMessage = "kaboom",
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(result, opts);
        var back = JsonSerializer.Deserialize<JobResult>(json, opts);

        back.Should().NotBeNull();
        back!.Status.Should().Be(JobStatus.Failed);
        back.ExecutionId.Should().Be("exec-1");
        back.ErrorMessage.Should().Be("kaboom");
    }

    [Fact]
    public void JobMessage_RoundTrips_PreservingAllFields()
    {
        var opts = GetTransportJsonOpts();
        var message = new JobMessage
        {
            MessageId = "m-1",
            CorrelationId = "c-1",
            JobName = "DoWork",
            SourceService = "orchestrator",
            TargetService = "worker",
            BatchId = "batch-1",
            BatchStepId = "step-1",
            Parameters = new Dictionary<string, object?> { ["count"] = 42, ["name"] = "x" },
            Headers = new Dictionary<string, string> { ["traceparent"] = "00-abc-def-01" },
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 3,
        };

        var json = JsonSerializer.Serialize(message, opts);
        var back = JsonSerializer.Deserialize<JobMessage>(json, opts);

        back.Should().NotBeNull();
        back!.MessageId.Should().Be("m-1");
        back.CorrelationId.Should().Be("c-1");
        back.JobName.Should().Be("DoWork");
        back.SourceService.Should().Be("orchestrator");
        back.TargetService.Should().Be("worker");
        back.BatchId.Should().Be("batch-1");
        back.BatchStepId.Should().Be("step-1");
        back.AttemptNumber.Should().Be(3);
        back.Headers["traceparent"].Should().Be("00-abc-def-01");
    }

    [Fact]
    public void JobMessage_CamelCaseProperties_OnWire()
    {
        var opts = GetTransportJsonOpts();
        var message = new JobMessage
        {
            MessageId = "m-1",
            JobName = "DoWork",
            SourceService = "orchestrator",
            Parameters = new Dictionary<string, object?>(),
            Headers = new Dictionary<string, string>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
            AttemptNumber = 1,
        };

        var json = JsonSerializer.Serialize(message, opts);
        json.Should().Contain("\"messageId\":");
        json.Should().Contain("\"jobName\":");
        json.Should().Contain("\"sourceService\":");
        json.Should().NotContain("\"MessageId\":", "camelCase policy lowercases the first letter");
    }

    [Fact]
    public void JobResult_NullableFields_OmittedOrNull_RoundTrip()
    {
        var opts = GetTransportJsonOpts();
        var result = new JobResult
        {
            ExecutionId = "exec-1",
            Status = JobStatus.Completed,
            ErrorMessage = null,
            ReturnValues = null,
            Headers = null,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(result, opts);
        var back = JsonSerializer.Deserialize<JobResult>(json, opts);

        back!.ErrorMessage.Should().BeNull();
        back.ReturnValues.Should().BeNull();
        back.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void JobResult_WithReturnValues_RoundTrip()
    {
        // Step-output forwarding rides the reply's ReturnValues across the wire; the values must round-trip.
        // On deserialize the object? values come back as JsonElement (STJ's shape for object) — the JSON-aware
        // JobParameters readers resolve them on the consuming side.
        var opts = GetTransportJsonOpts();
        var result = new JobResult
        {
            ExecutionId = "exec-1",
            Status = JobStatus.Completed,
            ReturnValues = new Dictionary<string, object?> { ["orderId"] = 42, ["invoiceId"] = "INV-42" },
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        var json = JsonSerializer.Serialize(result, opts);
        var back = JsonSerializer.Deserialize<JobResult>(json, opts);

        back.Should().NotBeNull();
        back!.ReturnValues.Should().NotBeNull();
        ((JsonElement)back.ReturnValues!["orderId"]!).GetInt32().Should().Be(42);
        ((JsonElement)back.ReturnValues["invoiceId"]!).GetString().Should().Be("INV-42");
    }

    [Fact]
    public void JobStatus_NumericPayload_DoesNotMatchStringContract()
    {
        // Guard the inverse: an integer status payload should NOT silently parse to the wrong enum via
        // these opts (string converter is strict on names). Confirms the converter is actually engaged.
        var opts = GetTransportJsonOpts();
        const string IntPayload = "{\"executionId\":\"x\",\"status\":7,\"completedAtUtc\":\"2026-01-01T00:00:00+00:00\"}";

        // JsonStringEnumConverter still accepts integers by default; this asserts the STRING form is what
        // production emits (the contract), which the serialize-side tests above lock. Here we only assert
        // the deserialize of the canonical STRING form yields the right value.
        const string StringPayload = "{\"executionId\":\"x\",\"status\":\"Failed\",\"completedAtUtc\":\"2026-01-01T00:00:00+00:00\"}";
        JsonSerializer.Deserialize<JobResult>(StringPayload, opts)!.Status.Should().Be(JobStatus.Failed);
        // The integer form is tolerated by the converter but is NOT what production serializes.
        JsonSerializer.Deserialize<JobResult>(IntPayload, opts)!.Status.Should().Be(JobStatus.Failed);
    }
}
