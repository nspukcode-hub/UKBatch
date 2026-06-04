using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Transport.Http;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Json;

/// <summary>
/// enum serialization must be string-based on BOTH
/// sender and receiver. Drift between worker (configured via <c>AddUKBatchApi</c>'s
/// <c>ConfigureHttpJsonOptions</c>) and HTTP transport's JsonOpts produces dead-letter responses.
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class JsonContractTests
{
    private static JsonSerializerOptions GetTransportJsonOpts()
    {
        // Reflectively read the internal static JsonOpts on HttpTransport so the test locks the
        // value the production code actually uses.
        var field = typeof(HttpTransport).GetField("JsonOpts", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("HttpTransport.JsonOpts internal field not found.");
        return (JsonSerializerOptions)field.GetValue(null)!;
    }

    // lock
    [Fact]
    public void HttpTransport_JobResult_StatusEnum_SerializesAsString_BothEnds()
    {
        var opts = GetTransportJsonOpts();
        var result = new JobResult
        {
            ExecutionId = "x",
            Status = JobStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };
        var json = JsonSerializer.Serialize(result, opts);
        json.Should().Contain("\"status\":\"Completed\"", "JobStatus MUST serialize as the enum name, NOT as a numeric value");

        // Roundtrip
        var back = JsonSerializer.Deserialize<JobResult>(json, opts);
        back.Should().NotBeNull();
        back!.Status.Should().Be(JobStatus.Completed);
    }

    [Fact]
    public void JsonOpts_ContainsJsonStringEnumConverter()
    {
        var opts = GetTransportJsonOpts();
        opts.Converters.OfType<JsonStringEnumConverter>().Should().NotBeEmpty(
 " invariant: JsonOpts MUST include JsonStringEnumConverter so JobStatus round-trips against AddUKBatchApi-configured workers");
    }

    [Fact]
    public void JsonOpts_PropertyNamingPolicy_CamelCase()
    {
        var opts = GetTransportJsonOpts();
        opts.PropertyNamingPolicy.Should().Be(JsonNamingPolicy.CamelCase);
    }
}
