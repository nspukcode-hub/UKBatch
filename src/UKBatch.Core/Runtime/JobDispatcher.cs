using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace UKBatch.Runtime;

/// <summary>
/// Bounded <see cref="Channel{T}"/> of <see cref="JobExecutionRequest"/>. Backpressure on
/// triggers when full (<see cref="BoundedChannelFullMode.Wait"/>); emits a warning + counter
/// increment whenever <c>EnqueueAsync</c> blocks (an observability seam). The REST layer surfaces
/// the backpressure state as 503 Retry-After.
/// </summary>
internal sealed class JobDispatcher
{
    private readonly Channel<JobExecutionRequest> _channel;
    private readonly int _capacity;
    private readonly ILogger<JobDispatcher> _logger;
    private int _acceptingTriggers = 1;
    private long _backpressureWaiterCount;

    /// <summary>Constructs the dispatcher with capacity computed from options.</summary>
    public JobDispatcher(IOptions<UKBatchOptions> options, ILogger<JobDispatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _capacity = options.Value.DispatcherChannelCapacity > 0
            ? options.Value.DispatcherChannelCapacity
            : options.Value.MaxDegreeOfParallelism * 32;
        _channel = Channel.CreateBounded<JobExecutionRequest>(new BoundedChannelOptions(_capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
        });
        _logger = logger;
    }

    /// <summary>Channel reader exposed to worker tasks.</summary>
    public ChannelReader<JobExecutionRequest> Reader => _channel.Reader;

    /// <summary>Number of writers currently awaiting a free slot.</summary>
    public long BackpressureWaiterCount => Interlocked.Read(ref _backpressureWaiterCount);

    /// <summary>Capacity configured for the channel.</summary>
    public int Capacity => _capacity;

    /// <summary>
    /// Enqueues a request. Fast-path is non-blocking <see cref="ChannelWriter{T}.TryWrite"/>;
    /// slow-path emits a backpressure warning and awaits a free slot.
    /// </summary>
    /// <exception cref="InvalidOperationException">After <see cref="StopAcceptingTriggers"/> has been called.</exception>
    public async Task EnqueueAsync(JobExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Volatile.Read(ref _acceptingTriggers) == 0)
        {
            throw new InvalidOperationException("UKBatch is shutting down; no new triggers accepted.");
        }
        if (_channel.Writer.TryWrite(request))
        {
            return;
        }
        var waiters = Interlocked.Increment(ref _backpressureWaiterCount);
        _logger.LogWarning(
            "Dispatcher backpressure: {Waiters} writer(s) awaiting slot; queue capacity={Capacity}.",
            waiters,
            _capacity);
        try
        {
            await _channel.Writer.WriteAsync(request, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Decrement(ref _backpressureWaiterCount);
        }
    }

    /// <summary>Refuses further triggers (idempotent).</summary>
    public void StopAcceptingTriggers() => Interlocked.Exchange(ref _acceptingTriggers, 0);

    /// <summary>Closes the channel for writers AFTER all in-flight workers have drained.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
