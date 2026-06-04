using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

// <summary> — <c>/batches</c> surface tests.</summary>
public sealed class BatchesEndpointTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public BatchesEndpointTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetBatches_ListsAcrossSources()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        var names = doc.RootElement.GetProperty("items").EnumerateArray()
            .Select(b => b.GetProperty("name").GetString())
            .ToList();
        names.Should().Contain("invoice-pipeline");
    }

    [Fact]
    public async Task GetBatchByName_ReturnsCodeBatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("source").GetString().Should().Be("Code");
    }

    [Fact]
    public async Task GetBatchByName_UnknownName_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/batches/by-name/does-not-exist", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:batch-not-found");
    }

    [Fact]
    public async Task RunBatchByName_Returns202_WithBatchId()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { initialParameters = new Dictionary<string, object?>() }));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("batchId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task RunByName_UnknownName_Returns404_BeforeDispatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/no-such-batch/run", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RunByName_SourceFilterCode_ResolvesCodeBatch()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run?source=Code", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RunByName_SourceFilterDashboard_OnCodeBatchReturns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run?source=Dashboard", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateBatch_CodeSource_Returns400()
    {
        using var client = _factory.CreateClient();
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = "my-batch",
            source = "Code",
            steps = Array.Empty<object>(),
            failurePolicy = "StopOnFailure",
        });
        var response = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task CreateBatch_Dashboard_Returns201_ThenDuplicateReturns409()
    {
        using var client = _factory.CreateClient();
        var uniqueName = $"dashboard-batch-{Guid.NewGuid():N}";
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = uniqueName,
            source = "Dashboard",
            steps = new[]
            {
                new
                {
                    stepId = "s1",
                    order = 0,
                    stepType = "Job",
                    job = new { jobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
                },
            },
            failurePolicy = "StopOnFailure",
        });
        var first = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), payload);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        // Duplicate with the same name within the same source -> 409.
        var dupPayload = DevAuthHttpClientExtensions.JsonContent(new
        {
            name = uniqueName,
            source = "Dashboard",
            steps = new[]
            {
                new
                {
                    stepId = "s1",
                    order = 0,
                    stepType = "Job",
                    job = new { jobName = "Sample.RestApi.Jobs.InvoiceGenerationJob" },
                },
            },
            failurePolicy = "StopOnFailure",
        });
        var dup = await client.PostAsync(new Uri("/api/batches", UriKind.Relative), dupPayload);
        dup.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task DeleteBatch_AbsentId_Returns204_Idempotent()
    {
        using var client = _factory.CreateClient();
        var response = await client.DeleteAsync(new Uri("/api/batches/by-id/nonexistent-id", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteBatch_CodeSource_Returns400()
    {
        // Resolve the Code batch's id via the catalog.
        using var client = _factory.CreateClient();
        var getResp = await client.GetAsync(new Uri("/api/batches/by-name/invoice-pipeline", UriKind.Relative));
        var json = await getResp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var codeId = doc.RootElement.GetProperty("id").GetString();
        codeId.Should().NotBeNullOrWhiteSpace();
        var delResp = await client.DeleteAsync(new Uri($"/api/batches/by-id/{codeId}", UriKind.Relative));
        delResp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BatchRunIdPath_DoesNotClashWithByIdPath()
    {
        // Verify both routes resolve distinctly:
        // /batches/by-id/{id} -> GetBatchById
        // /batches/{batchRunId}/status -> GetBatchRunStatus
        using var client = _factory.CreateClient();
        var byIdResp = await client.GetAsync(new Uri("/api/batches/by-id/nope", UriKind.Relative));
        byIdResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // RUN-keyed: empty list, NOT 404.
        var runResp = await client.GetAsync(new Uri("/api/batches/some-run-id/status", UriKind.Relative));
        runResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
