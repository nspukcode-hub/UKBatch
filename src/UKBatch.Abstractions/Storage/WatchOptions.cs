namespace UKBatch.Abstractions.Storage;

/// <summary>
/// Configuration for <see cref="IJobExecutionReader.WatchAsync"/>. The defaults are tuned for
/// SignalR push consumers that must not drop events.
/// </summary>
public sealed record class WatchOptions
{
    private const int DefaultBufferCapacity = 1024;

    private readonly int _bufferCapacity = DefaultBufferCapacity;

    /// <summary>Default options (Backpressure with 1024-capacity buffer).</summary>
    public static WatchOptions Default { get; } = new();

    /// <summary>Overflow handling policy when the consumer cannot keep up.</summary>
    public WatchOverflowPolicy OverflowPolicy { get; init; } = WatchOverflowPolicy.Backpressure;

    /// <summary>
    /// Buffer capacity (number of pending events) before <see cref="OverflowPolicy"/> kicks in.
    /// Must be greater than zero; the contract is enforced by the property's <c>init</c> setter.
    /// </summary>
    public int BufferCapacity
    {
        get => _bufferCapacity;
        init
        {
            if (value <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "BufferCapacity must be > 0.");
            }
            _bufferCapacity = value;
        }
    }
}
