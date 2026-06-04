using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Runtime;

/// <summary>
/// <see cref="IJobProgress"/> backed by <see cref="Interlocked"/> counters and a Volatile-pair
/// guarded <c>Total</c>. Every counter mutation posts a snapshot to the
/// <see cref="DebouncedProgressFlusher"/> per-execution channel.
/// </summary>
/// <remarks>
/// Terminal progress writes are NOT routed through the flusher — <c>JobWorker</c> awaits
/// <c>IJobExecutionWriter.UpdateProgressAsync(..., CancellationToken.None)</c> directly in the
/// terminal status code paths.
/// </remarks>
internal sealed class CountingJobProgress : IJobProgress
{
    private long _processed;
    private long _failed;
    private long _totalValue;
    private int _totalSetFlag;
    private readonly string _executionId;
    private readonly DebouncedProgressFlusher _flusher;

    /// <summary>Constructs a progress tracker bound to a single execution.</summary>
    public CountingJobProgress(string executionId, DebouncedProgressFlusher flusher)
    {
        ArgumentException.ThrowIfNullOrEmpty(executionId);
        ArgumentNullException.ThrowIfNull(flusher);
        _executionId = executionId;
        _flusher = flusher;
    }

    /// <summary><c>Total</c> reads via a Volatile pair so weak-memory architectures observe the value
    /// in happens-before order with the set-flag.</summary>
    public long? Total
        => Volatile.Read(ref _totalSetFlag) == 1
            ? Volatile.Read(ref _totalValue)
            : (long?)null;

    /// <inheritdoc/>
    public long Processed => Interlocked.Read(ref _processed);

    /// <inheritdoc/>
    public long Failed => Interlocked.Read(ref _failed);

    /// <inheritdoc/>
    public void SetTotal(long total)
    {
        // Write the value first, then publish the flag via Volatile.Write.
        if (Interlocked.CompareExchange(ref _totalSetFlag, -1, 0) == 0)
        {
            Volatile.Write(ref _totalValue, total);
            Volatile.Write(ref _totalSetFlag, 1);
        }
        Beat();
    }

    /// <inheritdoc/>
    public void Increment()
    {
        Interlocked.Increment(ref _processed);
        Beat();
    }

    /// <inheritdoc/>
    public void Increment(long count)
    {
        Interlocked.Add(ref _processed, count);
        Beat();
    }

    /// <inheritdoc/>
    public void ReportFailure()
    {
        Interlocked.Increment(ref _failed);
        Beat();
    }

    /// <inheritdoc/>
    public void ReportFailure(long count)
    {
        Interlocked.Add(ref _failed, count);
        Beat();
    }

    /// <inheritdoc/>
    public void ReportStatus(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        // Reserved SignalR push hook — currently a no-op.
    }

    private void Beat()
    {
        var beat = new ProgressBeat
        {
            ExecutionId = _executionId,
            Processed = Interlocked.Read(ref _processed),
            Failed = Interlocked.Read(ref _failed),
            Total = Total,
            IsTerminal = false,
        };
        _flusher.PostBeat(beat);
    }
}
