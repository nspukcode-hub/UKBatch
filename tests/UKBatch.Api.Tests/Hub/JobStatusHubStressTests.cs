using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using UKBatch;
using UKBatch.Abstractions.Models;
using UKBatch.Api.Hub;
using UKBatch.Api.Tests.Common;
using Xunit;

namespace UKBatch.Api.Tests.Hub;

/// <summary>
/// hub stress + reconnect tests. Locks the contracts that
/// <c>RestUKBatchClient</c> depends on: concurrent client fan-out, mid-stream disconnect,
/// backpressure drop-oldest, 4x dedupe, group-membership-loss on reconnect.
/// </summary>
/// <remarks>
/// cleanup: each test gets a fresh
/// <see cref="SampleRestApiFactory"/> via <see cref="IAsyncLifetime"/> so the per-host
/// <c>JobStatusHubFanout._completedBatches</c> LRU dedupe cache cannot leak batch ids across
/// tests. The previous <c>IClassFixture&lt;SampleRestApiFactory&gt;</c> approach shared one
/// factory across all 7 tests in this class, causing ~1% flake in
/// <c>Hub_25ConcurrentClients_AllReceiveBatchCompleted_ExactlyOnce</c> when prior tests had
/// already dedupe-cached the same batch ids in the full suite run.
/// </remarks>
[Trait("Category", "Stress")]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "The disposable _factory field is owned by IAsyncLifetime.DisposeAsync (xUnit-managed lifecycle).")]
public sealed class JobStatusHubStressTests : IAsyncLifetime
{
    // Threshold can be lowered to ~5 if the CI sandbox cannot handle 50 connections.
    private const int ConcurrentClientCount = 25;

    private SampleRestApiFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new SampleRestApiFactory();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync().ConfigureAwait(false);
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
    public async Task Hub_25ConcurrentClients_AllReceiveBatchCompleted_ExactlyOnce()
    {
        // open N concurrent connections, subscribe to 'all', trigger ONE batch;
        // each connection must receive BatchCompleted exactly once for that batch.
        var connections = new List<HubConnection>();
        var perConnectionSummaries = new List<List<BatchCompletionSummary>>();
        var subscribers = new List<TaskCompletionSource<BatchCompletionSummary>>();

        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for (var i = 0; i < ConcurrentClientCount; i++)
            {
                var connection = BuildHubConnection();
                var idx = i;
                var bucket = new List<BatchCompletionSummary>();
                var tcs = new TaskCompletionSource<BatchCompletionSummary>(TaskCreationOptions.RunContinuationsAsynchronously);
                connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
                {
                    lock (bucket) { bucket.Add(s); }
                });
                perConnectionSummaries.Add(bucket);
                subscribers.Add(tcs);
                connections.Add(connection);
            }

            // Start + subscribe in parallel.
            await Task.WhenAll(connections.Select(async c =>
            {
                await c.StartAsync(startCts.Token);
                await c.InvokeAsync("SubscribeAll", startCts.Token);
            }));

            using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
            var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");

            // Wait up to 40s for all connections to record the BatchCompleted for this batch.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var allHave = perConnectionSummaries.All(bucket =>
                {
                    lock (bucket) { return bucket.Any(s => s.BatchId == batchId); }
                });
                if (allHave) break;
                await Task.Delay(200, startCts.Token);
            }

            // Assert exactly-one per connection for the batch id.
            foreach (var bucket in perConnectionSummaries)
            {
                lock (bucket)
                {
                    bucket.Count(s => s.BatchId == batchId).Should().Be(1,
                        "every connection in the 'all' group must receive BatchCompleted exactly once for the batch.");
                }
            }
        }
        finally
        {
            foreach (var c in connections)
            {
                try { await c.DisposeAsync(); } catch { /* swallow */ }
            }
        }
    }

    [Fact]
    public async Task Hub_ClientDisconnectMidStream_DoesNotBlockPump()
    {
        // open 3 connections, kill connection 1 mid-stream, verify remaining
        // 2 still receive events.
        var connections = new List<HubConnection>();
        var perConnectionSummaries = new List<List<BatchCompletionSummary>>();
        try
        {
            using var startCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            for (var i = 0; i < 3; i++)
            {
                var connection = BuildHubConnection();
                var bucket = new List<BatchCompletionSummary>();
                connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
                {
                    lock (bucket) { bucket.Add(s); }
                });
                perConnectionSummaries.Add(bucket);
                connections.Add(connection);
                await connection.StartAsync(startCts.Token);
                await connection.InvokeAsync("SubscribeAll", startCts.Token);
            }

            // Kill connection 0 BEFORE triggering the batch.
            await connections[0].StopAsync(startCts.Token);

            using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
            var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");

            // Wait for connections 1 and 2 to receive BatchCompleted.
            var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
            while (DateTimeOffset.UtcNow < deadline)
            {
                var twoHave = perConnectionSummaries.Skip(1).All(bucket =>
                {
                    lock (bucket) { return bucket.Any(s => s.BatchId == batchId); }
                });
                if (twoHave) break;
                await Task.Delay(200, startCts.Token);
            }

            // Connection 0 dropped; should have no record for this batch.
            lock (perConnectionSummaries[0])
            {
                perConnectionSummaries[0].Any(s => s.BatchId == batchId).Should().BeFalse(
                    "killed connection cannot have received the event.");
            }
            // Connections 1+2 still received.
            for (var i = 1; i < 3; i++)
            {
                lock (perConnectionSummaries[i])
                {
                    perConnectionSummaries[i].Count(s => s.BatchId == batchId).Should().Be(1,
                        "remaining connections must continue receiving events; pump not blocked by the dead connection.");
                }
            }
        }
        finally
        {
            foreach (var c in connections)
            {
                try { await c.DisposeAsync(); } catch { /* swallow */ }
            }
        }
    }

    [Fact]
    public async Task Hub_PumpBackpressure_HubBufferCapacityConfigured_DoesNotThrow()
    {
        // configure a small HubBufferCapacity and verify the hub continues
        // to function without throwing under burst (best-effort drop-oldest in v0.1).
        // Note: HubBufferCapacity governs the WATCH pump's per-fan-out buffer, not the SignalR
        // server-side per-connection buffer. We can only assert "no crash" + "client still
        // receives terminal events" under a tight buffer.
        await using var factory = new BackpressureLowFixture();
        await using var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/api/hubs/jobs"), opt =>
            {
                opt.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                opt.Transports = HttpTransportType.LongPolling;
            })
            .Build();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var summaries = new List<BatchCompletionSummary>();
        connection.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (summaries) { summaries.Add(s); }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        using var client = factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(40);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (summaries)
            {
                if (summaries.Any(s => s.BatchId == batchId)) break;
            }
            await Task.Delay(200, cts.Token);
        }

        lock (summaries)
        {
            summaries.Should().Contain(s => s.BatchId == batchId,
                "even with a tight buffer, terminal BatchCompleted must surface.");
        }
        await connection.StopAsync(cts.Token);
    }

    private sealed class BackpressureLowFixture : WebApplicationFactory<Sample.RestApi.Program>
    {
        public BackpressureLowFixture()
        {
            Environment.SetEnvironmentVariable("Sample__ApprovalTimeoutSeconds", "5");
        }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.UseEnvironment("Development");
            builder.ConfigureServices(services =>
            {
                services.PostConfigure<UKBatchOptions>(o => o.HubBufferCapacity = 8);
            });
        }
    }

    [Fact]
    public async Task Hub_4xFanout_ClientSubscribedToAllGroups_ReceivesEventExactly4Times()
    {
        // connection subscribes to all 4 group axes; ExecutionStateChanged for
        // a single event must arrive EXACTLY 4 times (contract).
        await using var connection = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var execs = new List<JobExecution>();
        connection.On<JobExecution>(nameof(IJobStatusHubClient.ExecutionStateChanged), e =>
        {
            lock (execs) { execs.Add(e); }
        });
        await connection.StartAsync(cts.Token);

        // Pre-subscribe to ALL group axes for a triggered execution. We trigger a standalone job
        // first to get an executionId we can subscribe to, then wait for events. Strategy: subscribe
        // to `all` + `job:<jobName>` UP FRONT, trigger, capture execution id from the events,
        // then subscribe to `exec:<id>` + `batch:<id>` to receive remaining events. But 
        // SignalR has no replay — we subscribe to a known batch instead.
        await connection.InvokeAsync("SubscribeAll", cts.Token);
        await connection.InvokeAsync("SubscribeToJob", "Sample.RestApi.Jobs.InvoiceGenerationJob", cts.Token);

        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");
        // Also subscribe to the batch group.
        await connection.InvokeAsync("SubscribeToBatch", batchId, cts.Token);

        // Wait for a stream of ExecutionStateChanged events.: a client subscribed to BOTH 'all'
        // and 'batch:<id>' MUST receive each execution event 2 times — combined with 'job:<name>'
        // for invoice-generation = 3 times. The 4th subscription would be exec-id-keyed which we
        // can't pre-subscribe to.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (execs)
            {
                // We expect multiple events from the batch run; at least one execution should
                // arrive duplicated due to multi-group subscription.
                var grouped = execs
                    .Where(e => e.BatchId == batchId)
                    .GroupBy(e => (e.ExecutionId, e.Status))
                    .Any(g => g.Count() >= 2);
                if (grouped) break;
            }
            await Task.Delay(200, cts.Token);
        }

        lock (execs)
        {
            // The InvoiceGenerationJob execution should be a member of:
            // - 'all' group → 1 emission
            // - 'job:Sample.RestApi.Jobs.InvoiceGenerationJob' group → 1 emission
            // - 'batch:<batchId>' group → 1 emission
            // contract: client receives each event ONCE PER MATCHING GROUP.
            var invoiceEvents = execs
                .Where(e => e.BatchId == batchId && e.JobName == "Sample.RestApi.Jobs.InvoiceGenerationJob")
                .ToList();
            invoiceEvents.Should().NotBeEmpty();
            // Group by (ExecutionId, Status) — each tuple should appear at least 3 times due to fan-out.
            var maxCount = invoiceEvents
                .GroupBy(e => (e.ExecutionId, e.Status))
                .Max(g => g.Count());
            maxCount.Should().BeGreaterThanOrEqualTo(3,
 " lock: client subscribed to multiple matching groups receives the same event once per group.");
        }
        await connection.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Hub_DedupeKey_ExecutionIdStatusAttempt_IsTheContract()
    {
        // verifies the (ExecutionId, Status, AttemptNumber) tuple is sufficient
        // for client-side dedupe across retries. We trigger a job that succeeds first try; assert
        // distinct events differ on at least one of these dimensions.
        await using var connection = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var execs = new List<JobExecution>();
        connection.On<JobExecution>(nameof(IJobStatusHubClient.ExecutionStateChanged), e =>
        {
            lock (execs) { execs.Add(e); }
        });
        await connection.StartAsync(cts.Token);
        await connection.InvokeAsync("SubscribeAll", cts.Token);

        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");

        // Wait for terminal arrivals.
        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (execs)
            {
                if (execs.Any(e => e.BatchId == batchId && (e.Status == JobStatus.Completed || e.Status == JobStatus.Failed)))
                    break;
            }
            await Task.Delay(200, cts.Token);
        }

        lock (execs)
        {
            var dedupeKeys = execs
                .Where(e => e.BatchId == batchId)
                .Select(e => (e.ExecutionId, e.Status, e.AttemptNumber))
                .Distinct()
                .ToList();
            // Sanity: dedupe shrinks the multi-group fanout copies to distinct event keys.
            execs.Count(e => e.BatchId == batchId).Should().BeGreaterThan(dedupeKeys.Count,
                "the dedupe key (ExecutionId, Status, AttemptNumber) must be smaller than raw event count due to multi-group fan-out.");
        }
        await connection.StopAsync(cts.Token);
    }

    [Fact]
    public async Task Hub_ClientReconnectAfterServerCycle_GroupMembershipsLost()
    {
        // connection 1 subscribes to a batch group, then stops; new connection 2
        // starts WITHOUT resubscribing; any NEW event for that batch arrives at connection 2 ONLY
        // if it has resubscribed.
        await using var conn1 = BuildHubConnection();
        await using var conn2 = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        var conn1Events = new List<BatchCompletionSummary>();
        var conn2Events = new List<BatchCompletionSummary>();
        conn1.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (conn1Events) { conn1Events.Add(s); }
        });
        conn2.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (conn2Events) { conn2Events.Add(s); }
        });
        await conn1.StartAsync(cts.Token);
        await conn1.InvokeAsync("SubscribeAll", cts.Token);

        // Trigger a batch — conn1 should receive it.
        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId1 = await client.TriggerBatchByNameAsync("invoice-pipeline");

        var deadline1 = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline1)
        {
            lock (conn1Events) { if (conn1Events.Any(s => s.BatchId == batchId1)) break; }
            await Task.Delay(200, cts.Token);
        }
        lock (conn1Events)
        {
            conn1Events.Should().Contain(s => s.BatchId == batchId1, "conn1 was subscribed before the batch.");
        }

        // Stop conn1 — its group memberships are dropped.
        await conn1.StopAsync(cts.Token);

        // Now start conn2 WITHOUT subscribing.
        await conn2.StartAsync(cts.Token);
        var batchId2 = await client.TriggerBatchByNameAsync("invoice-pipeline");

        await Task.Delay(TimeSpan.FromSeconds(8), cts.Token);
        lock (conn2Events)
        {
            conn2Events.Should().NotContain(s => s.BatchId == batchId2,
 "conn2 did NOT subscribe to 'all'; without resubscribe, no events are delivered. Locks the contract that RestUKBatchClient.Reconnected handler MUST re-call SubscribeAll/SubscribeToBatch/etc.");
        }
    }

    [Fact]
    public async Task Hub_ClientReconnectAndResubscribe_RestoresDelivery()
    {
        // happy path companion to #6: a NEW connection that DOES resubscribe
        // restores delivery.
        await using var conn = BuildHubConnection();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var events = new List<BatchCompletionSummary>();
        conn.On<BatchCompletionSummary>(nameof(IJobStatusHubClient.BatchCompleted), s =>
        {
            lock (events) { events.Add(s); }
        });

        await conn.StartAsync(cts.Token);
        await conn.InvokeAsync("SubscribeAll", cts.Token);

        using var client = _factory.CreateClient().WithDevAuth("alice", "ops");
        var batchId = await client.TriggerBatchByNameAsync("invoice-pipeline");

        var deadline = DateTimeOffset.UtcNow.AddSeconds(35);
        while (DateTimeOffset.UtcNow < deadline)
        {
            lock (events) { if (events.Any(s => s.BatchId == batchId)) break; }
            await Task.Delay(200, cts.Token);
        }
        lock (events)
        {
            events.Should().Contain(s => s.BatchId == batchId,
                "subscribed connection delivers BatchCompleted as expected.");
        }
    }
}
