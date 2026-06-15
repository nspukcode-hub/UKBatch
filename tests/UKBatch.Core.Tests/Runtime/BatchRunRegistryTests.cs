using FluentAssertions;
using UKBatch.Abstractions.Runtime;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <see cref="BatchRunRegistry"/> tracks the per-run cancellation source so an administrative
/// <see cref="IBatchRunCanceller.Cancel"/> can trip it. It never owns disposal (the runner's finally
/// disposes the source), so the registry's one real hazard is a cancel landing the instant the source is
/// disposed — which it must swallow. These pin: Cancel trips the token, Cancel of an unknown / removed id
/// returns false, a post-dispose cancel is a benign no-op (no <see cref="ObjectDisposedException"/>), and a
/// concurrent cancel/dispose race throws nothing.
/// </summary>
public class BatchRunRegistryTests
{
    [Fact]
    public void Cancel_RegisteredRun_TripsTheToken_AndReturnsTrue()
    {
        var registry = new BatchRunRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register("r1", cts);

        var result = registry.Cancel("r1");

        result.Should().BeTrue("a live run is found and signalled");
        cts.Token.IsCancellationRequested.Should().BeTrue("Cancel trips the run's cancellation source");
    }

    [Fact]
    public void Cancel_UnknownId_ReturnsFalse()
    {
        var registry = new BatchRunRegistry();
        registry.Cancel("never-registered").Should().BeFalse();
    }

    [Fact]
    public void Cancel_AfterRemove_ReturnsFalse()
    {
        var registry = new BatchRunRegistry();
        using var cts = new CancellationTokenSource();
        registry.Register("r1", cts);
        registry.Remove("r1");

        registry.Cancel("r1").Should().BeFalse("a removed run is no longer live");
        cts.Token.IsCancellationRequested.Should().BeFalse("removing must not trip the token");
    }

    [Fact]
    public void Cancel_AfterSourceDisposed_ReturnsFalse_DoesNotThrow()
    {
        // The run's finally disposes the source; if a cancel arrives after disposal but before Remove,
        // Cancel must swallow the ObjectDisposedException and report a benign no-op.
        var registry = new BatchRunRegistry();
        var cts = new CancellationTokenSource();
        registry.Register("r1", cts);
        cts.Dispose();

        var act = () => registry.Cancel("r1");

        act.Should().NotThrow<ObjectDisposedException>();
        act().Should().BeFalse("a cancel against a disposed source is a no-op");
    }

    [Fact]
    public async Task Cancel_ConcurrentWithDisposeAndRemove_ThrowsNothing()
    {
        // Spin the cancel-vs-dispose race many times: one task cancels while another removes-and-disposes
        // the same id, mirroring the runner's finally. No ObjectDisposedException must surface.
        var registry = new BatchRunRegistry();

        for (var i = 0; i < 500; i++)
        {
            var id = $"r{i}";
            var cts = new CancellationTokenSource();
            registry.Register(id, cts);

            var cancelTask = Task.Run(() => registry.Cancel(id));
            var teardownTask = Task.Run(() =>
            {
                registry.Remove(id);
                cts.Dispose();
            });

            var act = async () => await Task.WhenAll(cancelTask, teardownTask);
            await act.Should().NotThrowAsync("the cancel/dispose race must never surface an ObjectDisposedException");
        }
    }
}
