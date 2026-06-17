using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Pins <c>IJobRunner.ResumeBatchAsync</c> and the completion-aggregation interaction that durable
/// resume depends on. A resumed run skips already-completed steps (<see cref="ResumePolicy.ResumeForward"/>),
/// re-runs from the start (<see cref="ResumePolicy.RestartAll"/>) or from an index
/// (<see cref="ResumePolicy.RestartFrom"/>); an orphan attempt of a re-run step is NOT double-counted; and
/// a never-resumed run's counts are bit-identical to a flat count (the zero-regression identity property).
/// </summary>
public class JobRunnerResumeBatchTests
{
    /// <summary>Records every step that ran, by job name, in order, with a fresh signal per test.</summary>
    public sealed class StepProbeJob : IJob
    {
        public static readonly ConcurrentQueue<string> Ran = new();
        public static void Reset() => Ran.Clear();
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    private static async Task<BatchRun> AwaitRunTerminalAsync(IBatchRunStore store, string runId)
    {
        BatchRun? run = null;
        var ok = await Waits.ForAsync(async () =>
        {
            run = await store.GetAsync(runId, CancellationToken.None);
            return run is { Status: not null };
        }, TimeSpan.FromSeconds(60));
        ok.Should().BeTrue("the run must reach a terminal stored status (60s deadlock backstop).");
        return run!;
    }

    /// <summary>
    /// Seeds a run-store record in its in-progress state (Status null) with the given cursor, mirroring
    /// what a crash would leave behind. <paramref name="stepCount"/> defaults to the definition's true
    /// step count so the drift guard does not trip.
    /// </summary>
    private static async Task SeedInProgressRunAsync(
        IBatchRunStore runStore, string runId, BatchDefinition def, int cursor, int stepCount)
    {
        await runStore.CreateAsync(new BatchRun
        {
            BatchId = runId,
            BatchDefinitionId = def.Id,
            BatchName = def.Name,
            Status = null,
            TriggeredBy = "tester",
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = null,
            StepCount = stepCount,
            Total = 0,
            Succeeded = 0,
            Failed = 0,
            Cancelled = 0,
        }, CancellationToken.None);
        await runStore.UpdateCursorAsync(runId, cursor, CancellationToken.None);
    }

    /// <summary>Inserts a terminal shadow execution row for a specific run + step into the in-memory store.</summary>
    private static Task SeedExecutionRowAsync(
        IJobStore jobStore, string runId, string stepId, JobStatus status, DateTimeOffset enqueuedAt, string jobName = "SeededJob")
    {
        var internalStore = (IJobStoreInternal)jobStore;
        return internalStore.InsertAsync(new JobExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            JobName = jobName,
            BatchId = runId,
            BatchStepId = stepId,
            BatchDefinitionId = null,
            Status = status,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = enqueuedAt,
            StartedAtUtc = enqueuedAt,
            CompletedAtUtc = enqueuedAt,
            AttemptNumber = 1,
            MaxRetries = 0,
            LastError = status == JobStatus.Failed ? "orphaned" : null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = "tester",
            WorkerName = null,
        }, CancellationToken.None);
    }

    private static async Task<IHost> StartTwoStepHostAsync(string batchName)
    {
        StepProbeJob.Reset();
        return await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<StepProbeJob>();
            b.AddBatch(batchName, x => x
                .RunJob<StepProbeJob>()
                .ThenRunJob<StepProbeJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        });
    }

    [Fact]
    public async Task ResumeBatchAsync_TerminalRun_NoOp()
    {
        var host = await StartTwoStepHostAsync("resume.terminal.noop");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.terminal.noop")!;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 2);
            // Complete the run so it is terminal.
            await runStore.CompleteAsync(runId, JobStatus.Completed, new BatchRunCounts(0, 0, 0, 0), DateTimeOffset.UtcNow, CancellationToken.None);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            // A terminal run is a no-op: no step runs.
            await Task.Delay(150);
            StepProbeJob.Ran.Should().BeEmpty("resuming a terminal run is a no-op");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeBatchAsync_MissingRun_Throws()
    {
        var host = await StartTwoStepHostAsync("resume.missing.run");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var act = async () => await runner.ResumeBatchAsync(Guid.NewGuid().ToString("N"), ResumePolicy.ResumeForward, CancellationToken.None);
            await act.Should().ThrowAsync<BatchRunNotFoundException>("an unknown run id cannot be resumed");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeBatchAsync_CursorAtEnd_FinalizesWithoutDispatch()
    {
        var host = await StartTwoStepHostAsync("resume.cursorend.finalize");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.cursorend.finalize")!;

            var runId = Guid.NewGuid().ToString("N");
            // Cursor == ordered top-level step count (2) → every step already finished.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 2, stepCount: 2);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "a cursor at the end finalizes the run without dispatching");
            StepProbeJob.Ran.Should().BeEmpty("no step is dispatched when the cursor is already at the end");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeForward_SkipsCompleted_RestartContinues()
    {
        // THE headline restart scenario. A 2-step run whose step 0 completed (cursor=1) before a crash.
        // ResumeForward re-dispatches only step 1; step 0 is NOT re-run.
        var host = await StartTwoStepHostAsync("resume.forward.skip");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.forward.skip")!;
            var step0Id = def.Steps[0].StepId;
            var step1Id = def.Steps[1].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 2);
            // Step 0 already completed on the prior attempt.
            await SeedExecutionRowAsync(jobStore, runId, step0Id, JobStatus.Completed, DateTimeOffset.UtcNow.AddMinutes(-5));

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(step1Id,
                "ResumeForward re-dispatches only the not-yet-completed step 1; step 0 is skipped");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RestartAll_ReRunsFromZero()
    {
        var host = await StartTwoStepHostAsync("resume.restartall");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.restartall")!;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 2);

            await runner.ResumeBatchAsync(runId, ResumePolicy.RestartAll, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            StepProbeJob.Ran.Should().HaveCount(2, "RestartAll re-runs every step from the beginning");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task RestartFrom_ReRunsFromIndex()
    {
        var host = await StartTwoStepHostAsync("resume.restartfrom");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.restartfrom")!;
            var step1Id = def.Steps[1].StepId;

            var runId = Guid.NewGuid().ToString("N");
            // Cursor 0 (nothing recorded yet) but RestartFrom(1) overrides to start at index 1.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0, stepCount: 2);

            await runner.ResumeBatchAsync(runId, ResumePolicy.RestartFrom(1), CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(step1Id,
                "RestartFrom(1) re-runs only from index 1");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeForward_DefinitionDrift_DegradesToRestartAll()
    {
        // The drift guard: the run's recorded StepCount no longer matches the current definition (a step
        // was added/removed). For the automatic ResumeForward path this degrades to RestartAll, so every
        // step re-runs (rather than trusting a cursor that indexes the OLD topology).
        var host = await StartTwoStepHostAsync("resume.drift.degrade");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.drift.degrade")!;

            var runId = Guid.NewGuid().ToString("N");
            // Seed a mismatched StepCount (the run claims 99 steps; the definition has 2) and cursor=1.
            // Without the guard, cursor=1 would skip step 0; the degrade-to-RestartAll re-runs both.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 99);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            StepProbeJob.Ran.Should().HaveCount(2, "definition drift degrades ResumeForward to RestartAll (every step re-runs)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Completion_OrphanAttempt_NotDoubleCounted()
    {
        // The interaction-1 guard. Step 0 has TWO rows for its single BatchStepId: a Failed orphan (an
        // interrupted attempt the reaper tombstoned) and a Completed re-run. On ResumeForward step 1 runs
        // and produces its own row. The run must complete Completed (the orphan is collapsed away,
        // Failed == 0), while Total counts every row that genuinely existed.
        var host = await StartTwoStepHostAsync("resume.orphan.count");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.orphan.count")!;
            var step0Id = def.Steps[0].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1, stepCount: 2);
            // Two rows for step 0's BatchStepId: the earlier Failed orphan, then the later Completed re-run.
            await SeedExecutionRowAsync(jobStore, runId, step0Id, JobStatus.Failed, DateTimeOffset.UtcNow.AddMinutes(-5));
            await SeedExecutionRowAsync(jobStore, runId, step0Id, JobStatus.Completed, DateTimeOffset.UtcNow.AddMinutes(-4));

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed,
                "the orphan attempt is collapsed away by the latest-attempt-per-step aggregation");
            run.Failed.Should().Be(0, "the Failed orphan is superseded by the Completed re-run of the same step");
            run.Succeeded.Should().Be(2, "step 0's latest attempt (Completed) + step 1 = 2 succeeded");
            run.Total.Should().Be(3, "Total counts every row that existed: the orphan, the re-run, and step 1's row");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task TriggerBatchAsync_AdvancesCursor()
    {
        // The normal trigger path advances the resume cursor after each completed step (SF-5). This is what
        // makes a crash mid-run recoverable: without it CurrentStepIndex stays null and recovery's
        // ResumeForward would restart from the beginning. A 3-step run must end with the cursor at 3 (every
        // step recorded), having advanced 1 → 2 → 3.
        var spy = new SpyRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<StepProbeJob>();
                b.AddBatch("trigger.cursor.advance", x => x.RunJob<StepProbeJob>().ThenRunJob<StepProbeJob>().ThenRunJob<StepProbeJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            StepProbeJob.Reset();
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("trigger.cursor.advance")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Completed);
            run.CurrentStepIndex.Should().Be(3,
                "the trigger path advances the cursor past every completed step (3 steps → cursor 3)");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Completion_NeverResumed_CountsBitIdentical()
    {
        // The identity guard: a normal run that is never resumed has exactly one row per step, so the
        // latest-attempt collapse is the identity function and the four counts equal a flat row count.
        var spy = new SpyRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<StepProbeJob>();
                b.AddBatch("resume.identity.clean", x => x.RunJob<StepProbeJob>().ThenRunJob<StepProbeJob>().ThenRunJob<StepProbeJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            StepProbeJob.Reset();
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.identity.clean")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(spy, runId);

            // Independently compute the flat counts over the run's rows and assert the stored counts match.
            var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
            run.Total.Should().Be(rows.Count, "a never-resumed run's Total equals the flat row count");
            run.Succeeded.Should().Be(rows.Count(r => r.Status == JobStatus.Completed),
                "a never-resumed run's Succeeded equals the flat completed count (the collapse is identity)");
            run.Succeeded.Should().Be(3);
            run.Failed.Should().Be(0);
            run.Cancelled.Should().Be(0);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    /// <summary>A transparent spy over the real in-memory run store (for tests that need direct read access).</summary>
    private sealed class SpyRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public Task CreateAsync(BatchRun run, CancellationToken cancellationToken) => _inner.CreateAsync(run, cancellationToken);
        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
            => _inner.CompleteAsync(batchId, terminalStatus, counts, completedAtUtc, cancellationToken);
        public Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken cancellationToken)
            => _inner.UpdateCursorAsync(batchId, nextStepIndex, cancellationToken);
        public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken) => _inner.GetAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);
    }
}
