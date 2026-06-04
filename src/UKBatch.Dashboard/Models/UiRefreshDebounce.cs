namespace UKBatch.Dashboard.Models;

/// <summary>
/// Coalesces a burst of hub events into one render call per <c>window</c> (default 100 ms via
/// <c>DashboardOptions.UiRefreshDebounce</c>). For live pages that fan-in many
/// <c>ExecutionStateChanged</c> events per real change under the up-to-4× hub fan-out.
/// </summary>
/// <remarks>
/// <para><b>Render callback contract:</b> <c>render</c> MUST hop to the render dispatcher
/// (<c>() =&gt; InvokeAsync(StateHasChanged)</c>) — the debounce fires on a thread-pool
/// continuation, off the render thread, so a bare <c>StateHasChanged()</c> would throw.</para>
/// <para><b>Disposal-race hardening:</b>
/// (a) the per-request <see cref="CancellationTokenSource"/> is captured into a LOCAL inside the
/// lock so a concurrent <see cref="RequestAsync"/> cannot null the field out from under the
/// continuation; (b) the continuation catches <see cref="ObjectDisposedException"/> as well as
/// <see cref="OperationCanceledException"/> because <c>Task.Delay</c> on a disposed CTS throws the
/// former during circuit teardown; (c) a <see cref="Volatile"/> <c>_disposed</c> guard prevents a
/// late hub event from firing <c>StateHasChanged</c> on an already-disposed component.</para>
/// </remarks>
internal sealed class UiRefreshDebounce : IAsyncDisposable
{
    private readonly Func<Task> _render;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private int _disposed; // 0 = live, 1 = disposed (Volatile)

    /// <summary>Creates the debounce around <paramref name="render"/> with the coalescing window.</summary>
    public UiRefreshDebounce(Func<Task> render, TimeSpan window)
    {
        ArgumentNullException.ThrowIfNull(render);
        _render = render;
        _window = window;
    }

    /// <summary>Requests a render; coalesces with any in-flight request inside the window.</summary>
    public Task RequestAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask; // disposed guard
        CancellationTokenSource cts;
        lock (_lock)
        {
            if (Volatile.Read(ref _disposed) != 0) return Task.CompletedTask;
            _cts?.Cancel();
            cts = _cts = new CancellationTokenSource(); // capture local inside lock
        }
        var ct = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(_window, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested && Volatile.Read(ref _disposed) == 0)
                    await _render().ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* coalesced — a newer request superseded this one */ }
            catch (ObjectDisposedException) { /* CTS/circuit gone during teardown */ }
        }, ct);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        lock (_lock) { _cts?.Cancel(); _cts?.Dispose(); _cts = null; }
        return ValueTask.CompletedTask;
    }
}
