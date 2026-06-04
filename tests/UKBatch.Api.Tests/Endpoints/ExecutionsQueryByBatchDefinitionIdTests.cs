using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// <c>POST /executions/query</c> filters by the new
/// <c>JobQueryRequest.BatchDefinitionId</c> field; validation rejects empty/too-long inputs;
/// OpenAPI schema surfaces the field.
/// </summary>
public sealed class ExecutionsQueryByBatchDefinitionIdTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public ExecutionsQueryByBatchDefinitionIdTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Query_ByBatchDefinitionId_ReturnsMatchingExecutions()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        // Trigger a batch — its child executions will have BatchDefinitionId == def.Id.
        var batchRunId = await client.TriggerBatchByNameAsync("invoice-pipeline");

        // Resolve the definition id via GET by-name.
        var defResp = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        defResp.IsSuccessStatusCode.Should().BeTrue();
        var defJson = await defResp.Content.ReadAsStringAsync();
        using var defDoc = JsonDocument.Parse(defJson);
        var defId = defDoc.RootElement.GetProperty("id").GetString()!;

        // Poll for batch executions to land in the store (the invoice pipeline has multiple steps).
        // Drive via the batch run id status endpoint until at least one execution is recorded.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var executionsCount = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var statusResp = await client.GetAsync(new Uri($"/api/batches/{batchRunId}/status", UriKind.Relative));
            if (statusResp.IsSuccessStatusCode)
            {
                var statusJson = await statusResp.Content.ReadAsStringAsync();
                using var statusDoc = JsonDocument.Parse(statusJson);
                executionsCount = statusDoc.RootElement.GetProperty("items").GetArrayLength();
                if (executionsCount > 0) break;
            }
            await Task.Delay(200);
        }
        executionsCount.Should().BeGreaterThan(0, "the invoice-pipeline batch must have created executions.");

        // Now query by BatchDefinitionId — should return at least one execution.
        var queryResp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { batchDefinitionId = defId, limit = 100 }));
        queryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var qjson = await queryResp.Content.ReadAsStringAsync();
        using var qdoc = JsonDocument.Parse(qjson);
        var items = qdoc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty("filtering by definition id should return matching executions.");
        // Every item must have batchDefinitionId == defId.
        foreach (var item in items)
        {
            item.GetProperty("batchDefinitionId").GetString().Should().Be(defId);
        }
    }

    [Fact]
    public async Task Query_BatchDefinitionId_TooLong_Returns400()
    {
        using var client = _factory.CreateClient();
        var tooLong = new string('a', 65);
        var resp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { batchDefinitionId = tooLong, limit = 50 }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("batchDefinitionId", "validation error key must surface the field name.");
        body.Should().Contain("64");
    }

    [Fact]
    public async Task Query_BatchDefinitionId_EmptyString_Returns400()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { batchDefinitionId = "", limit = 50 }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("batchDefinitionId");
        body.Should().Contain("empty");
    }

    [Fact]
    public async Task Query_OpenApi_BatchDefinitionIdInRequestSchema()
    {
        // OpenAPI schema reflects the new optional field.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/openapi/v1.json", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var schemas = doc.RootElement.GetProperty("components").GetProperty("schemas");
        schemas.TryGetProperty("JobQueryRequest", out var reqSchema).Should().BeTrue(
            "JobQueryRequest must appear in OpenAPI schemas.");
        var props = reqSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToList();
        props.Should().Contain("batchDefinitionId",
            "batchDefinitionId must surface in JobQueryRequest OpenAPI schema.");
    }
}
