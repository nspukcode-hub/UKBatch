namespace UKBatch.Runtime;

/// <summary>
/// Concrete <see cref="IDispatcherProbe"/> backed by <see cref="JobDispatcher"/>. Registered as
/// a DI singleton by <c>UKBatchBuilder.Complete()</c>. Reads the dispatcher's
/// <c>BackpressureWaiterCount</c> and <c>Capacity</c> directly — both already publicly visible
/// on the friend-internal <see cref="JobDispatcher"/> type.
/// </summary>
internal sealed class JobDispatcherProbe : IDispatcherProbe
{
    private readonly JobDispatcher _dispatcher;

    /// <summary>Constructs the probe with the dispatcher backing it.</summary>
    public JobDispatcherProbe(JobDispatcher dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);
        _dispatcher = dispatcher;
    }

    /// <inheritdoc/>
    public long BackpressureWaiterCount => _dispatcher.BackpressureWaiterCount;

    /// <inheritdoc/>
    public int DispatcherChannelCapacity => _dispatcher.Capacity;
}
