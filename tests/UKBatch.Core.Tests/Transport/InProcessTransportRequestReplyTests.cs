using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;
using UKBatch.Transport;
using Xunit;

namespace UKBatch.Core.Tests.Transport;

/// <summary>
/// RequestReplyAsync — mixed timeouts / cancellations leave
/// _pendingReplies empty (no leak).
/// </summary>
public class InProcessTransportRequestReplyTests
{
    private static JobMessage NewMessage(string id, string jobName = "rpc.echo") => new()
    {
        MessageId = id,
        CorrelationId = id,
        JobName = jobName,
        SourceService = "src",
        Parameters = new Dictionary<string, object?>(),
        Headers = new Dictionary<string, string>(),
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
        AttemptNumber = 1,
    };

    private static JobResult NewResult(string execId) => new()
    {
        ExecutionId = execId,
        Status = JobStatus.Completed,
        CompletedAtUtc = DateTimeOffset.UtcNow,
    };

    private static int PendingRepliesCount(InProcessTransport t)
    {
        var field = typeof(InProcessTransport).GetField("_pendingReplies", BindingFlags.Instance | BindingFlags.NonPublic);
        var dict = field!.GetValue(t) as System.Collections.Concurrent.ConcurrentDictionary<string, TaskCompletionSource<JobResult>>;
        return dict!.Count;
    }

    [Fact]
    public async Task RequestReply_ImmediateCompleteReply_ReturnsResult()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var msg = NewMessage("req1");

        // Race the request and the reply; CompleteReply is invoked from another task.
        var rpcTask = transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(5), default);
        await Task.Delay(50).ConfigureAwait(false); // give RequestReply time to register
        transport.CompleteReply("req1", NewResult("exec-1")).Should().BeTrue();

        var result = await rpcTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        result.ExecutionId.Should().Be("exec-1");

        PendingRepliesCount(transport).Should().Be(0);
    }

    [Fact]
    public async Task RequestReply_Timeout_ThrowsAndLeavesNoLeak()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var msg = NewMessage("rpc-timeout");

        Func<Task> act = async () =>
            await transport.RequestReplyAsync("svc", msg, TimeSpan.FromMilliseconds(100), default).ConfigureAwait(false);
        await act.Should().ThrowAsync<TimeoutException>().ConfigureAwait(false);

        PendingRepliesCount(transport).Should().Be(0);
    }

    [Fact]
    public async Task RequestReply_Cancellation_ThrowsAndLeavesNoLeak()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var msg = NewMessage("rpc-cancel");
        using var cts = new CancellationTokenSource();

        var rpcTask = transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(5), cts.Token);
        await Task.Delay(50).ConfigureAwait(false);
        cts.Cancel();

        Func<Task> act = async () => await rpcTask.ConfigureAwait(false);
        await act.Should().ThrowAsync<OperationCanceledException>().ConfigureAwait(false);

        PendingRepliesCount(transport).Should().Be(0);
    }

    [Fact]
    public async Task RequestReply_100ConcurrentMixedOutcomes_NoLeak()
    {
        // #9 acceptance — 100 concurrent requests, 50 succeed, 30 timeout, 20 cancel.
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        const int Succeed = 50;
        const int Timeout = 30;
        const int Cancel = 20;
        const int Total = Succeed + Timeout + Cancel;

        var rpcTasks = new List<Task<JobResult>>();
        var cancellationSources = new List<CancellationTokenSource>();

        for (var i = 0; i < Succeed; i++)
        {
            var msg = NewMessage($"ok-{i}");
            rpcTasks.Add(transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(10), default));
        }
        for (var i = 0; i < Timeout; i++)
        {
            var msg = NewMessage($"to-{i}");
            rpcTasks.Add(transport.RequestReplyAsync("svc", msg, TimeSpan.FromMilliseconds(150), default));
        }
        for (var i = 0; i < Cancel; i++)
        {
            var msg = NewMessage($"cn-{i}");
            var cts = new CancellationTokenSource();
            cancellationSources.Add(cts);
            rpcTasks.Add(transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(10), cts.Token));
        }

        await Task.Delay(100).ConfigureAwait(false); // let all register

        // Complete the 50 successful ones.
        for (var i = 0; i < Succeed; i++)
        {
            transport.CompleteReply($"ok-{i}", NewResult($"e-{i}"));
        }

        // Cancel the 20 cancellation cases.
        foreach (var cts in cancellationSources)
        {
            cts.Cancel();
        }

        // Wait for everything to settle (timeouts will fire on their own).
        await Task.WhenAll(rpcTasks.Select(async t => { try { _ = await t.ConfigureAwait(false); return 0; } catch { return 1; } })).ConfigureAwait(false);

        // Give the timeout cleanup time to complete.
        await Task.Delay(200).ConfigureAwait(false);

        PendingRepliesCount(transport).Should().Be(0, "all pending replies should be drained — no leak");

        // Verify counts.
        var settled = rpcTasks.Select(t => (t.Status, t.IsCompletedSuccessfully)).ToList();
        settled.Count(s => s.IsCompletedSuccessfully).Should().Be(Succeed);
        // Disposing CTSes.
        foreach (var cts in cancellationSources) { cts.Dispose(); }
        _ = Total;
    }

    [Fact]
    public async Task RequestReply_DuplicateMessageId_Throws()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var msg = NewMessage("dup");

        var rpc1 = transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(5), default);
        await Task.Delay(20).ConfigureAwait(false);

        Func<Task> act = async () => await transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(5), default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Duplicate*").ConfigureAwait(false);

        transport.CompleteReply("dup", NewResult("e1"));
        _ = await rpc1.ConfigureAwait(false);
    }

    [Fact]
    public async Task CompleteReply_NoPendingRequest_ReturnsFalseAndDoesNotThrow()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var ok = transport.CompleteReply("never-registered", NewResult("e1"));
        ok.Should().BeFalse();
        // Should also leave the dict at zero.
        await Task.Delay(20).ConfigureAwait(false);
        PendingRepliesCount(transport).Should().Be(0);
    }

    [Fact]
    public async Task FailReply_PropagatesExceptionToCaller()
    {
        var transport = new InProcessTransport(NullLogger<InProcessTransport>.Instance);
        var msg = NewMessage("fail1");
        var rpc = transport.RequestReplyAsync("svc", msg, TimeSpan.FromSeconds(5), default);
        await Task.Delay(50).ConfigureAwait(false);

        transport.FailReply("fail1", new InvalidOperationException("handler boom")).Should().BeTrue();

        Func<Task> act = async () => await rpc.ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*handler boom*").ConfigureAwait(false);
    }
}
