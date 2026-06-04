using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using UKBatch;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Progress;

/// <summary>
/// S9 invariant: Volatile pair semantics for Total. Interlocked counters for Processed/Failed.
/// </summary>
public class CountingJobProgressTests
{
    private static DebouncedProgressFlusher NewFlusher()
    {
        var writer = Substitute.For<IJobExecutionWriter>();
        return new DebouncedProgressFlusher(writer, Options.Create(new UKBatchOptions()), NullLogger<DebouncedProgressFlusher>.Instance);
    }

    [Fact]
    public void Total_UnsetByDefault_ReturnsNull()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        p.Total.Should().BeNull();
    }

    [Fact]
    public void SetTotal_FirstCallSetsValue()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        p.SetTotal(100);

        p.Total.Should().Be(100);
    }

    [Fact]
    public void SetTotal_SecondCall_IsIgnored()
    {
        // S9 invariant + IJobProgress xmldoc: SetTotal is idempotent — only first call wins.
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        p.SetTotal(100);
        p.SetTotal(200);

        p.Total.Should().Be(100);
    }

    [Fact]
    public void Increment_BumpsProcessedAtomically()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        for (var i = 0; i < 10; i++) { p.Increment(); }

        p.Processed.Should().Be(10);
    }

    [Fact]
    public void Increment_WithCount_AddsAtomically()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        p.Increment(5);
        p.Increment(7);

        p.Processed.Should().Be(12);
    }

    [Fact]
    public void ReportFailure_BumpsFailedAtomically()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        p.ReportFailure();
        p.ReportFailure();

        p.Failed.Should().Be(2);
    }

    [Fact]
    public void ReportFailure_WithCount_AddsAtomically()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);

        p.ReportFailure(3);

        p.Failed.Should().Be(3);
    }

    [Fact]
    public async Task Increment_Concurrent_ConsistentFinalCount()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        const int N = 1000;

        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(() => p.Increment()))).ConfigureAwait(false);

        p.Processed.Should().Be(N);
    }

    [Fact]
    public async Task ReportFailure_Concurrent_ConsistentFinalCount()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        const int N = 1000;

        await Task.WhenAll(Enumerable.Range(0, N).Select(_ => Task.Run(() => p.ReportFailure()))).ConfigureAwait(false);

        p.Failed.Should().Be(N);
    }

    [Fact]
    public async Task SetTotal_ConcurrentCalls_OnlyFirstWins()
    {
        // S9 — only the FIRST SetTotal call is observable.
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        const int N = 100;

        await Task.WhenAll(Enumerable.Range(1, N).Select(i => Task.Run(() => p.SetTotal(i)))).ConfigureAwait(false);

        // The first SetTotal "wins" — Total can be any of the concurrently posted values, but
        // CAS guarantees exactly one publication. So Total must be in [1, N].
        p.Total.Should().NotBeNull();
        p.Total!.Value.Should().BeInRange(1, N);

        // Now further SetTotal calls must be ignored.
        var snapshot = p.Total;
        p.SetTotal(99_999);
        p.Total.Should().Be(snapshot);
    }

    [Fact]
    public void ReportStatus_DoesNotThrow()
    {
        // SignalR hook — no-op in; should not throw.
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        Action act = () => p.ReportStatus("hello");
        act.Should().NotThrow();
    }

    [Fact]
    public void ReportStatus_Null_ThrowsArgumentNull()
    {
        var flusher = NewFlusher();
        var p = new CountingJobProgress("ex1", flusher);
        Action act = () => p.ReportStatus(null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
