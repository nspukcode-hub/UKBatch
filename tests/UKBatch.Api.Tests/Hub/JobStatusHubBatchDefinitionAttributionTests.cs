using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Hub;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Hub;

/// <summary>
/// verifies the SignalR <c>BatchCompleted</c> summary attributes
/// the correct <see cref="BatchCompletionSummary.BatchDefinitionId"/> and
/// <see cref="BatchCompletionSummary.BatchName"/> rather than the <c>"&lt;unknown&gt;"</c>
/// placeholder.
/// </summary>
public sealed class JobStatusHubBatchDefinitionAttributionTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public JobStatusHubBatchDefinitionAttributionTests(SampleRestApiFactory factory)
    {
        _factory = factory;
    }

    private HubConnection BuildHubConnection()
    {
        var baseUri = _factory.Server.BaseAddress;
        var hubUri = new Uri(baseUri, "/api/hubs/jobs");
        return new HubConnectionBuilder()
            .WithUrl(hubUri, opt =>
            {
                opt.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();
    }

    [Fact]
    public async Task Hub_BatchCompleted_BatchDefinitionId_NotUnknown()
    {
        await using var connection = BuildHubConnection();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var mySummary = new TaskCompletionSource<BatchCompletionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingBatchId = (string?)null;
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            if (pendingBatchId is not null && s.BatchId == pendingBatchId)
            {
                mySummary.TrySetResult(s);
            }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var trigger = await client.PostAsync(
            new Uri("/api/batches/by-name/invoice-pipeline/run", UriKind.Relative),
            DevAuthHttpClientExtensions.JsonContent(new { }));
        trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var json = await trigger.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        pendingBatchId = doc.RootElement.GetProperty("batchId").GetString();
        pendingBatchId.Should().NotBeNullOrEmpty();

        var winner = await Task.WhenAny(mySummary.Task, Task.Delay(TimeSpan.FromSeconds(40), cts.Token));
        winner.Should().Be(mySummary.Task, "the runtime must signal completion within 40s.");

        var summary = await mySummary.Task;
        summary.BatchId.Should().Be(pendingBatchId);
        // fix: BatchDefinitionId is the DEFINITION id, NOT "<unknown>".
        summary.BatchDefinitionId.Should().NotBe("<unknown>", "the definition id must be attributed.");
        summary.BatchDefinitionId.Should().NotBeNullOrEmpty();
        // fix: BatchName is the DEFINITION name, NOT "<unknown>".
        summary.BatchName.Should().Be("invoice-pipeline");

        await connection.StopAsync(cts.Token);
    }

    [Fact]
    public async Task HubFanout_CompletedBatchesLruDedupe_ContinuesEmittingAfterCapacity()
    {
        // integration gate: drive multiple distinct batch ids through the hub
        // fan-out signal pump; assert each surfaces exactly once even after thousands of
        // distinct keys have transited the LRU cache. We can't easily produce 10,001 batch
        // runs in a unit test, but we can verify the LRU cache instance allows distinct
        // batch ids to ALL receive their BatchCompleted (vs. silent drop) — a regression
        // gate ensuring the swap from ConcurrentDictionary → LruDedupeCache didn't break
        // distinct-key dispatch.
        await using var connection = BuildHubConnection();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var allSummaries = new List<BatchCompletionSummary>();
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (allSummaries) { allSummaries.Add(s); }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        // Trigger 3 distinct batches in quick succession.
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var triggeredBatchIds = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            var trigger = await client.PostAsync(
                new Uri("/api/batches/by-name/invoice-pipeline/run", UriKind.Relative),
                DevAuthHttpClientExtensions.JsonContent(new { }));
            trigger.StatusCode.Should().Be(HttpStatusCode.Accepted);
            var json = await trigger.Content.ReadAsStringAsync();
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            triggeredBatchIds.Add(doc.RootElement.GetProperty("batchId").GetString()!);
        }

        // Wait for all 3 BatchCompleted summaries.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(50);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (allSummaries)
            {
                if (triggeredBatchIds.All(id => allSummaries.Any(s => s.BatchId == id)))
                {
                    break;
                }
            }
            await Task.Delay(200, cts.Token);
        }

        lock (allSummaries)
        {
            foreach (var batchId in triggeredBatchIds)
            {
                allSummaries.Should().Contain(s => s.BatchId == batchId,
                    "every distinct batch id must surface in the LRU dedupe path.");
                allSummaries.Where(s => s.BatchId == batchId).Should().HaveCount(1,
                    "dedupe still applies for the same key.");
            }
        }
        await connection.StopAsync(cts.Token);
    }
}
