using System.Threading.Channels;

namespace UKBatch.Runtime;

/// <summary>
/// Concrete <see cref="IBatchCompletionEvents"/> implementation — owns a bounded channel that
/// the runtime writes to after each batch run finishes and the hub fan-out reads from.
/// </summary>
/// <remarks>
/// <para>Singleton lifetime. Writers: <see cref="JobRunner.TriggerBatchAsync"/> writes a
/// <see cref="BatchCompletionSignalPayload"/> after <see cref="BatchExecutor.RunAsync"/> returns
/// (in any outcome). Readers: <c>JobStatusHubFanout</c>'s batch-completion pump consumes the
/// channel.</para>
/// <para>Overflow posture: <see cref="BoundedChannelFullMode.DropOldest"/> with capacity 1024 —
/// consistent with the rest of the runtime's best-effort fan-out invariant
/// (<c>WatchOverflowPolicy.Backpressure</c> in v0.1).</para>
/// </remarks>
internal sealed class BatchCompletionSignal : IBatchCompletionEvents
{
    private readonly Channel<BatchCompletionSignalPayload> _channel = Channel.CreateBounded<BatchCompletionSignalPayload>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    /// <inheritdoc/>
    public ChannelReader<BatchCompletionSignalPayload> CompletedBatchRunIds => _channel.Reader;

    /// <summary>
    /// Records that the batch carried by <paramref name="payload"/> has finished. Idempotent at the
    /// channel level — the hub fan-out's dedupe set handles late re-writes. Drops silently if the
    /// channel is full.
    /// </summary>
    public void Signal(BatchCompletionSignalPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        _channel.Writer.TryWrite(payload);
    }
}
