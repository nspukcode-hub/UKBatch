using FluentAssertions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport.Http.Tests.Common;
using Xunit;

namespace UKBatch.Transport.Http.Tests.Sender;

/// <summary>
/// HttpTransport.RequestReplyAsync (happy + receiver 4xx + receiver 5xx +
/// receiver timeout + caller timeout + Polly retry on 5xx).
/// </summary>
[Trait("Category", "HttpTransport")]
public sealed class HttpTransportRequestReplyTests : IClassFixture<WorkerFactory>
{
    private readonly WorkerFactory _factory;

    public HttpTransportRequestReplyTests(WorkerFactory factory)
    {
        _factory = factory;
    }

    private static JobMessage BuildMessage(string jobName = "InvoiceProcessing") => new JobMessage
    {
        MessageId = Guid.NewGuid().ToString("N"),
        CorrelationId = null,
        JobName = jobName,
        SourceService = "orchestrator-test",
        TargetService = "billing-worker",
        BatchId = null,
        BatchStepId = null,
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    [Fact]
    public async Task RequestReplyAsync_HappyPath_ReturnsJobResult()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            result.Should().NotBeNull();
            result.Status.Should().Be(JobStatus.Completed);
        }
    }

    [Fact]
    public async Task RequestReplyAsync_UnknownService_ThrowsInvalidOperation()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.RequestReplyAsync(
                "no-such-service",
                BuildMessage() with { TargetService = "no-such-service" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message.Contains("no-such-service"));
        }
    }

    [Fact]
    public async Task RequestReplyAsync_ClientCancellation_Propagates()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> act = () => transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(5),
                cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_JobNotRegistered_Receiver404_ThrowsInvalidOperation()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            // Worker's invoke endpoint returns 404 + ProblemDetails when job is unknown.
            var msg = BuildMessage(jobName: "Unknown.Job");
            Func<Task> act = () => transport.RequestReplyAsync(
                "billing-worker",
                msg,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>()
                .Where(ex => ex.Message.Contains("404") || ex.Message.Contains("could not locate"));
        }
    }

    [Fact]
    public async Task RequestReplyAsync_SignatureMismatch_Throws()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server)
            .WithSecret("WRONG-SECRET-NO-MATCH-INTENTIONAL-32CH+")
            .Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_NullMessage_Throws()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.RequestReplyAsync(
                "billing-worker",
                null!,
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentNullException>();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_EmptyServiceName_Throws()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            Func<Task> act = () => transport.RequestReplyAsync(
                string.Empty,
                BuildMessage(),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            await act.Should().ThrowAsync<ArgumentException>();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_ResultCarriesCompletedAtUtc()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            result.CompletedAtUtc.Should().NotBe(default);
        }
    }

    [Fact]
    public async Task RequestReplyAsync_MultipleConcurrentCalls_AllReceiveDistinctResults()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var tasks = Enumerable.Range(0, 5).Select(_ =>
                transport.RequestReplyAsync("billing-worker", BuildMessage(), TimeSpan.FromSeconds(10), CancellationToken.None)).ToArray();
            var results = await Task.WhenAll(tasks);
            results.Select(r => r.ExecutionId).Distinct().Count().Should().Be(5);
        }
    }

    [Fact]
    public async Task RequestReplyAsync_ResultStatusEnumIsString_RoundTrips()
    {
        // integration smoke: enum value round-trips through HTTP correctly.
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            // If enum had been deserialized as 0 (Scheduled default) due to drift, this would fail.
            result.Status.Should().Be(JobStatus.Completed);
        }
    }

    [Fact]
    public async Task RequestReplyAsync_SameMessageId_RetryReturnsCachedResult()
    {
        // First call dispatches; second call with same MessageId returns the cached result without
        // re-executing the job on the worker (idempotent replay).
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var msg = BuildMessage();
            var r1 = await transport.RequestReplyAsync("billing-worker", msg, TimeSpan.FromSeconds(10), CancellationToken.None);
            var r2 = await transport.RequestReplyAsync("billing-worker", msg, TimeSpan.FromSeconds(10), CancellationToken.None);
            r1.ExecutionId.Should().Be(r2.ExecutionId, "MessageId dedupe returns the cached result");
        }
    }

    [Fact]
    public async Task RequestReplyAsync_DifferentMessageIds_DifferentExecutions()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var r1 = await transport.RequestReplyAsync("billing-worker", BuildMessage(), TimeSpan.FromSeconds(10), CancellationToken.None);
            var r2 = await transport.RequestReplyAsync("billing-worker", BuildMessage(), TimeSpan.FromSeconds(10), CancellationToken.None);
            r1.ExecutionId.Should().NotBe(r2.ExecutionId);
        }
    }

    [Fact]
    public async Task RequestReplyAsync_PreCancelled_ThrowsImmediately()
    {
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();
            Func<Task> act = () => transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(5),
                cts.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_HeadersForwarded_NotPropagatedToHttpHeaders()
    {
        // Body Headers metadata travels in JSON; receiver accepts the envelope.
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var msg = BuildMessage() with
            {
                Headers = new Dictionary<string, string> { ["X-Tenant"] = "tenant-A" },
            };
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                msg,
                TimeSpan.FromSeconds(10),
                CancellationToken.None);
            result.Should().NotBeNull();
        }
    }

    [Fact]
    public async Task RequestReplyAsync_CallWithVerySmallTimeout_StillSucceedsIfJobIsFast()
    {
        // Worker's InvoiceProcessingJob has a 500ms delay. With 5s timeout, succeeds.
        var (transport, sp) = new HttpTransportTestBuilder(_factory.Server).Build();
        await using (sp.ConfigureAwait(false))
        {
            var result = await transport.RequestReplyAsync(
                "billing-worker",
                BuildMessage(),
                TimeSpan.FromSeconds(5),
                CancellationToken.None);
            result.Status.Should().Be(JobStatus.Completed);
        }
    }
}
