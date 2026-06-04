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
/// <c>IPartitionedJob&lt;TItem&gt;.FinalizeAsync</c> (the unit-of-work commit hook).
/// Contract under test: runs EXACTLY ONCE after ALL items completed (accumulate-then-commit is safe);
/// NOT invoked on a FailFast abort (all-or-nothing preserved); runs under ContinueOnError with the
/// successful subset; a throw inside it fails the job.
/// </summary>
public class PartitionedFinalizeTests
{
    public sealed class FinalizeProbeJob : IPartitionedJob<int>
    {
        public const int Items = 10;
        public static int FailAtItem = -1;            // -1 = no item failure
        public static bool ThrowInFinalize;
        public static int FinalizeCalls;
        public static int ResultsAtFinalize = -1;     // snapshot of the accumulated bag inside Finalize

        // The REAL pattern: per-run instance accumulation (ProcessAsync writes, FinalizeAsync commits).
        private readonly ConcurrentBag<int> _results = new();

        public static void Reset(int failAt = -1, bool throwInFinalize = false)
        {
            Interlocked.Exchange(ref FailAtItem, failAt);
            ThrowInFinalize = throwInFinalize;
            Interlocked.Exchange(ref FinalizeCalls, 0);
            Interlocked.Exchange(ref ResultsAtFinalize, -1);
        }

        public async IAsyncEnumerable<int> SourceAsync(
            JobContext context,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            for (var i = 0; i < Items; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return i;
            }
            await Task.CompletedTask.ConfigureAwait(false);
        }

        public async Task ProcessAsync(int item, JobContext context, CancellationToken cancellationToken)
        {
            if (item == FailAtItem)
            {
                throw new InvalidOperationException($"injected item failure at {item}");
            }
            await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken).ConfigureAwait(false);
            _results.Add(item);
        }

        public Task FinalizeAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FinalizeCalls);
            Interlocked.Exchange(ref ResultsAtFinalize, _results.Count);   // "AddRange + SaveChanges" point
            if (ThrowInFinalize)
            {
                throw new InvalidOperationException("finalize (commit) failed");
            }
            return Task.CompletedTask;
        }
    }

    private static async Task<JobStatus> RunAsync(ItemErrorPolicy policy)
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddPartitionedJob<FinalizeProbeJob, int>().Named("probe.finalize")
                .WithParallelism(3)
                .WithItemErrorPolicy(policy)
                .WithMaxRetries(0);
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var awaiter = host.Services.GetRequiredService<IJobExecutionAwaiter>();
            var execution = await runner.TriggerAsync("probe.finalize", JobParameters.Empty, "test", default).ConfigureAwait(false);
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
    public async Task Completed_FinalizeRunsExactlyOnce_AfterAllItemsAccumulated()
    {
        FinalizeProbeJob.Reset();
        var status = await RunAsync(ItemErrorPolicy.FailFast).ConfigureAwait(false);

        status.Should().Be(JobStatus.Completed);
        FinalizeProbeJob.FinalizeCalls.Should().Be(1);
        // Every ProcessAsync finished BEFORE Finalize ran — the bag already held all 10 results.
        FinalizeProbeJob.ResultsAtFinalize.Should().Be(FinalizeProbeJob.Items);
    }

    [Fact]
    public async Task FailFast_FinalizeIsNotInvoked_NothingCommits()
    {
        FinalizeProbeJob.Reset(failAt: 3);
        var status = await RunAsync(ItemErrorPolicy.FailFast).ConfigureAwait(false);

        status.Should().Be(JobStatus.Failed);
        FinalizeProbeJob.FinalizeCalls.Should().Be(0);   // all-or-nothing: the commit point never ran
    }

    [Fact]
    public async Task ContinueOnError_FinalizeRuns_WithSuccessfulSubset()
    {
        FinalizeProbeJob.Reset(failAt: 3);
        var status = await RunAsync(ItemErrorPolicy.ContinueOnError).ConfigureAwait(false);

        status.Should().Be(JobStatus.Completed);
        FinalizeProbeJob.FinalizeCalls.Should().Be(1);
        FinalizeProbeJob.ResultsAtFinalize.Should().Be(FinalizeProbeJob.Items - 1);   // failed item excluded
    }

    [Fact]
    public async Task FinalizeThrow_FailsTheJob()
    {
        FinalizeProbeJob.Reset(throwInFinalize: true);
        var status = await RunAsync(ItemErrorPolicy.FailFast).ConfigureAwait(false);

        status.Should().Be(JobStatus.Failed);
        FinalizeProbeJob.FinalizeCalls.Should().Be(1);
    }
}
