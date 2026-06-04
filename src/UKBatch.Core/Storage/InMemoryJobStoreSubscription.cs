using System.Threading.Channels;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage;

/// <summary>
/// Per-call WatchAsync subscription. Allocates a private bounded <see cref="Channel{T}"/>
/// sized and configured per the caller's <see cref="WatchOptions"/>.
/// </summary>
/// <remarks>
/// Backpressure semantics: the publisher path is non-blocking
/// (<see cref="TryPublish"/> uses <c>Channel.Writer.TryWrite</c>), so events drop when the
/// per-subscriber buffer is full regardless of the caller's <see cref="WatchOverflowPolicy"/>
/// choice. All three policy values therefore degrade to a drop semantic in this in-memory
/// adapter; <c>Backpressure</c> is mapped to <c>BoundedChannelFullMode.DropNewest</c> to keep
/// the channel-internals consistent with the observable behaviour (<c>Wait</c> mode would be
/// inert here because <c>TryWrite</c> short-circuits it). Sized for SignalR push cadence
/// (~1024 events default). See <see cref="WatchOverflowPolicy.Backpressure"/> xmldoc for the
/// durable-adapter contract.
/// </remarks>
internal sealed class InMemoryJobStoreSubscription : IAsyncDisposable
{
    private readonly Channel<JobExecution> _channel;

    /// <summary>Constructs a subscription with a private buffer matching the requested overflow policy.</summary>
    public InMemoryJobStoreSubscription(WatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _channel = options.OverflowPolicy switch
        {
            WatchOverflowPolicy.Backpressure => Channel.CreateBounded<JobExecution>(new BoundedChannelOptions(options.BufferCapacity)
            {
                // DropNewest: the publisher uses TryWrite which short-circuits Wait mode, so
                // DropNewest keeps the channel internals consistent with the observable behaviour.
                // See WatchOverflowPolicy.Backpressure xmldoc.
                FullMode = BoundedChannelFullMode.DropNewest,
                SingleReader = true,
                SingleWriter = false,
            }),
            WatchOverflowPolicy.DropOldest => Channel.CreateBounded<JobExecution>(new BoundedChannelOptions(options.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            }),
            WatchOverflowPolicy.DropNewest => Channel.CreateBounded<JobExecution>(new BoundedChannelOptions(options.BufferCapacity)
            {
                FullMode = BoundedChannelFullMode.DropNewest,
                SingleReader = true,
                SingleWriter = false,
            }),
            _ => throw new ArgumentOutOfRangeException(nameof(options), options.OverflowPolicy, "Unknown WatchOverflowPolicy."),
        };
    }

    /// <summary>Non-blocking publish; returns <c>false</c> if the buffer is full (drop semantics apply).</summary>
    public bool TryPublish(JobExecution ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return _channel.Writer.TryWrite(ex);
    }

    /// <summary>Streams events to the caller; honours <paramref name="ct"/>.</summary>
    public IAsyncEnumerable<JobExecution> ReadAsync(CancellationToken ct) => _channel.Reader.ReadAllAsync(ct);

    /// <inheritdoc/>
    public ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
