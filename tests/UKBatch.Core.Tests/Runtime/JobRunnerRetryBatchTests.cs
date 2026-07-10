using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
/// Retry-from-failed-step contract: a Failed run is never mutated; a retry is a NEW run that starts at
/// the failed run's cursor, carries its forwarded state, and links back via
/// <see cref="BatchRun.RetryOfBatchId"/>. The preconditions are strict because a wrong retry point
/// replays completed work — the exact hazard this entry point exists to prevent:
/// <list type="bullet">
///   <item>Only a <c>Failed</c> run is retryable (in-progress / Completed / Cancelled → rejected).</item>
///   <item>A compensated run is NOT retryable (its completed steps were already undone).</item>
///   <item>A cursor-less run with completed work is NOT retryable (the retry point cannot be proven).</item>
///   <item>A definition whose topology changed since the run started is NOT retryable.</item>
///   <item>The retried run re-runs NOTHING that completed — proven by an execution counter.</item>
/// </list>
/// </summary>
public class JobRunnerRetryBatchTests
{
    private sealed class CountingJob : IJob
    {
        public static int Executions;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Executions);
            return Task.CompletedTask;
        }
    }

    private sealed class FlakyJob : IJob
    {
        public static volatile bool FailNext;
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => FailNext
                ? throw new InvalidOperationException("flaky step failing on purpose")
                : Task.CompletedTask;
    }

    private sealed class AlwaysFailsJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
            => throw new InvalidOperationException("always fails");
    }

    private sealed class UndoJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Wraps the real in-memory run store but drops every cursor write — models an external store that
    /// left the cursor hooks as their default no-ops, so runs complete work yet record no retry point.
    /// </summary>
    private sealed class CursorlessBatchRunStore : IBatchRunStore
    {
        private readonly InMemoryBatchRunStore _inner = new();
        public Task CreateAsync(BatchRun run, CancellationToken ct)
            // Strip the create-time cursor too: this store cannot persist cursors at all.
            => _inner.CreateAsync(run with { CurrentStepIndex = null }, ct);
        public Task CompleteAsync(string batchId, JobStatus terminalStatus, BatchRunCounts counts, DateTimeOffset completedAtUtc, CancellationToken ct)
            => _inner.CompleteAsync(batchId, terminalStatus, counts, completedAtUtc, ct);
        public Task UpdateCursorAsync(string batchId, int nextStepIndex, CancellationToken ct) => Task.CompletedTask;
        public Task UpdateForwardedStateAsync(string batchId, IReadOnlyDictionary<string, object?> state, CancellationToken ct)
            => _inner.UpdateForwardedStateAsync(batchId, state, ct);
        public Task UpdateCompensationCursorAsync(string batchId, int? compensationStepIndex, CancellationToken ct) => Task.CompletedTask;
        public Task<BatchRun?> GetAsync(string batchId, CancellationToken ct) => _inner.GetAsync(batchId, ct);
        public Task<IReadOnlyList<BatchRun>> QueryAsync(BatchRunQuery query, CancellationToken ct) => _inner.QueryAsync(query, ct);
        public Task<long> CountAsync(BatchRunQuery query, CancellationToken ct) => _inner.CountAsync(query, ct);
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

    [Fact]
    public async Task Retry_FailedRun_NewRunStartsAtFailedStep_CompletedStepNotReRun()
    {
        CountingJob.Executions = 0;
        FlakyJob.FailNext = true;
        var store = new InMemoryBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddJob<FlakyJob>();
                b.AddBatch("retry.pipeline", x => x.RunJob<CountingJob>().ThenRunJob<FlakyJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("retry.pipeline")!;

            var failedRunId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var failedRun = await AwaitRunTerminalAsync(store, failedRunId);
            failedRun.Status.Should().Be(JobStatus.Failed);
            CountingJob.Executions.Should().Be(1, "the first step completed once in the original run");

            // The failing condition is cleared; the retry must continue from the failed step, not from the top.
            FlakyJob.FailNext = false;
            var newRunId = await runner.RetryBatchAsync(failedRunId, default);
            newRunId.Should().NotBe(failedRunId, "a retry is a NEW run — the failed run is history");

            var newRun = await AwaitRunTerminalAsync(store, newRunId);
            newRun.Status.Should().Be(JobStatus.Completed);
            newRun.RetryOfBatchId.Should().Be(failedRunId, "the new run links back to the run it retries");
            CountingJob.Executions.Should().Be(1,
                "the completed first step must NOT re-run — the retry starts at the failed step");

            var original = await store.GetAsync(failedRunId, CancellationToken.None);
            original!.Status.Should().Be(JobStatus.Failed, "the original run's terminal status is set-once history");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_NewRunCursorIsSetAtCreate()
    {
        FlakyJob.FailNext = true;
        var store = new InMemoryBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddJob<FlakyJob>();
                b.AddBatch("retry.cursor.pipeline", x => x.RunJob<CountingJob>().ThenRunJob<FlakyJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("retry.cursor.pipeline")!;

            var failedRunId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            await AwaitRunTerminalAsync(store, failedRunId);

            FlakyJob.FailNext = false;
            var newRunId = await runner.RetryBatchAsync(failedRunId, default);

            // The cursor is seeded at create time (not only after the first retried step completes), so a
            // crash before that first step still resumes from the retry point instead of replaying step 0.
            var newRun = await store.GetAsync(newRunId, CancellationToken.None);
            newRun!.CurrentStepIndex.Should().NotBeNull().And.Be(1, "the retry starts at the failed step's index");
            await AwaitRunTerminalAsync(store, newRunId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_CompletedRun_IsRejected()
    {
        var store = new InMemoryBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddBatch("retry.completed.pipeline", x => x.RunJob<CountingJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("retry.completed.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(store, runId);
            run.Status.Should().Be(JobStatus.Completed);

            var act = () => runner.RetryBatchAsync(runId, default);
            (await act.Should().ThrowAsync<BatchRunNotRetryableException>())
                .Which.BatchId.Should().Be(runId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_CompensatedRun_IsRejected()
    {
        var store = new InMemoryBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddJob<AlwaysFailsJob>();
                b.AddJob<UndoJob>();
                b.AddBatch("retry.compensated.pipeline", x => x
                    .RunJob<CountingJob>(step => step.CompensateWith<UndoJob>())
                    .ThenRunJob<AlwaysFailsJob>()
                    .FailurePolicy(BatchFailurePolicy.Compensate));
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("retry.compensated.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(store, runId);
            run.Status.Should().Be(JobStatus.Failed);
            run.CompensationStepIndex.Should().NotBeNull("the failed run unwound — its cursor records that");

            // Forward continuation after an unwind would replay work on top of a rolled-back state.
            var act = () => runner.RetryBatchAsync(runId, default);
            (await act.Should().ThrowAsync<BatchRunNotRetryableException>())
                .WithMessage("*compensated*");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_CursorlessStoreWithCompletedWork_IsRejected()
    {
        FlakyJob.FailNext = true;
        var store = new CursorlessBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddJob<FlakyJob>();
                b.AddBatch("retry.cursorless.pipeline", x => x.RunJob<CountingJob>().ThenRunJob<FlakyJob>());
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("retry.cursorless.pipeline")!;

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(store, runId);
            run.Status.Should().Be(JobStatus.Failed);
            run.CurrentStepIndex.Should().BeNull("this store drops cursor writes");
            run.Succeeded.Should().BeGreaterThan(0, "the first step completed");

            // Without a cursor the retry point cannot be proven; "from the beginning" would re-run the
            // completed first step, so the retry is refused outright.
            FlakyJob.FailNext = false;
            var act = () => runner.RetryBatchAsync(runId, default);
            (await act.Should().ThrowAsync<BatchRunNotRetryableException>())
                .WithMessage("*cursor*");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_DriftedDefinition_IsRejected()
    {
        FlakyJob.FailNext = true;
        var store = new InMemoryBatchRunStore();
        var host = await TestHostBuilder.StartAsync(
            b =>
            {
                b.AddJob<CountingJob>();
                b.AddJob<FlakyJob>();
            },
            services =>
            {
                services.RemoveAll<IBatchRunStore>();
                services.AddSingleton<IBatchRunStore>(store);
            });
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var defStore = host.Services.GetRequiredService<IBatchDefinitionStore>();

            // A store-backed definition (unlike code-registered ones) can change shape between the failed
            // run and the retry — exactly the drift the index-based retry must refuse.
            var def = new BatchDefinition
            {
                Id = "retry-drift-def",
                Name = "retry.drift.pipeline",
                Source = BatchSource.Api,
                Version = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                FailurePolicy = BatchFailurePolicy.StopOnFailure,
                Steps =
                [
                    new BatchStep { StepId = "s1", Order = 0, StepType = BatchStepType.Job, Job = new JobStepData { JobName = typeof(CountingJob).FullName! } },
                    new BatchStep { StepId = "s2", Order = 1, StepType = BatchStepType.Job, Job = new JobStepData { JobName = typeof(FlakyJob).FullName! } },
                ],
            };
            await defStore.CreateAsync(def, CancellationToken.None);

            var runId = await runner.TriggerBatchAsync(def.Id, null, "tester", default);
            var run = await AwaitRunTerminalAsync(store, runId);
            run.Status.Should().Be(JobStatus.Failed);

            // UpdateAsync's optimistic-concurrency check expects the CALLER to present the stored version.
            var drifted = def with
            {
                Version = 1,
                Steps =
                [
                    .. def.Steps,
                    new BatchStep { StepId = "s3", Order = 2, StepType = BatchStepType.Job, Job = new JobStepData { JobName = typeof(CountingJob).FullName! } },
                ],
            };
            await defStore.UpdateAsync(drifted, CancellationToken.None);

            FlakyJob.FailNext = false;
            var act = () => runner.RetryBatchAsync(runId, default);
            (await act.Should().ThrowAsync<BatchRunNotRetryableException>())
                .WithMessage("*changed*");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task Retry_UnknownRun_ThrowsNotFound()
    {
        var host = await TestHostBuilder.StartAsync(b => b.AddJob<CountingJob>());
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var act = () => runner.RetryBatchAsync("no-such-run", default);
            await act.Should().ThrowAsync<BatchRunNotFoundException>();
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
