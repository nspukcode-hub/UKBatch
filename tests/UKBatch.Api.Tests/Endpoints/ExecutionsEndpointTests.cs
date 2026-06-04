using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

// <summary> — <c>/executions</c> surface tests.</summary>
public sealed class ExecutionsEndpointTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public ExecutionsEndpointTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task GetExecution_Unknown_Returns404()
    {
        using var client = _factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/executions/no-such-id", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:execution-not-found");
    }

    [Fact]
    public async Task CancelExecution_Unknown_Returns404_TypedExceptionMapping()
    {
        // mapping — JobExecutionNotFoundException -> 404 ukbatch:execution-not-found.
        using var client = _factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/executions/no-such-id/cancel", UriKind.Relative),
            new StringContent(string.Empty));
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ukbatch:execution-not-found");
    }

    private static readonly string[] StatusFilter = ["Completed", "Failed"];

    [Fact]
    public async Task QueryExecutions_StatusFilter_Returns200()
    {
        using var client = _factory.CreateClient();
        var payload = DevAuthHttpClientExtensions.JsonContent(new
        {
            statuses = StatusFilter,
            limit = 50,
        });
        var response = await client.PostAsync(new Uri("/api/executions/query", UriKind.Relative), payload);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        doc.RootElement.GetProperty("items").ValueKind.Should().Be(JsonValueKind.Array);
    }

    [Fact]
    public async Task QueryExecutions_InvalidLimit_Returns400()
    {
        using var client = _factory.CreateClient();
        var payload = DevAuthHttpClientExtensions.JsonContent(new { limit = 99999 });
        var response = await client.PostAsync(new Uri("/api/executions/query", UriKind.Relative), payload);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Triggered_Job_AppearsInGet_AfterDelay()
    {
        // End-to-end smoke: trigger a job, then GET its execution.
        using var client = _factory.CreateClient();
        var trigger = await client.PostAsync(
            new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await trigger.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var execId = doc.RootElement.GetProperty("executionId").GetString()!;

        // Poll briefly for the execution row to appear in the store.
        HttpResponseMessage? get = null;
        for (var i = 0; i < 10; i++)
        {
            get = await client.GetAsync(new Uri($"/api/executions/{execId}", UriKind.Relative));
            if (get.StatusCode == HttpStatusCode.OK) break;
            await Task.Delay(50);
        }
        get!.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
