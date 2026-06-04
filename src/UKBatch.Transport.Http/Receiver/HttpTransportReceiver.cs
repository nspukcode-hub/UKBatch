using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Transport.Http.Receiver;

/// <summary>
/// Long-lived singleton receiver pump. Bridges three concerns:
/// <list type="number">
///   <item><see cref="Endpoints.PublishEndpointHandler"/> enqueues inbound <see cref="JobMessage"/>s
///   into per-topic channels.</item>
///   <item><see cref="Endpoints.PollEndpointHandler"/> drains queued messages on long-poll GET.</item>
///   <item>In-process consumers (when wired via <c>HttpTransport.SubscribeAsync</c>) read the same
///   channel infrastructure.</item>
/// </list>
/// </summary>
internal sealed class HttpTransportReceiver
{
    private readonly ConcurrentDictionary<string, HttpTransportReceiverTopic> _topics = new(StringComparer.Ordinal);
    private readonly ILogger<HttpTransportReceiver> _logger;
    private readonly IOptions<HttpTransportOptions> _options;
    private readonly TimeProvider _timeProvider;

    public HttpTransportReceiver(
        ILogger<HttpTransportReceiver> logger,
        IOptions<HttpTransportOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _logger = logger;
        _options = options;
        _timeProvider = timeProvider;
    }

    /// <summary>Non-blocking enqueue into the per-topic bounded channel.</summary>
    public void Enqueue(string topic, JobMessage message)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        ArgumentNullException.ThrowIfNull(message);
        var t = GetOrAddTopic(topic);
        if (!t.PublishToAll(message))
        {
            _logger.LogWarning(
                "HttpTransportReceiver: failed to enqueue MessageId {MessageId} for topic {Topic} (channel writer closed).",
                message.MessageId, topic);
        }
    }

    /// <summary>
    /// Long-poll drain — waits up to <see cref="HttpTransportOptions.LongPollMaxWait"/> for at least
    /// one message; returns an empty array on timeout.
    /// </summary>
    public Task<IReadOnlyList<JobMessage>> AwaitMessagesAsync(string topic, CancellationToken ct)
        => AwaitMessagesAsync(topic, clientWait: null, ct);

    /// <summary>
    /// Long-poll drain with caller-supplied wait override. Actual wait is
    /// <c>min(clientWait, LongPollMaxWait)</c>; honors client tuning while
    /// enforcing server policy as a hard cap.
    /// </summary>
    public Task<IReadOnlyList<JobMessage>> AwaitMessagesAsync(string topic, TimeSpan? clientWait, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        return GetOrAddTopic(topic).DrainLongPollAsync(clientWait, ct);
    }

    /// <summary>Pump-style consume for in-process consumers (<c>HttpTransport.SubscribeAsync</c>).</summary>
    public IAsyncEnumerable<JobMessage> ConsumeAsync(string topic, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(topic);
        return GetOrAddTopic(topic).ConsumeAsync(ct);
    }

    private HttpTransportReceiverTopic GetOrAddTopic(string topic)
        => _topics.GetOrAdd(topic, _ => new HttpTransportReceiverTopic(_options.Value, _timeProvider));
}
