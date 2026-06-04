using System.Net;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// Hardening tests for <c>/executions</c>: query input bounds (statuses count + search-text
/// length) and cancel idempotency (terminal state ⇒ 204) at the endpoint boundary.
/// </summary>
public sealed class ExecutionsEndpointHardeningTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public ExecutionsEndpointHardeningTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task QueryExecutions_TooManyStatuses_Returns400()
    {
        // Posting > MaxQueryStatusesCount (default 20) status entries must yield 400
        // ValidationProblem with the statuses field flagged.
        using var client = _factory.CreateClient();
        var manyStatuses = Enumerable.Range(0, 50).Select(_ => "Completed").ToArray();
        var resp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { statuses = manyStatuses }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("statuses");
    }

    [Fact]
    public async Task QueryExecutions_TooLongSearchText_Returns400()
    {
        // Posting > MaxQuerySearchTextLength (default 1024) chars in searchText must yield 400
        // ValidationProblem with the searchText field flagged.
        using var client = _factory.CreateClient();
        var hugeSearch = new string('x', 2000);
        var resp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { searchText = hugeSearch }));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("searchText");
    }

    [Fact]
    public async Task CancelExecution_TerminalState_NoThrow_Returns204()
    {
        // Idempotency lock: cancel after the execution has already reached a terminal state must
        // return 204 NoContent (no-op), NOT 409 / 400. The runtime branch is
        // `BatchStateMachine.IsTerminal(current.Status) -> return;` and the endpoint maps it to 204.
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var trigger = await client.PostAsync(
            new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await trigger.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var execId = doc.RootElement.GetProperty("executionId").GetString()!;

        // Poll until the execution terminates (Completed).
        var settled = false;
        for (var i = 0; i < 100; i++)
        {
            var get = await client.GetAsync(new Uri($"/api/executions/{execId}", UriKind.Relative));
            if (get.IsSuccessStatusCode)
            {
                var gjson = await get.Content.ReadAsStringAsync();
                using var gdoc = System.Text.Json.JsonDocument.Parse(gjson);
                var status = gdoc.RootElement.GetProperty("status").GetString();
                if (status is "Completed" or "Failed" or "Cancelled")
                {
                    settled = true;
                    break;
                }
            }
            await Task.Delay(50);
        }
        settled.Should().BeTrue("InvoiceGenerationJob must terminate within ~5s under in-memory store");

        // Now cancel a TERMINAL execution — must be a 204 no-op, NOT an error.
        var cancel = await client.PostAsync(
            new Uri($"/api/executions/{execId}/cancel", UriKind.Relative),
            new StringContent(string.Empty));
        cancel.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }
}
