using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Transport;

/// <summary>
/// In-process pub/sub transport.
/// <list type="bullet">
///   <item>Per-subscriber channel — every subscriber sees every published message (NOT competing-consumer).</item>
///   <item>RequestReply via internal <see cref="CompleteReply"/> + <see cref="FailReply"/> — no JSON, no topic noise.</item>
/// </list>
/// </summary>
public sealed class InProcessTransport : ITransport
{
    private readonly ConcurrentDictionary<string, InProcessTransportTopic> _topics = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JobResult>> _pendingReplies = new(StringComparer.Ordinal);
    private readonly ILogger<InProcessTransport> _logger;

    /// <summary>Constructs the transport.</summary>
    public InProcessTransport(ILogger<InProcessTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public string Name => "InProcess";

    /// <inheritdoc/>
    public Task PublishAsync(JobMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var topic = _topics.GetOrAdd(message.JobName, static _ => new InProcessTransportTopic());
        topic.PublishToAll(message);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<JobMessage> SubscribeAsync(string topic, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        var t = _topics.GetOrAdd(topic, static _ => new InProcessTransportTopic());
        var subId = Guid.NewGuid();
        var ch = t.AddSubscriber(subId);
        try
        {
            await foreach (var msg in ch.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return msg;
            }
        }
        finally
        {
            t.RemoveSubscriber(subId);
        }
    }

    /// <inheritdoc/>
    public async Task<JobResult> RequestReplyAsync(string targetService, JobMessage message, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetService);
        ArgumentNullException.ThrowIfNull(message);

        var tcs = new TaskCompletionSource<JobResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingReplies.TryAdd(message.MessageId, tcs))
        {
            throw new InvalidOperationException($"Duplicate MessageId {message.MessageId} for RequestReplyAsync.");
        }

        try
        {
            await PublishAsync(message, cancellationToken).ConfigureAwait(false);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(timeout, linkedCts.Token);
            var winner = await Task.WhenAny(tcs.Task, delayTask).ConfigureAwait(false);
            if (winner == delayTask)
            {
                // Drain ct.IsCancellationRequested -> OCE; otherwise throw TimeoutException.
                cancellationToken.ThrowIfCancellationRequested();
                throw new TimeoutException($"RequestReply for MessageId {message.MessageId} did not arrive within {timeout}.");
            }
            linkedCts.Cancel();
            try
            {
                await delayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // expected — we cancelled the delay
            }
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pendingReplies.TryRemove(message.MessageId, out _);
        }
    }

    /// <summary>
    /// Internal-only API used by in-process handlers to complete a pending RequestReply.
    /// </summary>
    /// <remarks>
    /// <para>A return value of <c>false</c> is benign in two cases:
    /// (a) the correlation id was never registered — handler bug; (b) the request already timed
    /// out or was cancelled. We log a warning so case (a) is observable.</para>
    /// </remarks>
    internal bool CompleteReply(string correlationId, JobResult result)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        ArgumentNullException.ThrowIfNull(result);
        if (_pendingReplies.TryGetValue(correlationId, out var tcs))
        {
            return tcs.TrySetResult(result);
        }
        _logger.LogWarning(
            "InProcessTransport.CompleteReply: no pending request for correlationId={CorrelationId} (already timed-out, cancelled, or handler bug).",
            correlationId);
        return false;
    }

    /// <summary>Internal-only API to surface a reply-side exception (handler threw).</summary>
    internal bool FailReply(string correlationId, Exception exception)
    {
        ArgumentException.ThrowIfNullOrEmpty(correlationId);
        ArgumentNullException.ThrowIfNull(exception);
        if (_pendingReplies.TryGetValue(correlationId, out var tcs))
        {
            return tcs.TrySetException(exception);
        }
        _logger.LogWarning(
            "InProcessTransport.FailReply: no pending request for correlationId={CorrelationId} (already timed-out, cancelled, or handler bug).",
            correlationId);
        return false;
    }
}
