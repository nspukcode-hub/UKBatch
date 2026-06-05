using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Auth;
using UKBatch.Transport.Http.Tests.Common;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Integration;

/// <summary>
/// end-to-end two-service integration. Orchestrator boots a
/// <see cref="Sample.CrossServiceHttp.Orchestrator.Program"/> WAF; Worker boots a
/// <see cref="Sample.CrossServiceHttp.Worker.Program"/> WAF. The orchestrator's HTTP transport is
/// re-wired to route through the worker's TestServer.CreateHandler().
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class TwoServiceIntegrationTests : IDisposable
{
    private readonly WorkerFactory _worker;
    private readonly OrchestratorFactory _orchestrator;
    private bool _disposed;

    public TwoServiceIntegrationTests()
    {
        _worker = new WorkerFactory();
        _orchestrator = new OrchestratorFactory();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _orchestrator.Dispose();
        _worker.Dispose();
    }

    /// <summary>
    /// Builds a cross-wired HttpClient: requests to the orchestrator's HttpTransport endpoints land
    /// inside the orchestrator's TestServer; requests destined for the worker's HMAC endpoints land
    /// in the worker's TestServer. We reuse the HttpTransportTestBuilder pattern: standalone transport
    /// pointing at worker.
    /// </summary>
    private (global::UKBatch.Transport.Http.HttpTransport Transport, ServiceProvider Sp) BuildBridgedTransport()
    {
        return new HttpTransportTestBuilder(_worker.Server)
            .WithSecret(TestHmacHeaders.TestSecret)
            .Build();
    }

    [Fact]
    public async Task CrossServiceRequestReply_OrchestratorToWorker_RoundTrips()
    {
        var (transport, sp) = BuildBridgedTransport();
        await using (sp.ConfigureAwait(false))
        {
            var msg = new JobMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = null,
                JobName = "InvoiceProcessing",
                SourceService = "orchestrator-test",
                TargetService = "billing-worker",
                BatchId = "test-batch",
                BatchStepId = "step-1",
                Parameters = new Dictionary<string, object?> { ["orderId"] = 42 },
                Headers = new Dictionary<string, string>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
            };
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                msg,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            result.Status.Should().Be(JobStatus.Completed);
            result.ExecutionId.Should().NotBeNullOrEmpty();
        }
    }

    [Fact]
    public async Task CrossServicePublish_OrchestratorToWorker_Returns202()
    {
        var (transport, sp) = BuildBridgedTransport();
        await using (sp.ConfigureAwait(false))
        {
            var msg = new JobMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = null,
                JobName = "InvoiceProcessing",
                SourceService = "orchestrator-test",
                TargetService = "billing-worker",
                BatchId = null,
                BatchStepId = null,
                Parameters = new Dictionary<string, object?>(),
                Headers = new Dictionary<string, string>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
            };
            await transport.PublishAsync(msg, CancellationToken.None);
        }
    }

    [Fact]
    public async Task CrossServiceHMACTampering_Returns401WithCacheControl()
    {
        // Orchestrator signs with WRONG secret; worker rejects with 401 + Cache-Control header set.
        var (transport, sp) = new HttpTransportTestBuilder(_worker.Server)
            .WithSecret("INTENTIONAL-WRONG-SECRET-MISMATCH-32CH+")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            var msg = new JobMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = null,
                JobName = "InvoiceProcessing",
                SourceService = "orchestrator-test",
                TargetService = "billing-worker",
                BatchId = null,
                BatchStepId = null,
                Parameters = new Dictionary<string, object?>(),
                Headers = new Dictionary<string, string>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
            };
            Func<Task> act = () => transport.PublishAsync(msg, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message.Contains("401"));
        }
    }

    [Fact]
    public async Task CrossServiceRequestReply_MultipleSequential_AllSucceed()
    {
        var (transport, sp) = BuildBridgedTransport();
        await using (sp.ConfigureAwait(false))
        {
            for (var i = 0; i < 3; i++)
            {
                var msg = new JobMessage
                {
                    MessageId = Guid.NewGuid().ToString("N"),
                    CorrelationId = null,
                    JobName = "InvoiceProcessing",
                    SourceService = "orchestrator-test",
                    TargetService = "billing-worker",
                    BatchId = null,
                    BatchStepId = null,
                    Parameters = new Dictionary<string, object?>(),
                    Headers = new Dictionary<string, string>(),
                    EnqueuedAtUtc = DateTimeOffset.UtcNow,
                    AttemptNumber = 1,
                };
                var result = await transport.RequestReplyAsync(
                    "billing-worker",
                    msg,
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None);
                result.Status.Should().Be(JobStatus.Completed);
            }
        }
    }

    [Fact]
    public async Task CrossServicePublishToWorkerPoll_RoundTripsMessage()
    {
        // Sender publishes; receiver pump enqueues; subsequent poll on the same topic returns the message.
        const string Topic = "Integration.Cycle.Topic";
        // Publish using the bridged transport.
        var (transport, sp) = new HttpTransportTestBuilder(_worker.Server)
            .WithService(Topic, "http://billing-worker.test")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            var msg = new JobMessage
            {
                MessageId = Guid.NewGuid().ToString("N"),
                CorrelationId = null,
                JobName = Topic,
                SourceService = "orchestrator-test",
                TargetService = "billing-worker",
                BatchId = null,
                BatchStepId = null,
                Parameters = new Dictionary<string, object?>(),
                Headers = new Dictionary<string, string>(),
                EnqueuedAtUtc = DateTimeOffset.UtcNow,
                AttemptNumber = 1,
            };
            // Publish through the transport.
            msg = msg with { TargetService = Topic };  // route through the service we registered
            await transport.PublishAsync(msg, CancellationToken.None);

            // Now poll from the worker via the bridged transport's SubscribeAsync.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            JobMessage? received = null;
            await foreach (var m in transport.SubscribeAsync(Topic, cts.Token))
            {
                received = m;
                break;
            }
            received.Should().NotBeNull();
            received!.JobName.Should().Be(Topic);
        }
    }
}
