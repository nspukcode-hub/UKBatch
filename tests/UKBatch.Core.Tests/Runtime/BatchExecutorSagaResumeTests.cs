using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using UKBatch.Storage;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Durable reverse-unwind: the compensation cursor is written marker-first (the failed step's index,
/// BEFORE the first compensator), then decremented to <c>j</c> after compensator <c>j</c>, then <c>0</c>
/// before the failure chain — and never cleared to null by the runtime. A run interrupted mid-unwind
/// (its <see cref="BatchRun.CompensationStepIndex"/> is set) resumes the UNWIND under
/// <see cref="ResumePolicy.ResumeForward"/>: only compensators below the cursor run (descending), a
/// compensator whose derived-id row already completed is skipped (effectively-once), the chain follows,
/// and the run finalizes Failed. <see cref="ResumePolicy.RestartAll"/>/<see cref="ResumePolicy.RestartFrom"/>
/// abandon the unwind (cursor cleared, forward replay); definition drift during an unwind finalizes the
/// run Failed WITHOUT compensating against the changed topology.
/// </summary>
public class BatchExecutorSagaResumeTests
{
    /// <summary>Every probe job appends its step id here, giving one global dispatch order per test.</summary>
    private static readonly ConcurrentQueue<string> Sequence = new();
    private static void ResetSequence() => Sequence.Clear();

    private static List<string> CompensatorEntries()
        => Sequence.Where(id => id.EndsWith(CompensationStepIds.Suffix, StringComparison.Ordinal)).ToList();

    public sealed class OkStepJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    public sealed class FailingStepJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("intentional step failure");
    }

    public sealed class CompProbeJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    public sealed class ChainProbeJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            return Task.CompletedTask;
        }
    }

    /// <summary>A compensator that signals entry, then parks until released — to freeze the unwind mid-flight.</summary>
    public sealed class GatedCompJob : IJob
    {
        public static TaskCompletionSource Entered { get; private set; } = NewSignal();
        public static TaskCompletionSource Release { get; private set; } = NewSignal();
        public static void Reset()
        {
            Entered = NewSignal();
            Release = NewSignal();
        }
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Sequence.Enqueue(context.BatchStepId ?? context.JobName);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A transparent spy over the real in-memory run store recording compensation-cursor writes.</summary>
    private sealed class CursorSpyRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public ConcurrentQueue<int?> CompensationCursorWrites { get; } = new();

        public Task CreateAsync(BatchRun run, CancellationToken cancellationToken) => _inner.CreateAsync(run, cancellationToken);
        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken cancellationToken)
            => _inner.CompleteAsync(batchId, terminalStatus, counts, completedAtUtc, cancellationToken);
        public Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken cancellationToken)
            => _inner.UpdateCursorAsync(batchId, nextStepIndex, cancellationToken);
        public Task UpdateForwardedStateAsync(string batchId, IReadOnlyDictionary<string, object?> state, CancellationToken cancellationToken)
            => _inner.UpdateForwardedStateAsync(batchId, state, cancellationToken);
        public Task UpdateCompensationCursorAsync(string batchId, int? compensationStepIndex, CancellationToken cancellationToken)
        {
            CompensationCursorWrites.Enqueue(compensationStepIndex);
            return _inner.UpdateCompensationCursorAsync(batchId, compensationStepIndex, cancellationToken);
        }
        public Task<BatchRun?> GetAsync(string batchId, CancellationToken cancellationToken) => _inner.GetAsync(batchId, cancellationToken);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.QueryAsync(query, cancellationToken);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken cancellationToken) => _inner.CountAsync(query, cancellationToken);
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
    /// Seeds a run-store record frozen mid-unwind (Status null, compensation cursor set), mirroring what
    /// a crash during compensation would leave behind. <paramref name="stepCount"/> must equal the
    /// definition's true step count (main steps + compensators + chain) or the drift guard trips.
    /// </summary>
    private static Task SeedUnwindingRunAsync(
        IBatchRunStore runStore, string runId, BatchDefinition def, int compensationCursor, int stepCount, int? forwardCursor = null)
        => runStore.CreateAsync(new BatchRun
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
            CurrentStepIndex = forwardCursor,
            CompensationStepIndex = compensationCursor,
        }, CancellationToken.None);

    /// <summary>Inserts a terminal execution row for a specific run + step id into the in-memory store.</summary>
    private static Task SeedExecutionRowAsync(
        IJobStore jobStore, string runId, string stepId, JobStatus status, string jobName = "SeededJob")
    {
        var internalStore = (IJobStoreInternal)jobStore;
        var now = DateTimeOffset.UtcNow;
        return internalStore.InsertAsync(new JobExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            JobName = jobName,
            BatchId = runId,
            BatchStepId = stepId,
            BatchDefinitionId = null,
            Status = status,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = now.AddMinutes(-5),
            StartedAtUtc = now.AddMinutes(-5),
            CompletedAtUtc = now.AddMinutes(-4),
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

    /// <summary>Builds the canonical 3-comp definition: A(comp) → B(comp) → C(comp), plus a one-step chain.</summary>
    private static async Task<IHost> StartThreeCompBatchHostAsync(string name)
    {
        ResetSequence();
        return await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddJob<ChainProbeJob>();
            b.AddBatch(name, x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate)
                .OnFailure(f => f.RunJob<ChainProbeJob>()));
        });
    }

    /// <summary>The 3-comp definition's step count: 3 main steps + 3 compensators + 1 chain step.</summary>
    private const int ThreeCompStepCount = 7;

    private static string IdNew() => Guid.NewGuid().ToString("N");

    // ===== cursor write discipline =====

    [Fact]
    public async Task CompensationCursor_WriteOrder_MarkerFirst_ThenPerCompensator_ThenZero()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.cursor.order", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<FailingStepJob>()
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.cursor.order")!;
            var cursorWrites = new ConcurrentQueue<int>();
            var executor = new BatchExecutor(
                host.Services.GetRequiredService<IJobRunnerInternal>(),
                host.Services.GetRequiredService<IApprovalGateCoordinator>(),
                host.Services.GetRequiredService<IJobExecutionAwaiter>(),
                host.Services.GetRequiredService<ITransport>(),
                thisServiceName: null,
                host.Services.GetRequiredService<TimeProvider>(),
                host.Services.GetRequiredService<ILogger<BatchExecutor>>(),
                onStepCompleted: null,
                onCompensationProgress: (index, _) =>
                {
                    cursorWrites.Enqueue(index);
                    return Task.CompletedTask;
                });

            var act = async () => await executor.RunAsync(def, IdNew(), JobParameters.Empty, "tester", CancellationToken.None);
            await act.Should().ThrowAsync<Exception>();

            cursorWrites.Should().Equal(new[] { 3, 2, 1, 0, 0 },
                "the marker is the failed step's index (3, BEFORE any compensator), then j after compensator j " +
                "(2, 1, 0 in reverse), then 0 again before the chain — a monotonically non-increasing sequence");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task CompensationCursor_MarkerPersistedBeforeFirstCompensator_AndNeverClearedToNull()
    {
        ResetSequence();
        GatedCompJob.Reset();
        var spy = new CursorSpyRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<OkStepJob>();
                b.AddJob<FailingStepJob>();
                b.AddJob<CompProbeJob>();
                b.AddJob<GatedCompJob>();
                b.AddBatch("saga.cursor.marker", x => x
                    .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                    .ThenRunJob<OkStepJob>(s => s.CompensateWith<GatedCompJob>())
                    .ThenRunJob<FailingStepJob>()
                    .FailurePolicy(BatchFailurePolicy.Compensate));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(spy);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.cursor.marker")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);

            // Freeze the unwind inside its FIRST compensator: the marker must already be durable.
            await GatedCompJob.Entered.Task.WaitAsync(TimeSpan.FromSeconds(60));
            var midUnwind = (await spy.GetAsync(runId, CancellationToken.None))!;
            midUnwind.CompensationStepIndex.Should().Be(2,
                "the unwind marker (the failed step's index) is persisted BEFORE the first compensator runs, " +
                "so a crash here resumes the whole unwind");

            GatedCompJob.Release.TrySetResult();
            var run = await AwaitRunTerminalAsync(spy, runId);

            run.Status.Should().Be(JobStatus.Failed);
            run.CompensationStepIndex.Should().Be(0, "the finished unwind leaves the cursor at 0, not null");
            spy.CompensationCursorWrites.Should().Equal(new int?[] { 2, 1, 0, 0 },
                "marker 2, then 1 after the gated compensator (j=1), then 0 after the last (j=0), then the chain marker 0");
            spy.CompensationCursorWrites.Should().NotContain((int?)null,
                "the runtime never clears the compensation cursor to null on its own paths");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== resume-in-unwind =====

    [Fact]
    public async Task Resume_MidUnwind_K_RunsOnlyCompensatorsBelowK_ThenChain_Failed()
    {
        var host = await StartThreeCompBatchHostAsync("saga.resume.k2");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.k2")!;

            // A crash left the unwind at cursor 2: compensator for step 2 already ran; [0, 2) remain.
            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 2, stepCount: ThreeCompStepCount);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed, "a resumed unwind still finalizes the run Failed");
            run.CompensationStepIndex.Should().Be(0, "the resumed unwind finished and left the cursor at 0");

            CompensatorEntries().Should().Equal(
                new[]
                {
                    CompensationStepIds.For(def.Steps[1].StepId),
                    CompensationStepIds.For(def.Steps[0].StepId),
                },
                "only compensators below the recorded cursor run, in descending index order — " +
                "the compensator at index 2 already ran before the crash and is not revisited");
            Sequence.Should().Contain(def.OnFailureSteps[0].StepId, "the failure chain runs after the unwind");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_MidUnwind_Zero_RunsChainWholesale_Failed()
    {
        var host = await StartThreeCompBatchHostAsync("saga.resume.k0");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.k0")!;

            // Cursor 0: the unwind had finished; the crash landed before/during the chain.
            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 0, stepCount: ThreeCompStepCount);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed);
            CompensatorEntries().Should().BeEmpty("cursor 0 means the unwind already finished — no compensator re-runs");
            Sequence.Should().Contain(def.OnFailureSteps[0].StepId, "the failure chain re-runs wholesale");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_RestartAll_ClearsCursor_RunsForward()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.resume.restartall", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.restartall")!;

            // Frozen mid-unwind; the operator explicitly abandons the unwind with RestartAll.
            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 2, stepCount: 4, forwardCursor: 1);

            await runner.ResumeBatchAsync(runId, ResumePolicy.RestartAll, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the forward replay succeeded end-to-end");
            run.CompensationStepIndex.Should().BeNull("RestartAll clears the unwind cursor before replaying forward");
            CompensatorEntries().Should().BeEmpty("no compensator runs on the abandoned-unwind forward replay");
            Sequence.Should().Contain(new[] { def.Steps[0].StepId, def.Steps[1].StepId },
                "RestartAll replays every main step from the beginning");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_RestartFrom_ClearsCursor_RunsForward()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.resume.restartfrom", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.restartfrom")!;

            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 2, stepCount: 4, forwardCursor: 1);

            await runner.ResumeBatchAsync(runId, ResumePolicy.RestartFrom(1), CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            run.CompensationStepIndex.Should().BeNull("RestartFrom clears the unwind cursor before replaying forward");
            CompensatorEntries().Should().BeEmpty();
            Sequence.Should().Equal(new[] { def.Steps[1].StepId },
                "RestartFrom(1) replays forward from index 1 only");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_DriftDuringUnwind_FinalizesFailed_NoCompensation()
    {
        var host = await StartThreeCompBatchHostAsync("saga.resume.drift");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.drift")!;

            // The run's recorded StepCount no longer matches the definition: an index-based unwind against
            // a changed topology could compensate the wrong step, so recovery must not unwind at all.
            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 2, stepCount: 99);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed, "drift during an unwind finalizes the run Failed, un-wedging recovery");
            CompensatorEntries().Should().BeEmpty("no compensator may run against a drifted topology");
            Sequence.Should().NotContain(def.OnFailureSteps[0].StepId, "the chain is part of compensation and is skipped too");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_CompensatorDedupe_LocalCompletedRow_NotReRun()
    {
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.resume.dedupe.local", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.dedupe.local")!;

            // The crash landed AFTER step 1's compensator completed but BEFORE its cursor write, so the
            // cursor still reads 2 — the documented at-least-once window. The compensator's derived-id row
            // is already Completed, so the resumed unwind must skip it instead of compensating twice.
            var runId = IdNew();
            await SeedUnwindingRunAsync(runStore, runId, def, compensationCursor: 2, stepCount: 4);
            await SeedExecutionRowAsync(jobStore, runId, CompensationStepIds.For(def.Steps[1].StepId), JobStatus.Completed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed);
            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[0].StepId) },
                "the compensator whose derived-id row is already Completed is NOT re-dispatched (effectively-once); " +
                "only the genuinely-unfinished compensator runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Resume_CompensatorDedupe_CrossServiceCompletedRow_NotReRun_TransportNotCalled()
    {
        ResetSequence();
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-exec",
                Status = JobStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });

        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.Configure(o => o.ThisServiceName = "orchestrator");
                b.AddJob<OkStepJob>();
                b.AddBatch("saga.resume.dedupe.remote", x => x
                    .RunJob<OkStepJob>(s => s.CompensateWith("RemoteComp", c => c.OnService("billing")))
                    .ThenRunJob<OkStepJob>()
                    .FailurePolicy(BatchFailurePolicy.Compensate));
            },
            services =>
            {
                services.RemoveAll<ITransport>();
                services.AddSingleton(transport);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.dedupe.remote")!;
            var stepCount = 3;   // 2 main steps + 1 compensator

            // First resume: the cross-service compensator's derived-id row is already Completed → skipped.
            var runA = IdNew();
            await SeedUnwindingRunAsync(runStore, runA, def, compensationCursor: 2, stepCount: stepCount);
            await SeedExecutionRowAsync(jobStore, runA, CompensationStepIds.For(def.Steps[0].StepId), JobStatus.Completed);

            await runner.ResumeBatchAsync(runA, ResumePolicy.ResumeForward, CancellationToken.None);
            (await AwaitRunTerminalAsync(runStore, runA)).Status.Should().Be(JobStatus.Failed);

            await transport.DidNotReceive().RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());

            // Second resume, no prior row: the same compensator IS dispatched — proof the skip above came
            // from the completed-row dedupe, not from the cross-service arm being unreachable.
            var runB = IdNew();
            await SeedUnwindingRunAsync(runStore, runB, def, compensationCursor: 2, stepCount: stepCount);

            await runner.ResumeBatchAsync(runB, ResumePolicy.ResumeForward, CancellationToken.None);
            (await AwaitRunTerminalAsync(runStore, runB)).Status.Should().Be(JobStatus.Failed);

            await transport.Received(1).RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== resume-forward skip-exclusion =====

    [Fact]
    public async Task Resume_Forward_SkippedStep_NotCompensated()
    {
        // The resume-FORWARD twin of the trigger-path SkippedStep_IsNotCompensated_DuringUnwind: a step
        // skipped by a run-if condition on the ORIGINAL attempt lives only in the durable Skipped row (the
        // fresh RunAsync starts with an empty in-memory skip set). When a LATER step fails on the resumed
        // forward run, the unwind walks the full prior range — so the skip set MUST be rebuilt from the store,
        // or the skipped step's compensator would wrongly run (e.g. "refund a card that was never charged").
        ResetSequence();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkStepJob>();
            b.AddJob<FailingStepJob>();
            b.AddJob<CompProbeJob>();
            b.AddBatch("saga.resume.forward.skip", x => x
                .RunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())      // step 0: skipped on the original attempt
                .ThenRunJob<OkStepJob>(s => s.CompensateWith<CompProbeJob>())  // step 1: completed on the original attempt
                .ThenRunJob<FailingStepJob>()                                  // step 2: resumes here and fails → unwind
                .FailurePolicy(BatchFailurePolicy.Compensate));
        });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("saga.resume.forward.skip")!;

            // Original attempt state persisted before a crash: step 0 SKIPPED, step 1 COMPLETED, forward
            // cursor at step 2, no unwind started (compensation cursor null). StepCount = 3 main + 2 comps = 5.
            var runId = IdNew();
            await runStore.CreateAsync(new BatchRun
            {
                BatchId = runId,
                BatchDefinitionId = def.Id,
                BatchName = def.Name,
                Status = null,
                TriggeredBy = "tester",
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = null,
                StepCount = 5,
                Total = 0,
                Succeeded = 0,
                Failed = 0,
                Cancelled = 0,
                CurrentStepIndex = 2,       // resume forward at step 2
                CompensationStepIndex = null,   // forward crash — no unwind was in progress
            }, CancellationToken.None);
            await SeedExecutionRowAsync(jobStore, runId, def.Steps[0].StepId, JobStatus.Skipped);
            await SeedExecutionRowAsync(jobStore, runId, def.Steps[1].StepId, JobStatus.Completed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed, "step 2 fails on the resumed forward run, triggering the unwind");
            CompensatorEntries().Should().Equal(
                new[] { CompensationStepIds.For(def.Steps[1].StepId) },
                "only the COMPLETED step (1) is compensated; the step SKIPPED on the original attempt (0) must NOT be — " +
                "its durable Skipped row excludes it from the resumed unwind");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
