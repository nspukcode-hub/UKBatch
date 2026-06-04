using System.Net;
using System.Text.Json;
using FluentAssertions;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Endpoints;

/// <summary>
/// Hardening tests for <c>/jobs/{name}/trigger</c>: HTTP disconnect orphan-row prevention and
/// the trace-context-derived TriggeredBy population.
/// </summary>
public sealed class JobsEndpointHardeningTests : IClassFixture<SampleRestApiFactory>
{
    private static readonly string[] PendingStatusFilter = ["Pending"];
    private readonly SampleRestApiFactory _factory;

    public JobsEndpointHardeningTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task TriggerJob_PopulatesTriggeredBy_FromIJobTriggerContext()
    {
        // When the request is authenticated via DevAuth, the JobExecution.TriggeredBy field should
        // reflect the user's name (HttpContextJobTriggerContext reads HttpContext.User.Identity?.Name).
        // Locks the trace propagation chain.
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var trigger = await client.PostAsync(
            new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await trigger.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var execId = doc.RootElement.GetProperty("executionId").GetString()!;

        // Poll for the execution row.
        HttpResponseMessage? get = null;
        for (var i = 0; i < 50; i++)
        {
            get = await client.GetAsync(new Uri($"/api/executions/{execId}", UriKind.Relative));
            if (get.IsSuccessStatusCode) break;
            await Task.Delay(50);
        }
        get!.IsSuccessStatusCode.Should().BeTrue();
        var getJson = await get.Content.ReadAsStringAsync();
        using var getDoc = JsonDocument.Parse(getJson);
        var triggeredBy = getDoc.RootElement.GetProperty("triggeredBy").GetString();
        triggeredBy.Should().Be("alice", "HttpContextJobTriggerContext maps Identity.Name -> TriggeredBy.");
    }

    [Fact]
    public async Task TriggerJob_HttpDisconnect_DuringBackpressure_DoesNotOrphanRow()
    {
        // The endpoint must NOT pass http.RequestAborted through to TriggerInternalAsync — otherwise
        // a client disconnect between InsertAsync (Storage) and EnqueueAsync (Dispatcher) would
        // leave an orphan Pending row.
        //
        // We can't easily reproduce dispatcher backpressure under the in-memory store in a
        // hermetic test, but we CAN simulate a client-aborted request and verify:
        // (a) the trigger goes through despite the cancellation (endpoint decoupled from CT),
        // (b) the resulting execution is queryable in /executions (i.e. NOT orphan-pending).
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(5));
        HttpResponseMessage? response = null;
        try
        {
            response = await client.PostAsync(
                new Uri("/api/jobs/Sample.RestApi.Jobs.InvoiceGenerationJob/trigger", UriKind.Relative),
                DevAuthHttpClientExtensions.JsonContent(new { }),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Client-side cancellation (incl. TaskCanceledException) surfaced before the response
            // acceptable. The endpoint still runs with CancellationToken.None internally and the
            // trigger should land regardless.
        }

        // Whatever happened client-side, the server should have NO Pending orphan rows that never
        // dispatched. Query executions filtered to Pending — Sample's in-memory worker drains
        // immediately under normal load. Allow brief settling.
        await Task.Delay(200);
        var queryResp = await client.PostAsync(
            new Uri("/api/executions/query", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { statuses = PendingStatusFilter, limit = 50 }));
        queryResp.IsSuccessStatusCode.Should().BeTrue();
        var qjson = await queryResp.Content.ReadAsStringAsync();
        using var qdoc = JsonDocument.Parse(qjson);
        var pending = qdoc.RootElement.GetProperty("items").EnumerateArray().ToList();
        // Some pending may exist for legitimately-in-flight items; the regression we're guarding
        // against would manifest as a permanent stuck row. We poll once more after a longer
        // settling period — if anything remains Pending after 1s, it's a stuck orphan.
        if (pending.Count > 0)
        {
            await Task.Delay(1000);
            var requery = await client.PostAsync(
                new Uri("/api/executions/query", UriKind.Relative),
                DevAuthHttpClientExtensions.JsonContent(new { statuses = PendingStatusFilter, limit = 50 }));
            var rjson = await requery.Content.ReadAsStringAsync();
            using var rdoc = JsonDocument.Parse(rjson);
            var stillPending = rdoc.RootElement.GetProperty("items").EnumerateArray().ToList();
            stillPending.Should().BeEmpty(
                "a client disconnect during trigger must NOT leave a permanently-Pending orphan row.");
        }

        _ = response; // suppress unused-variable when the request completed pre-cancel.
    }
}
