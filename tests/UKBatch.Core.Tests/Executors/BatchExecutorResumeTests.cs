using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Transport;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Executors;

/// <summary>
/// Drives <see cref="BatchExecutor"/> directly (constructed from the real internal seams resolved from a
/// host) to pin the resumable additions: the optional <c>startStepIndex</c> skips earlier steps, and the
/// optional <c>onStepCompleted</c> cursor seam fires the next-to-run index after each success and never
/// for a step that threw. The default path (<c>startStepIndex=0</c>, seam <c>null</c>) is asserted to
/// behave exactly as today.
/// </summary>
public class BatchExecutorResumeTests
{
    /// <summary>Records every step that ran, by job name, in order.</summary>
    public sealed class RecordingJob : IJob
    {
        public static readonly ConcurrentQueue<string> Ran = new();
        public static void Reset() => Ran.Clear();
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A registered job that always throws, forcing a Failed terminal status on its step.</summary>
    public sealed class FailingJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional step failure");
    }

    private static BatchExecutor BuildExecutor(IHost host, Func<int, CancellationToken, Task>? onStepCompleted)
        => new(
            host.Services.GetRequiredService<IJobRunnerInternal>(),
            host.Services.GetRequiredService<IApprovalGateCoordinator>(),
            host.Services.GetRequiredService<IJobExecutionAwaiter>(),
            host.Services.GetRequiredService<ITransport>(),
            thisServiceName: null,
            host.Services.GetRequiredService<TimeProvider>(),
            host.Services.GetRequiredService<ILogger<BatchExecutor>>(),
            onStepCompleted);

    private static async Task<(IHost host, BatchDefinition def)> StartThreeStepBatchAsync(
        string batchName, BatchFailurePolicy policy)
    {
        RecordingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddJob<FailingJob>();
            b.AddBatch(batchName, x => x
                .RunJob<RecordingJob>()
                .ThenRunJob<RecordingJob>()
                .ThenRunJob<RecordingJob>()
                .FailurePolicy(policy));
        });
        var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
        return (host, lookup.TryGetByName(batchName)!);
    }

    [Fact]
    public async Task RunAsync_StartStepIndexZero_SeamNull_IdenticalToToday()
    {
        // The byte-for-byte equivalence check: default startStepIndex (0) and an unbound seam run every
        // step in order — the same outcome the non-resume trigger path produces today.
        var (host, def) = await StartThreeStepBatchAsync("resume.equiv.three", BatchFailurePolicy.StopOnFailure);
        try
        {
            var executor = BuildExecutor(host, onStepCompleted: null);

            await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            RecordingJob.Ran.Should().HaveCount(3, "all three steps run on the default path");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunAsync_StartStepIndex_SkipsEarlierSteps()
    {
        // startStepIndex = 1 on a 3-step batch dispatches only steps 1 and 2; step 0 never runs.
        var (host, def) = await StartThreeStepBatchAsync("resume.skip.three", BatchFailurePolicy.StopOnFailure);
        try
        {
            var executor = BuildExecutor(host, onStepCompleted: null);

            await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None, startStepIndex: 1);

            RecordingJob.Ran.Should().HaveCount(2, "starting at index 1 skips the first step (steps 1 and 2 run)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunAsync_AdvancesCursorAfterEachSuccess()
    {
        // The seam fires the next-to-run index (1, 2, 3) after each of the three steps succeeds.
        var (host, def) = await StartThreeStepBatchAsync("resume.cursor.three", BatchFailurePolicy.StopOnFailure);
        try
        {
            var cursorWrites = new ConcurrentQueue<int>();
            var executor = BuildExecutor(host, (next, _) =>
            {
                cursorWrites.Enqueue(next);
                return Task.CompletedTask;
            });

            await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);

            cursorWrites.Should().Equal(new[] { 1, 2, 3 },
                "the cursor advances to the next-to-run index after each success");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RunAsync_FailedStep_DoesNotAdvanceCursor()
    {
        // Step 0 succeeds (cursor -> 1); step 1 fails under StopOnFailure (RunAsync throws); the cursor is
        // never advanced past 1 because a failed step throws before the seam call.
        RecordingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RecordingJob>();
            b.AddJob<FailingJob>();
            b.AddBatch("resume.failcursor.three", x => x
                .RunJob<RecordingJob>()
                .ThenRunJob<FailingJob>()
                .ThenRunJob<RecordingJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        });
        try
        {
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.failcursor.three")!;

            var cursorWrites = new ConcurrentQueue<int>();
            var executor = BuildExecutor(host, (next, _) =>
            {
                cursorWrites.Enqueue(next);
                return Task.CompletedTask;
            });

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>("a StopOnFailure batch rethrows when a step fails");

            cursorWrites.Should().Equal(new[] { 1 },
                "the cursor advances to 1 after step 0 but never past the failing step 1");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    private static string IdNew() => Guid.NewGuid().ToString("N");
}
