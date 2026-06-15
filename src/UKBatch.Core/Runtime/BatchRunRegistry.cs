using System.Collections.Concurrent;
using UKBatch.Abstractions.Runtime;

namespace UKBatch.Runtime;

/// <summary>
/// Tracks the per-run <see cref="CancellationTokenSource"/> of every in-flight batch run so an
/// administrative cancel can trip it. Backs the public <see cref="IBatchRunCanceller"/> so the API
/// surface depends on an Abstractions interface, not on the runtime's internal runner.
/// </summary>
/// <remarks>
/// <para><b>CTS ownership.</b> The registry does NOT create or dispose the cancellation sources — it
/// holds references the caller (<c>JobRunner.TriggerBatchAsync</c>) registers and de-registers. The
/// runner's <c>finally</c> removes the entry and disposes the CTS exactly once. The registry only ever
/// calls <see cref="CancellationTokenSource.Cancel()"/>, never <see cref="IDisposable.Dispose"/>.</para>
/// <para><b>Cancel-vs-dispose race.</b> A cancel can land the instant the run's <c>finally</c> is
/// disposing the source. <see cref="Cancel"/> swallows the resulting
/// <see cref="ObjectDisposedException"/> — the run is finishing anyway, so a late cancel is a harmless
/// no-op.</para>
/// </remarks>
internal sealed class BatchRunRegistry : IBatchRunCanceller
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _live = new(StringComparer.Ordinal);

    /// <summary>Registers the run's linked source. Called by the runner immediately after creating the run row.</summary>
    public void Register(string batchId, CancellationTokenSource cts)
    {
        ArgumentException.ThrowIfNullOrEmpty(batchId);
        ArgumentNullException.ThrowIfNull(cts);
        _live[batchId] = cts;
    }

    /// <summary>De-registers the run (the runner's <c>finally</c> calls this BEFORE disposing the source).</summary>
    public void Remove(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
        {
            return;
        }
        _live.TryRemove(batchId, out _);
    }

    /// <inheritdoc/>
    public bool Cancel(string batchId)
    {
        if (string.IsNullOrEmpty(batchId))
        {
            return false;
        }
        if (!_live.TryGetValue(batchId, out var cts))
        {
            return false;
        }
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The run's finally disposed the source between the lookup and the cancel — it is already
            // finishing, so this cancel has nothing to do. Treat as a benign no-op.
            return false;
        }
    }
}
