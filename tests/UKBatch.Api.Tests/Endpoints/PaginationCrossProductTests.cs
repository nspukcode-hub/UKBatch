using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// pagination edges + partitioned filter + cross-product tests.
/// </summary>
public sealed class PaginationCrossProductTests : IClassFixture<SampleRestApiFactory>
{
    private static readonly string[] CompletedStatusFilter = ["Completed"];
    private readonly SampleRestApiFactory _factory;

    public PaginationCrossProductTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    // Partitioned filter

    [Fact]
    public async Task Jobs_GetCatalog_PartitionedTrue_ReturnsOnlyPartitionedJobs()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?partitioned=true", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty("Sample.RestApi registers BulkArchiveJob as IPartitionedJob<string>.");
        foreach (var item in items)
        {
            item.GetProperty("isPartitioned").GetBoolean().Should().BeTrue(
                "partitioned=true filter must return only partitioned jobs.");
        }
    }

    [Fact]
    public async Task Jobs_GetCatalog_PartitionedFalse_ReturnsOnlyNonPartitionedJobs()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?partitioned=false", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        items.Should().NotBeEmpty();
        foreach (var item in items)
        {
            item.GetProperty("isPartitioned").GetBoolean().Should().BeFalse(
                "partitioned=false filter must return only non-partitioned jobs.");
        }
    }

    [Fact]
    public async Task Jobs_GetCatalog_PartitionedOmitted_ReturnsBoth()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var items = doc.RootElement.GetProperty("items").EnumerateArray().ToList();
        var partitionedKinds = items.Select(i => i.GetProperty("isPartitioned").GetBoolean()).Distinct().ToList();
        partitionedKinds.Should().Contain(true);
        partitionedKinds.Should().Contain(false);
    }

    // Offset/limit edges

    [Fact]
    public async Task Pagination_LimitExceedsTotal_ReturnsTotalCountUnchanged()
    {
        using var client = _factory.CreateClient();
        // limit=200 is well above the registered job count in Sample.RestApi (5 jobs).
        var resp = await client.GetAsync(new Uri("/api/jobs?limit=200", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var itemCount = doc.RootElement.GetProperty("items").GetArrayLength();
        var totalCount = doc.RootElement.GetProperty("totalCount").GetInt32();
        itemCount.Should().Be(totalCount, "limit > total: page returns all items, TotalCount unchanged.");
    }

    [Fact]
    public async Task Pagination_OffsetEqualsTotal_ReturnsEmptyPageWithTotalCount()
    {
        using var client = _factory.CreateClient();
        // First fetch total (respects default MaxPageLimit=500).
        var totalResp = await client.GetAsync(new Uri("/api/jobs?limit=500", UriKind.Relative));
        totalResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var totalBody = await totalResp.Content.ReadAsStringAsync();
        using var totalDoc = JsonDocument.Parse(totalBody);
        var totalCount = totalDoc.RootElement.GetProperty("totalCount").GetInt32();

        var resp = await client.GetAsync(new Uri($"/api/jobs?offset={totalCount}&limit=10", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0,
            "offset = total: page is empty.");
    }

    [Fact]
    public async Task Pagination_OffsetGreaterThanTotal_ReturnsEmpty()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?offset=10000&limit=10", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task Pagination_NegativeOffset_Returns400ValidationProblem()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?offset=-1", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("offset");
    }

    [Fact]
    public async Task Pagination_LimitZero_Returns400ValidationProblem()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?limit=0", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("limit");
    }

    [Fact]
    public async Task Pagination_LimitOverMax_Returns400ValidationProblem()
    {
        // Default MaxPageLimit = 500. Request 10000 → 400.
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync(new Uri("/api/jobs?limit=10000", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("limit");
    }

    // Cross-product

    [Fact]
    public async Task Executions_QueryByStatusAndBatchDefinitionId_AppliesBothFilters()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchRunId = await client.TriggerBatchByNameAsync("invoice-pipeline");

        // Resolve the definition id.
        var defResp = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        var defJson = await defResp.Content.ReadAsStringAsync();
        using var defDoc = JsonDocument.Parse(defJson);
        var defId = defDoc.RootElement.GetProperty("id").GetString()!;

        // Wait for batch to land executions.
        await Task.Delay(2000);

        // Query with both filters: BatchDefinitionId + Statuses=[Completed].
        var queryResp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new
            {
                batchDefinitionId = defId,
                statuses = CompletedStatusFilter,
                limit = 100,
            }));
        queryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var qbody = await queryResp.Content.ReadAsStringAsync();
        using var qdoc = JsonDocument.Parse(qbody);
        var items = qdoc.RootElement.GetProperty("items").EnumerateArray().ToList();
        // Every returned item must satisfy BOTH filters (or be empty if nothing matches).
        foreach (var item in items)
        {
            item.GetProperty("batchDefinitionId").GetString().Should().Be(defId);
            item.GetProperty("status").GetString().Should().Be("Completed");
        }
        // Silence unused var warning when items is empty (still passes — both filters applied).
        _ = batchRunId;
    }
}
