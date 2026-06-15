using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// The run-paginated history endpoint (<c>GET /batches/runs</c>) and the administrative run-cancel
/// endpoint (<c>POST /batches/{batchRunId}/cancel</c>) over the real WAF. Pins: the page envelope carries
/// the filter-wide <c>TotalCount</c> (not the page size), the <c>batchDefinitionId</c> filter, the
/// <c>includeRunning=false</c> exclusion of in-progress runs, pagination bounds validation (400), and the
/// idempotent 204 cancel (twice + for an unknown id).
/// </summary>
public sealed class BatchRunEndpointsTests : IClassFixture<SampleRestApiFactory>
{
    private const string InvoicePipeline = "invoice-pipeline";
    private const string WildcardApprovalPipeline = "wildcard-approval-pipeline";

    private readonly SampleRestApiFactory _factory;

    public BatchRunEndpointsTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private static async Task<string> ResolveDefinitionIdAsync(HttpClient client, string name)
    {
        var resp = await client.GetAsync(new Uri($"/api/batches/by-name/{name}", UriKind.Relative));
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>Polls GET /batches/runs (wide limit) until at least <paramref name="minimum"/> runs match the query, then returns the page.</summary>
    private static async Task<JsonDocument> PollRunsUntilAsync(HttpClient client, string queryString, int minimum)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        JsonDocument? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await client.GetAsync(new Uri($"/api/batches/runs{queryString}", UriKind.Relative));
            if (resp.IsSuccessStatusCode)
            {
                last?.Dispose();
                last = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (last.RootElement.GetProperty("items").GetArrayLength() >= minimum)
                {
                    return last;
                }
            }
            await Task.Delay(200);
        }
        last.Should().NotBeNull($"GET /batches/runs{queryString} should return at least {minimum} run(s) within the deadline");
        last!.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(minimum);
        return last;
    }

    [Fact]
    public async Task GetRuns_ReturnsPageEnvelope_WithFilterWideTotalCount()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var defId = await ResolveDefinitionIdAsync(client, InvoicePipeline);

        // Seed several runs of the invoice pipeline.
        for (var i = 0; i < 3; i++)
        {
            await client.TriggerBatchByNameAsync(InvoicePipeline);
        }

        // Wait until at least 3 runs exist for this definition, then request a single-item page: the
        // envelope must still report the filter-wide total.
        using var seeded = await PollRunsUntilAsync(client, $"?batchDefinitionId={defId}&limit=500", minimum: 3);
        var seededTotal = seeded.RootElement.GetProperty("items").GetArrayLength();

        var resp = await client.GetAsync(new Uri($"/api/batches/runs?batchDefinitionId={defId}&limit=1", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(1, "limit=1 returns one item");
        doc.RootElement.GetProperty("totalCount").GetInt64().Should().BeGreaterThanOrEqualTo(seededTotal,
            "TotalCount is the filter-wide total, not the page size");
    }

    [Fact]
    public async Task GetRuns_FilterByBatchDefinitionId_ReturnsOnlyThatDefinitionsRuns()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var invoiceDefId = await ResolveDefinitionIdAsync(client, InvoicePipeline);

        await client.TriggerBatchByNameAsync(InvoicePipeline);

        using var page = await PollRunsUntilAsync(client, $"?batchDefinitionId={invoiceDefId}&limit=500", minimum: 1);
        var defIds = page.RootElement.GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("batchDefinitionId").GetString())
            .ToList();

        defIds.Should().NotBeEmpty();
        defIds.Should().AllBe(invoiceDefId, "the batchDefinitionId filter returns only runs of that definition");
    }

    [Fact]
    public async Task GetRuns_IncludeRunningFalse_ExcludesAnInProgressRun()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var wildcardDefId = await ResolveDefinitionIdAsync(client, WildcardApprovalPipeline);

        // The wildcard approval pipeline parks on a gate, so its run stays in-progress (Status null).
        var runId = await client.TriggerBatchByNameAsync(WildcardApprovalPipeline);

        // It must surface while running with includeRunning=true ...
        using var running = await PollRunsUntilAsync(client, $"?batchDefinitionId={wildcardDefId}&includeRunning=true&limit=500", minimum: 1);
        var runningIds = running.RootElement.GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("batchId").GetString())
            .ToList();
        runningIds.Should().Contain(runId, "an in-progress run is visible with includeRunning=true");

        // ... and must be absent with includeRunning=false (null-status runs are excluded).
        var excludedResp = await client.GetAsync(
            new Uri($"/api/batches/runs?batchDefinitionId={wildcardDefId}&includeRunning=false&limit=500", UriKind.Relative));
        excludedResp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var excluded = JsonDocument.Parse(await excludedResp.Content.ReadAsStringAsync());
        var excludedIds = excluded.RootElement.GetProperty("items").EnumerateArray()
            .Select(r => r.GetProperty("batchId").GetString())
            .ToList();
        excludedIds.Should().NotContain(runId, "includeRunning=false excludes the still-running run");

        // Clean up: cancel the parked run so it terminalizes.
        await client.PostAsync(new Uri($"/api/batches/{runId}/cancel", UriKind.Relative), new StringContent(string.Empty));
    }

    [Theory]
    [InlineData("?limit=99999")]   // above MaxPageLimit (500)
    [InlineData("?offset=-1")]
    [InlineData("?limit=0")]
    public async Task GetRuns_InvalidPagination_Returns400(string queryString)
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var resp = await client.GetAsync(new Uri($"/api/batches/runs{queryString}", UriKind.Relative));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "out-of-range pagination is a validation error");
    }

    [Fact]
    public async Task CancelRun_UnknownId_Returns204_Idempotent()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");

        var first = await client.PostAsync(
            new Uri("/api/batches/00000000000000000000000000000000/cancel", UriKind.Relative), new StringContent(string.Empty));
        first.StatusCode.Should().Be(HttpStatusCode.NoContent, "cancelling an unknown run is a 204 no-op (the canceller boolean is internal)");

        var second = await client.PostAsync(
            new Uri("/api/batches/00000000000000000000000000000000/cancel", UriKind.Relative), new StringContent(string.Empty));
        second.StatusCode.Should().Be(HttpStatusCode.NoContent, "cancel is idempotent — 204 on a repeat call too");
    }

    [Fact]
    public async Task CancelRun_LiveRun_Returns204_AndRunTerminalizes()
    {
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var wildcardDefId = await ResolveDefinitionIdAsync(client, WildcardApprovalPipeline);

        // Park a run on the approval gate, confirm it is in-progress, then cancel it.
        var runId = await client.TriggerBatchByNameAsync(WildcardApprovalPipeline);
        using (await PollRunsUntilAsync(client, $"?batchDefinitionId={wildcardDefId}&includeRunning=true&limit=500", minimum: 1))
        {
            // running
        }

        var cancelResp = await client.PostAsync(new Uri($"/api/batches/{runId}/cancel", UriKind.Relative), new StringContent(string.Empty));
        cancelResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // The cancelled run eventually leaves the running set (its terminal status is recorded).
        var deadline = DateTimeOffset.UtcNow.AddSeconds(60);
        var noLongerRunning = false;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var resp = await client.GetAsync(
                new Uri($"/api/batches/runs?batchDefinitionId={wildcardDefId}&includeRunning=false&limit=500", UriKind.Relative));
            if (resp.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                var terminalIds = doc.RootElement.GetProperty("items").EnumerateArray()
                    .Select(r => r.GetProperty("batchId").GetString())
                    .ToList();
                if (terminalIds.Contains(runId))
                {
                    noLongerRunning = true;
                    break;
                }
            }
            await Task.Delay(200);
        }
        noLongerRunning.Should().BeTrue("the cancelled run must reach a terminal status and appear in the includeRunning=false list");
    }
}
