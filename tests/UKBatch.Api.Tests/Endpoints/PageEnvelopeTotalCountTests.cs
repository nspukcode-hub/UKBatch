using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// <c>PageEnvelope.TotalCount</c> must be the filter-wide total ("total count across all
/// pages for the same query"), NOT the size of the returned page. Both execution-listing endpoints
/// regressed on this: with <c>TotalCount == Items.Count</c> a pager computes
/// <c>offset + pageSize &lt; TotalCount</c> as always-false and can never advance past page one.
/// These tests pin the honest behaviour by querying with <c>limit=1</c> after seeding more
/// than one matching execution.
/// </summary>
public sealed class PageEnvelopeTotalCountTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public PageEnvelopeTotalCountTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task QueryExecutions_LimitBelowTotal_TotalCountIsFilterWide()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchRunId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var total = await PollRunExecutionCountAsync(client, batchRunId, minimum: 2);

        // The run produced `total` executions; a limit=1 query for the same run must still
        // report the full total in the envelope.
        var queryResp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { batchId = batchRunId, limit = 1 }));
        queryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await queryResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1, "limit=1 returns one item");
        doc.RootElement.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(total,
            "TotalCount is the filter-wide total, not the page size");
    }

    [Fact]
    public async Task GetBatchRunStatus_LimitBelowTotal_TotalCountIsRunWide()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchRunId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        var total = await PollRunExecutionCountAsync(client, batchRunId, minimum: 2);

        var statusResp = await client.GetAsync(
            new Uri($"/api/batches/{batchRunId}/status?limit=1", UriKind.Relative));
        statusResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await statusResp.Content.ReadAsStringAsync());
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1, "limit=1 returns one item");
        doc.RootElement.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(total,
            "TotalCount is the run-wide total, not the page size");
    }

    /// <summary>
    /// Polls the run-status endpoint (wide limit) until at least <paramref name="minimum"/>
    /// executions of the triggered run are recorded, then returns the observed count.
    /// </summary>
    private static async Task<long> PollRunExecutionCountAsync(HttpClient client, string batchRunId, int minimum)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        long observed = 0;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await client.GetAsync(new Uri($"/api/batches/{batchRunId}/status?limit=500", UriKind.Relative));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                observed = doc.RootElement.GetProperty("items").GetArrayLength();
                if (observed >= minimum) return observed;
            }
            await Task.Delay(200);
        }
        observed.Should().BeGreaterThanOrEqualTo(minimum,
            $"the batch run should have recorded at least {minimum} executions within the deadline");
        return observed;
    }
}
