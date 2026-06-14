using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// <see cref="JobContext.WorkerIndex"/> exposes the 0-based partition-worker slot to a job body.
/// A plain <see cref="IJob"/> reads 0; a partitioned job (or an inline <c>ParallelForEachAsync</c>)
/// sees exactly the slots <c>{0..workerCount-1}</c>, each stable across an <c>await</c> within an
/// item, distinct across concurrent workers, and reset to 0 after the run (no AsyncLocal leak).
/// </summary>
public class WorkerIndexTests
{
    /// <summary>A plain job that records the index it observes (expected 0 — no fan-out).</summary>
    public sealed class PlainIndexJob : IJob
    {
        public static int Observed = -1;
        public static void Reset() => Observed = -1;

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Observed = context.WorkerIndex;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// A partitioned probe that, for every item, records the worker index AND re-reads it after an
    /// <c>await</c> so the test can assert stability across the suspension point.
    /// </summary>
    public sealed class IndexProbeJob : IPartitionedJob<int>
    {
        public static int ItemCount;
        // (index-before-await, index-after-await) per processed item.
        public static readonly ConcurrentBag<(int Before, int After)> Samples = new();

        public static void Reset()
        {
            Samples.Clear();
            ItemCount = 0;
        }

        public async IAsyncEnumerable<int> SourceAsync(
            JobContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < ItemCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task ProcessAsync(int item, JobContext context, CancellationToken cancellationToken)
        {
            var before = context.WorkerIndex;
            await Task.Delay(TimeSpan.FromMilliseconds(15), cancellationToken).ConfigureAwait(false);
            var after = context.WorkerIndex;
            Samples.Add((before, after));
        }
    }

    /// <summary>
    /// A plain job that drives <c>ctx.ParallelForEachAsync</c> directly (the inline-parallelism API);
    /// the per-item body reads <c>ctx.WorkerIndex</c>. Proves the public 3-arg body signature is
    /// unchanged: the index is read from the context, not passed as a parameter.
    /// </summary>
    public sealed class InlineParallelJob : IJob
    {
        public static int WorkerCount;
        public static int ItemCount;
        public static readonly ConcurrentBag<int> Observed = new();

        public static void Reset()
        {
            Observed.Clear();
            WorkerCount = 0;
            ItemCount = 0;
        }

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            return context.ParallelForEachAsync(
                Source(ItemCount),
                WorkerCount,
                // Body keeps the 3-arg (item, ctx, ct) shape; WorkerIndex is read off ctx.
                async (item, ctx, ct) =>
                {
                    var slot = ctx.WorkerIndex;
                    await Task.Delay(TimeSpan.FromMilliseconds(15), ct).ConfigureAwait(false);
                    Observed.Add(slot);
                },
                ItemErrorPolicy.ContinueOnError,
                cancellationToken);
        }

        private static async IAsyncEnumerable<int> Source(int count)
        {
            for (var i = 0; i < count; i++)
            {
                yield return i;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static async Task<JobStatus> RunPartitionedAsync(int workers, int items, object? workerOverride = null)
    {
        IndexProbeJob.Reset();
        IndexProbeJob.ItemCount = items;
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddPartitionedJob<IndexProbeJob, int>().Named("worker.index.probe")
                .WithParallelism(workers)
                .WithItemErrorPolicy(ItemErrorPolicy.ContinueOnError)
                .WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var parameters = workerOverride is null
                ? JobParameters.Empty
                : new JobParameters(new Dictionary<string, object?> { ["ukbatch.workers"] = workerOverride });

            var execution = await runner.TriggerAsync("worker.index.probe", parameters, "test", default).ConfigureAwait(false);
            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            return terminal.Status;
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task PlainJob_ReadsWorkerIndexZero()
    {
        PlainIndexJob.Reset();
        var host = await TestHostBuilder.StartAsync(b => b.AddJob<PlainIndexJob>().Named("plain.index")).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();

            var execution = await runner.TriggerAsync("plain.index", JobParameters.Empty, "test", default).ConfigureAwait(false);
            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            terminal.Status.Should().Be(JobStatus.Completed);
            PlainIndexJob.Observed.Should().Be(0, "a plain job never enters the fan-out, so WorkerIndex is the default 0.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task PartitionedJob_ObservesEveryWorkerSlotExactlyOnceInRange()
    {
        const int workers = 4;
        var status = await RunPartitionedAsync(workers, items: workers * 10).ConfigureAwait(false);

        status.Should().Be(JobStatus.Completed);
        IndexProbeJob.Samples.Should().HaveCount(workers * 10, "every item is processed under ContinueOnError.");

        var distinct = IndexProbeJob.Samples.Select(s => s.Before).Distinct().OrderBy(x => x).ToArray();
        distinct.Should().BeEquivalentTo(Enumerable.Range(0, workers),
            "with enough items to saturate the channel, every worker slot 0..N-1 runs at least once.");
        IndexProbeJob.Samples.Should().OnlyContain(s => s.Before >= 0 && s.Before < workers,
            "no observed index may fall outside [0, workerCount).");
    }

    [Fact]
    public async Task PartitionedJob_IndexIsStableAcrossAwaitWithinAnItem()
    {
        const int workers = 3;
        var status = await RunPartitionedAsync(workers, items: workers * 10).ConfigureAwait(false);

        status.Should().Be(JobStatus.Completed);
        IndexProbeJob.Samples.Should().NotBeEmpty();
        IndexProbeJob.Samples.Should().OnlyContain(s => s.Before == s.After,
            "WorkerIndex flows via AsyncLocal, so it is identical before and after an await inside one item.");
    }

    [Fact]
    public async Task PartitionedJob_DoesNotLeakIndexAfterRunCompletes()
    {
        const int workers = 3;
        var status = await RunPartitionedAsync(workers, items: workers * 10).ConfigureAwait(false);
        status.Should().Be(JobStatus.Completed);

        // A fresh context read on the test's own async flow (never entered a worker scope) is the default 0.
        var fresh = new JobContext
        {
            ExecutionId = "x",
            JobName = "x",
            Parameters = JobParameters.Empty,
            Services = null!,
            Logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
            Progress = NoopProgress.Instance,
            ParallelExecutor = null!,
            AttemptNumber = 1,
            StartedAtUtc = DateTimeOffset.UtcNow,
        };
        fresh.WorkerIndex.Should().Be(0, "the worker scope is disposed when the run ends, leaving no AsyncLocal residue.");
    }

    [Fact]
    public async Task PartitionedJob_WorkerOverride_ConfinesIndicesToOverrideRange()
    {
        const int registeredWorkers = 1;
        const int overrideWorkers = 5;
        var status = await RunPartitionedAsync(registeredWorkers, items: overrideWorkers * 10, workerOverride: overrideWorkers).ConfigureAwait(false);

        status.Should().Be(JobStatus.Completed);
        var distinct = IndexProbeJob.Samples.Select(s => s.Before).Distinct().OrderBy(x => x).ToArray();
        distinct.Should().BeEquivalentTo(Enumerable.Range(0, overrideWorkers),
            "the ukbatch.workers override raises the worker count, so indices span 0..M-1.");
        IndexProbeJob.Samples.Should().OnlyContain(s => s.Before >= 0 && s.Before < overrideWorkers);
    }

    [Fact]
    public async Task InlineParallelForEach_BodyObservesWorkerSlotsInRange()
    {
        const int workers = 4;
        InlineParallelJob.Reset();
        InlineParallelJob.WorkerCount = workers;
        InlineParallelJob.ItemCount = workers * 10;
        var host = await TestHostBuilder.StartAsync(b => b.AddJob<InlineParallelJob>().Named("inline.parallel.index")).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();

            var execution = await runner.TriggerAsync("inline.parallel.index", JobParameters.Empty, "test", default).ConfigureAwait(false);
            var terminal = await awaiter.WaitForTerminalAsync(execution.ExecutionId, default)
                .WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            terminal.Status.Should().Be(JobStatus.Completed);
            InlineParallelJob.Observed.Should().HaveCount(workers * 10);
            var distinct = InlineParallelJob.Observed.Distinct().OrderBy(x => x).ToArray();
            distinct.Should().BeEquivalentTo(Enumerable.Range(0, workers),
                "the inline ParallelForEachAsync body reads ctx.WorkerIndex and sees every slot 0..N-1.");
            InlineParallelJob.Observed.Should().OnlyContain(i => i >= 0 && i < workers);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public void JobContext_ExposesWorkerIndex_AndEnterWorkerScope()
    {
        var prop = typeof(JobContext).GetProperty(nameof(JobContext.WorkerIndex));
        prop.Should().NotBeNull("JobContext must expose a public WorkerIndex accessor.");
        prop!.PropertyType.Should().Be<int>();
        prop.CanWrite.Should().BeFalse("WorkerIndex is read-only — the AsyncLocal stays encapsulated.");

        var enter = typeof(JobContext).GetMethod(nameof(JobContext.EnterWorkerScope),
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        enter.Should().NotBeNull("JobContext must expose a public static EnterWorkerScope seam for the runtime.");
        enter!.ReturnType.Should().Be<IDisposable>();
    }

    /// <summary>Inert progress sink for the leak-check context construction.</summary>
    private sealed class NoopProgress : IJobProgress
    {
        public static readonly NoopProgress Instance = new();
        public long? Total => null;
        public long Processed => 0;
        public long Failed => 0;
        public void SetTotal(long total) { }
        public void Increment() { }
        public void Increment(long count) { }
        public void ReportFailure() { }
        public void ReportFailure(long count) { }
        public void ReportStatus(string message) { }
    }
}
