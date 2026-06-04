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
/// dedupe lock — verifies that even if the runtime channel writer is
/// invoked twice for the same batch id (defense-in-depth scenario; v0.1 runtime only writes once
/// but adapter packages might re-write), the hub emits BatchCompleted exactly once.
/// </summary>
public sealed class JobStatusHubFanoutDedupeTests : IClassFixture<SampleRestApiFactory>
{
    private readonly SampleRestApiFactory _factory;

    public JobStatusHubFanoutDedupeTests(SampleRestApiFactory factory)
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
    public async Task Hub_LateTerminalEventAfterBatchCompleted_DoesNotEmitDuplicate()
    {
        // dedupe lock + peer B-PASS3-3. Strategy:
        // 1. Subscribe to 'all' on the hub.
        // 2. Trigger a batch, await the BatchCompleted summary.
        // 3. Drive a SECOND signal for the SAME batch run id via the internal
        // BatchCompletionSignal singleton (resolved via reflection — Core doesn't grant
        // InternalsVisibleTo to Api.Tests, so we use the internal type lookup).
        // 4. Verify no second BatchCompleted arrives for that batch id within a settling window.
        await using var connection = BuildHubConnection();
        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));

        var allSummaries = new List<BatchCompletionSummary>();
        var pendingBatchId = (string?)null;
        var mySummary = new TaskCompletionSource<BatchCompletionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (allSummaries)
            {
                allSummaries.Add(s);
                if (pendingBatchId is not null && s.BatchId == pendingBatchId)
                {
                    mySummary.TrySetResult(s);
                }
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
        var batchId = doc.RootElement.GetProperty("batchId").GetString()!;
        lock (allSummaries)
        {
            pendingBatchId = batchId;
            var existing = allSummaries.FirstOrDefault(s => s.BatchId == batchId);
            if (existing is not null) mySummary.TrySetResult(existing);
        }

        var winner = await Task.WhenAny(mySummary.Task, Task.Delay(TimeSpan.FromSeconds(40), cts.Token));
        winner.Should().Be(mySummary.Task, "the runtime must signal completion within 40s.");

        // Re-signal the SAME batch id via reflection on the internal BatchCompletionSignal type.
        // signal now accepts BatchCompletionSignalPayload, not bare string.
        var assembly = typeof(JobStatusHub).Assembly.GetReferencedAssemblies()
            .Select(System.Reflection.Assembly.Load)
            .First(a => a.GetName().Name == "UKBatch.Core");
        var signalType = assembly.GetType("UKBatch.Runtime.BatchCompletionSignal", throwOnError: false);
        if (signalType is null)
        {
            // Type relocation = hard fail rather than silent skip.
            throw new InvalidOperationException(
 "UKBatch.Runtime.BatchCompletionSignal type not found — internal seam relocation?");
        }
        var payloadType = assembly.GetType("UKBatch.Runtime.BatchCompletionSignalPayload", throwOnError: false);
        if (payloadType is null)
        {
            throw new InvalidOperationException(
 "UKBatch.Runtime.BatchCompletionSignalPayload type not found — internal seam relocation?");
        }
        var signalSvc = _factory.Services.GetService(signalType);
        signalSvc.Should().NotBeNull("BatchCompletionSignal must be registered as a singleton via UKBatchBuilder.");
        var signalMethod = signalType.GetMethod("Signal");
        signalMethod.Should().NotBeNull();

        // Construct BatchCompletionSignalPayload via reflection (internal type).
        var payload = Activator.CreateInstance(payloadType)!;
        payloadType.GetProperty("BatchRunId")!.SetValue(payload, batchId);
        payloadType.GetProperty("BatchDefinitionId")!.SetValue(payload, "test-def-id");
        payloadType.GetProperty("BatchName")!.SetValue(payload, "test-batch");
        // Drive the signal a second time for the SAME batch id.
        signalMethod!.Invoke(signalSvc, new object[] { payload });

        // Wait a settling window — any duplicate summary would arrive within ~1s.
        await Task.Delay(TimeSpan.FromSeconds(2), cts.Token);
        lock (allSummaries)
        {
            allSummaries.Where(s => s.BatchId == batchId).Should().HaveCount(1,
 " dedupe lock: second Signal call for the same batch id must NOT cause a duplicate BatchCompleted emission.");
        }
        await connection.StopAsync(cts.Token);
    }
}
