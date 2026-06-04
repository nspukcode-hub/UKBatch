using System.Threading.Channels;
using UKBatch.Abstractions.Transport;

namespace UKBatch.Transport.Http.Receiver;

/// <summary>
/// Per-topic message buffer for the receiver pump. Bounded channel of 1024 with
/// <see cref="BoundedChannelFullMode.DropOldest"/> — a worker that doesn't poll for
/// <see cref="HttpTransportOptions.LongPollMaxWait"/> × ~60 accumulates messages; oldest dropped to
/// prevent unbounded memory.
/// </summary>
/// <remarks>
/// <para><b>Backpressure:</b> production deployments wanting durability use the
/// RabbitMQ transport instead. Drops emit <c>_logger.LogWarning</c> via the parent receiver.</para>
/// </remarks>
internal sealed class HttpTransportReceiverTopic
{
    private readonly Channel<JobMessage> _buffer;
    private readonly HttpTransportOptions _opts;
    private readonly TimeProvider _timeProvider;

    public HttpTransportReceiverTopic(HttpTransportOptions opts, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(opts);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _opts = opts;
        _timeProvider = timeProvider;
        _buffer = Channel.CreateBounded<JobMessage>(new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            SingleWriter = false,
        });
    }

    /// <summary>Non-blocking publish. Returns true on accepted, false if the writer is closed.</summary>
    public bool PublishToAll(JobMessage msg) => _buffer.Writer.TryWrite(msg);

    /// <summary>
    /// Long-poll: returns up to N messages buffered for this topic, blocking up to
    /// <see cref="HttpTransportOptions.LongPollMaxWait"/> for the first message. Once at least one is
    /// available, returns the entire batch present in the buffer.
    /// </summary>
    public Task<IReadOnlyList<JobMessage>> DrainLongPollAsync(CancellationToken ct)
        => DrainLongPollAsync(clientWait: null, ct);

    /// <summary>
    /// Long-poll with caller-supplied wait override. Actual wait is
    /// <c>min(clientWait ?? LongPollMaxWait, LongPollMaxWait)</c>. Clients can tune for low-latency
    /// without breaking server policy (operator's LongPollMaxWait is the hard cap).
    /// </summary>
    public async Task<IReadOnlyList<JobMessage>> DrainLongPollAsync(TimeSpan? clientWait, CancellationToken ct)
    {
        var serverCap = _opts.LongPollMaxWait;
        var actualWait = clientWait.HasValue && clientWait.Value < serverCap
            ? clientWait.Value
            : serverCap;

        var batch = new List<JobMessage>(capacity: 16);
        using var timeoutCts = new CancellationTokenSource(actualWait, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        try
        {
            if (!await _buffer.Reader.WaitToReadAsync(linked.Token).ConfigureAwait(false))
            {
                return Array.Empty<JobMessage>();
            }
            while (_buffer.Reader.TryRead(out var msg))
            {
                batch.Add(msg);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            // Long-poll timeout — return empty array (200 + { messages: [] }).
            return Array.Empty<JobMessage>();
        }
        return batch;
    }

    /// <summary>
    /// Pump-style consume — yields messages as they arrive. Used by in-process consumers
    /// (e.g. when the host that mounts the receiver also wants to run jobs locally).
    /// </summary>
    public IAsyncEnumerable<JobMessage> ConsumeAsync(CancellationToken ct)
        => _buffer.Reader.ReadAllAsync(ct);
}
