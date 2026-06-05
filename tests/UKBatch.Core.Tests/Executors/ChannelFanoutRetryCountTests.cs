using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Polly;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Discovery;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// Locks the item-level <see cref="ItemErrorPolicy.RetryThenContinue"/> execution count to exactly
/// <c>1 + MaxRetries</c> (one direct attempt plus the cached pipeline's retries). A failing item with
/// <c>MaxRetries=0</c> must run exactly once, not twice.
/// </summary>
public class ChannelFanoutRetryCountTests
{
    /// <summary>Minimal thread-safe progress double exposing the success / failure tallies.</summary>
    private sealed class CountingProgress : IJobProgress
    {
        private long _processed;
        private long _failed;
        public long? Total => null;
        public long Processed => Interlocked.Read(ref _processed);
        public long Failed => Interlocked.Read(ref _failed);
        public void SetTotal(long total) { }
        public void Increment() => Interlocked.Increment(ref _processed);
        public void Increment(long count) => Interlocked.Add(ref _processed, count);
        public void ReportFailure() => Interlocked.Increment(ref _failed);
        public void ReportFailure(long count) => Interlocked.Add(ref _failed, count);
        public void ReportStatus(string message) { }
    }

    private static JobDefinition DefWithRetries(int maxRetries) => new()
    {
        Name = "fanout.retry.count",
        ImplementationTypeName = typeof(object).AssemblyQualifiedName,
        IsPartitioned = true,
        Schedule = null,
        MaxRetries = maxRetries,
        TimeoutSeconds = 0,
        PartitionWorkerCount = 1,
        ItemErrorPolicy = ItemErrorPolicy.RetryThenContinue,
        DefaultParameters = new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
        SourceService = null,
    };

    private static async IAsyncEnumerable<int> SingleItem()
    {
        yield return 0;
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private static async Task RunOneItemAsync(
        Func<int, CancellationToken, Task> body, JobDefinition def, IJobProgress progress)
    {
        // Mirror the registration call sites: MaxRetries==0 stores a null pipeline (one direct attempt
        // only), MaxRetries>=1 builds the cached pipeline that supplies the additional attempts.
        var pipeline = def.MaxRetries >= 1 ? JobDefinitionFactory.BuildItemRetryPipeline(def) : null;
        await ChannelFanout.RunAsync<int>(
            SingleItem(),
            workerCount: 1,
            body,
            ItemErrorPolicy.RetryThenContinue,
            channelCapacity: 1,
            progress,
            pipeline,
            NullLogger.Instance,
            CancellationToken.None).ConfigureAwait(false);
    }

    [Fact]
    public async Task RetryThenContinue_MaxRetriesZero_ExecutesExactlyOnce_ThenReportsFailure()
    {
        var counter = 0;
        var progress = new CountingProgress();
        await RunOneItemAsync(
            (_, _) => { Interlocked.Increment(ref counter); throw new InvalidOperationException("boom"); },
            DefWithRetries(0),
            progress).ConfigureAwait(false);

        counter.Should().Be(1, "MaxRetries=0 means a single attempt, no retry pipeline.");
        progress.Failed.Should().Be(1);
        progress.Processed.Should().Be(0);
    }

    [Fact]
    public async Task RetryThenContinue_MaxRetriesThree_ExecutesExactlyFourTimes()
    {
        var counter = 0;
        var progress = new CountingProgress();
        await RunOneItemAsync(
            (_, _) => { Interlocked.Increment(ref counter); throw new InvalidOperationException("boom"); },
            DefWithRetries(3),
            progress).ConfigureAwait(false);

        counter.Should().Be(4, "1 direct attempt + 3 retries = 4 total.");
        progress.Failed.Should().Be(1);
    }

    [Fact]
    public async Task RetryThenContinue_SucceedsOnSecondAttempt_ExecutesTwice_ReportsSuccess()
    {
        var counter = 0;
        var progress = new CountingProgress();
        await RunOneItemAsync(
            (_, _) =>
            {
                var n = Interlocked.Increment(ref counter);
                if (n == 1)
                {
                    throw new InvalidOperationException("first attempt fails");
                }
                return Task.CompletedTask;
            },
            DefWithRetries(3),
            progress).ConfigureAwait(false);

        counter.Should().Be(2, "throws once then succeeds inside the pipeline.");
        progress.Processed.Should().Be(1);
        progress.Failed.Should().Be(0);
    }

    [Fact]
    public async Task RetryThenContinue_SucceedsFirstAttempt_NoPipelineOverhead()
    {
        var counter = 0;
        var progress = new CountingProgress();
        await RunOneItemAsync(
            (_, _) => { Interlocked.Increment(ref counter); return Task.CompletedTask; },
            DefWithRetries(3),
            progress).ConfigureAwait(false);

        counter.Should().Be(1, "the direct call succeeded so the pipeline is never touched.");
        progress.Processed.Should().Be(1);
        progress.Failed.Should().Be(0);
    }
}
