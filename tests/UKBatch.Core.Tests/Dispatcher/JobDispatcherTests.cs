using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using UKBatch;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Dispatcher;

/// <summary>
/// Verifies dispatcher channel behaviour: capacity, backpressure observability, StopAccepting behaviour.
/// </summary>
public class JobDispatcherTests
{
    private static JobExecutionRequest NewRequest() => new()
    {
        ExecutionId = "ex-" + Guid.NewGuid().ToString("N"),
        Definition = new JobDefinition
        {
            Name = "j",
            IsPartitioned = false,
            MaxRetries = 0,
            TimeoutSeconds = 0,
            DefaultParameters = new Dictionary<string, object?>(),
            Tags = Array.Empty<string>(),
        },
        Parameters = JobParameters.Empty,
        AttemptNumber = 1,
        TriggeredBy = "test",
        BatchId = null,
        BatchStepId = null,
        EnqueuedAtUtc = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Capacity_ZeroDefaultsToMaxDoPTimes32()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 4, DispatcherChannelCapacity = 0 }),
            NullLogger<JobDispatcher>.Instance);
        d.Capacity.Should().Be(128); // 4 * 32
    }

    [Fact]
    public void Capacity_ExplicitValueUsed()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 4, DispatcherChannelCapacity = 256 }),
            NullLogger<JobDispatcher>.Instance);
        d.Capacity.Should().Be(256);
    }

    [Fact]
    public async Task EnqueueAsync_AfterStopAcceptingTriggers_Throws()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 1, DispatcherChannelCapacity = 8 }),
            NullLogger<JobDispatcher>.Instance);
        d.StopAcceptingTriggers();

        Func<Task> act = async () => await d.EnqueueAsync(NewRequest(), default).ConfigureAwait(false);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*shutting down*").ConfigureAwait(false);
    }

    [Fact]
    public async Task EnqueueAsync_FastPath_DoesNotIncrementBackpressureCounter()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 1, DispatcherChannelCapacity = 16 }),
            NullLogger<JobDispatcher>.Instance);

        await d.EnqueueAsync(NewRequest(), default).ConfigureAwait(false);
        d.BackpressureWaiterCount.Should().Be(0);
    }

    [Fact]
    public async Task EnqueueAsync_FullChannel_ApplyBackpressureViaWait()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 1, DispatcherChannelCapacity = 2 }),
            NullLogger<JobDispatcher>.Instance);

        // Fill capacity.
        await d.EnqueueAsync(NewRequest(), default).ConfigureAwait(false);
        await d.EnqueueAsync(NewRequest(), default).ConfigureAwait(false);

        // Next enqueue should block until a reader pulls one out.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var slowWriter = Task.Run(async () => await d.EnqueueAsync(NewRequest(), cts.Token).ConfigureAwait(false));

        // Wait a bit, expect backpressure counter to be 1.
        await Task.Delay(200).ConfigureAwait(false);
        d.BackpressureWaiterCount.Should().BeGreaterOrEqualTo(1);

        // Drain to release.
        d.Reader.TryRead(out _).Should().BeTrue();
        await slowWriter.ConfigureAwait(false);
        d.BackpressureWaiterCount.Should().Be(0);
    }

    [Fact]
    public void Complete_ClosesChannel()
    {
        var d = new JobDispatcher(
            Options.Create(new UKBatchOptions { MaxDegreeOfParallelism = 1, DispatcherChannelCapacity = 8 }),
            NullLogger<JobDispatcher>.Instance);
        d.Complete();
        d.Reader.Completion.IsCompleted.Should().BeTrue();
    }
}
