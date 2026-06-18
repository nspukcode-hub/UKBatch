using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using UKBatch;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Runtime;
using UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;
using Xunit;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Core;

/// <summary>
/// The headline durable-resume guarantee, automated and Docker-free: a batch run that a host crash leaves
/// in-flight is resumed AUTOMATICALLY on the next host start, continuing from its recorded cursor so an
/// already-completed step is NOT re-run. Two SEQUENTIAL real hosts point at the SAME SQLite <b>file</b>:
/// host A triggers a two-step run, completes step 0, parks in step 1, then is disposed mid-run ("process
/// dies"); host B is a cold boot whose <c>DurableRunRecovery</c> hosted service re-launches the in-flight
/// run with <see cref="ResumePolicy.ResumeForward"/>. Step 0 runs EXACTLY ONCE (proven by a deterministic
/// per-step invocation marker), the run completes, and the orphaned step-1 attempt is collapsed out of the
/// recorded counts.
/// </summary>
/// <remarks>
/// This is the automated complement to <c>JobRunnerResumeBatchTests</c> (which drives <c>ResumeBatchAsync</c>
/// directly): here recovery fires through the REAL DI-registered hosted service on an actual host restart,
/// not a direct call. It mirrors <c>HostRestartPersistenceTests</c> (a SQLite file across two containers),
/// but starts real <see cref="IHost"/>s so the hosted services — including <c>DurableRunRecovery</c> — run.
/// <para><b>Cursor is NOT seeded manually.</b> The normal trigger path now advances the cursor after each
/// completed step, so host A records cursor=1 automatically when step 0 finishes — exactly the real
/// recoverable state. The test proves resume works end-to-end through the genuine trigger → crash → restart
/// flow, with no test-only cursor write.</para>
/// </remarks>
public sealed class DurableRunRecoveryRestartE2ETests
{
    /// <summary>
    /// Deterministic, process-wide coordination shared by the two hosts. Records every step invocation
    /// keyed by job name (so step 0 running twice would be visible) and lets host A's step-1 job park until
    /// the test releases it (so host A is genuinely mid-step-1 at the crash, and host B's re-run does not
    /// block). Reset per test.
    /// </summary>
    private static class RunMarkers
    {
        public static readonly ConcurrentQueue<string> Invocations = new();

        public static TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource Release { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Invocations.Clear();
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static int CountFor(string jobName) => Invocations.Count(n => n == jobName);
    }

    /// <summary>Step 0: records its invocation and returns immediately. Must run exactly once across the restart.</summary>
    private sealed class Step0Job : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            RunMarkers.Invocations.Enqueue(nameof(Step0Job));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Step 1: records its invocation, signals that step 1 was entered, then waits for the test's release.
    /// On host A it parks here (a hung job that ignores the CT, modelling work that did not honor cancel),
    /// keeping the run non-terminal at the crash. On host B the release is already set, so the re-run returns
    /// immediately.
    /// </summary>
    private sealed class Step1Job : IJob
    {
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            RunMarkers.Invocations.Enqueue(nameof(Step1Job));
            RunMarkers.Entered.TrySetResult();
            await RunMarkers.Release.Task.ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Markers + jobs for the GRACEFUL-shutdown scenarios (a gate-parked run and a mid-job run), kept
    /// separate from the crash-path <see cref="RunMarkers"/> so the two scenarios never contaminate each
    /// other's invocation counts.
    /// </summary>
    private static class GraceMarkers
    {
        public static readonly ConcurrentQueue<string> Invocations = new();

        public static TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static TaskCompletionSource Release { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset()
        {
            Invocations.Clear();
            Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public static int CountFor(string jobName) => Invocations.Count(n => n == jobName);
    }

    /// <summary>Quick job: records its invocation and returns. Used as step 0 / post-gate steps.</summary>
    private sealed class GraceQuickJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            GraceMarkers.Invocations.Enqueue(context.BatchStepId ?? nameof(GraceQuickJob));
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Parking job for the mid-job graceful variant: records its invocation, signals it was entered, then
    /// parks until EITHER the host cancellation token trips (host A, graceful stop → throws OCE) OR the test
    /// releases it (host B re-run → returns).
    /// </summary>
    private sealed class GraceParkingJob : IJob
    {
        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            GraceMarkers.Invocations.Enqueue(context.BatchStepId ?? nameof(GraceParkingJob));
            GraceMarkers.Entered.TrySetResult();
            // Wait for whichever comes first: the host cancellation (host A's graceful stop) or the test's
            // release (host B). On host A the cancellation wins → ThrowIfCancellationRequested throws OCE, so
            // the step is interrupted mid-flight. On host B the release wins and the token is not cancelled,
            // so the re-run returns and the run completes.
            var delayTask = Task.Delay(Timeout.Infinite, cancellationToken);
            await Task.WhenAny(delayTask, GraceMarkers.Release.Task).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
    }

    [Fact]
    public async Task DurableRunRecovery_ResumesGracefullyStoppedRun_AcrossHostRestart()
    {
        // The graceful-shutdown counterpart of the Dispose-crash test above. A real `docker compose restart`
        // is SIGTERM → StopAsync, which (before this slice) terminalized a gate-parked run as Cancelled so
        // recovery skipped it. Now a graceful host stop LEAVES THE RUN IN-FLIGHT (Status null) and recovery
        // resumes it on the next start, re-opening exactly ONE gate. THE regression lock is the assertion
        // that Status is still null immediately after StopAsync.
        GraceMarkers.Reset();
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-grace-e2e-{Guid.NewGuid():N}.db");
        const string defId = "grace-e2e-def";
        const string step0Id = "grace-e2e-step-0";
        const string gateStepId = "grace-e2e-gate-1";
        const string step2Id = "grace-e2e-step-2";
        var definition = TestData.BatchDef(
            defId, "resume.grace.e2e",
            steps: new[]
            {
                TestData.JobStep(step0Id, order: 0, jobName: typeof(GraceQuickJob).FullName!),
                // OnTimeout=Fail with no timeout is a legitimate INDEFINITE wait — the gate parks until it is
                // approved (or until host shutdown cancels it). Hold/AutoApprove would require a timeout.
                TestData.ApprovalStep(gateStepId, order: 1, config: TestData.GateConfig(allowedRoles: new[] { "admin" }, onTimeout: ApprovalTimeoutAction.Fail)),
                TestData.JobStep(step2Id, order: 2, jobName: typeof(GraceQuickJob).FullName!),
            });

        try
        {
            string runId;

            // ===== Host A: trigger, complete step 0, park on the gate, then GRACEFULLY stop (SIGTERM). =====
            var hostA = BuildGraceHost(dbPath);
            await MigrateAsync(hostA);
            await hostA.StartAsync();
            try
            {
                var runner = hostA.Services.GetRequiredService<IJobRunner>();
                var runStore = hostA.Services.GetRequiredService<IBatchRunStore>();
                var defStore = hostA.Services.GetRequiredService<IBatchDefinitionStore>();
                var gateService = hostA.Services.GetRequiredService<IApprovalGateService>();
                await defStore.CreateAsync(definition, CancellationToken.None);

                runId = await runner.TriggerBatchAsync(defId, null, "tester", CancellationToken.None);

                // Wait until the gate is pending — step 0 has completed by then, and the cursor advanced to 1.
                await WaitForPendingGateAsync(gateService, gateStepId, TimeSpan.FromSeconds(30));
                GraceMarkers.CountFor(step0Id).Should().Be(1, "step 0 ran once on host A");
                await WaitForCursorAsync(runStore, runId, 1, TimeSpan.FromSeconds(10));
                (await runStore.GetAsync(runId, CancellationToken.None))!.Status
                    .Should().BeNull("the run is parked on the gate and has not completed");

                // GRACEFUL stop (SIGTERM equivalent), NOT Dispose: this is the path that previously wrote Cancelled.
                await hostA.StopAsync(TimeSpan.FromSeconds(5));

                // THE regression lock: a graceful stop must leave the run IN-FLIGHT (Status null), NOT
                // finalize it Cancelled — otherwise recovery would skip it. Before this slice this was Cancelled.
                (await runStore.GetAsync(runId, CancellationToken.None))!.Status
                    .Should().BeNull("a graceful host stop must leave the gate-parked run in-flight (Status null), not Cancelled");
            }
            finally
            {
                hostA.Dispose();
            }

            // ===== Host B: cold boot → DurableRunRecovery resumes the in-flight run, re-opening ONE gate. =====
            var hostB = BuildGraceHost(dbPath);
            await MigrateAsync(hostB);
            await hostB.StartAsync();
            try
            {
                var runStore = hostB.Services.GetRequiredService<IBatchRunStore>();
                var gateService = hostB.Services.GetRequiredService<IApprovalGateService>();

                // Recovery resumed the run and reached the gate again — exactly ONE pending gate for the step.
                string? freshGateId = null;
                var opened = await Waits_ForAsync(async () =>
                {
                    var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
                    var forStep = pending.Where(p => p.BatchStepId == gateStepId).ToList();
                    forStep.Count.Should().BeLessThanOrEqualTo(1, "resume must re-open/re-attach exactly one gate, never two");
                    freshGateId = forStep.FirstOrDefault()?.ApprovalId;
                    return freshGateId is not null;
                }, TimeSpan.FromSeconds(30));
                opened.Should().BeTrue("recovery resumes the run and re-opens exactly one pending gate");

                await gateService.ApproveAsync(
                    freshGateId!, new ApproverContext { Identity = "operator", Roles = new[] { "admin" } }, "ok", CancellationToken.None);

                var run = await AwaitRunTerminalAsync(runStore, runId, TimeSpan.FromSeconds(60));
                run.Status.Should().Be(JobStatus.Completed, "the re-opened gate was approved and the resumed run finished");

                // Idempotency proof: step 0 ran EXACTLY ONCE in total (host A only); resume skipped it.
                GraceMarkers.CountFor(step0Id).Should().Be(1, "ResumeForward skips the already-completed step 0");
                // The post-gate step ran once, on host B (it had not run before the shutdown).
                GraceMarkers.CountFor(step2Id).Should().Be(1, "the post-gate step ran once on the resumed host");

                // Exactly one gate pending right now collapsed to zero after approval; the run completed.
                (await gateService.ListPendingAsync(null, CancellationToken.None))
                    .Where(p => p.BatchStepId == gateStepId).Should().BeEmpty("the gate resolved on approval");
            }
            finally
            {
                await StopGracefullyAsync(hostB);
            }
        }
        finally
        {
            GraceMarkers.Release.TrySetResult();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task DurableRunRecovery_ResumesGracefullyStoppedRun_MidLocalJob_AcrossHostRestart()
    {
        // The mid-LOCAL-JOB graceful variant: host A is interrupted by a graceful StopAsync while a job is
        // executing (not parked on a gate). The run must be left in-flight (Status null) and resumed; the
        // at-least-once boundary means the interrupted step re-runs on host B.
        GraceMarkers.Reset();
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-grace-job-e2e-{Guid.NewGuid():N}.db");
        const string defId = "grace-job-e2e-def";
        const string step0Id = "grace-job-e2e-step-0";
        const string step1Id = "grace-job-e2e-step-1";
        var definition = TestData.BatchDef(
            defId, "resume.grace.job.e2e",
            steps: new[]
            {
                TestData.JobStep(step0Id, order: 0, jobName: typeof(GraceQuickJob).FullName!),
                TestData.JobStep(step1Id, order: 1, jobName: typeof(GraceParkingJob).FullName!),
            });

        try
        {
            string runId;

            var hostA = BuildGraceHost(dbPath);
            await MigrateAsync(hostA);
            await hostA.StartAsync();
            try
            {
                var runner = hostA.Services.GetRequiredService<IJobRunner>();
                var runStore = hostA.Services.GetRequiredService<IBatchRunStore>();
                var defStore = hostA.Services.GetRequiredService<IBatchDefinitionStore>();
                await defStore.CreateAsync(definition, CancellationToken.None);

                runId = await runner.TriggerBatchAsync(defId, null, "tester", CancellationToken.None);

                // Wait until step 1 is executing (step 0 completed, cursor=1).
                await GraceMarkers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                GraceMarkers.CountFor(step0Id).Should().Be(1, "step 0 ran once on host A");
                await WaitForCursorAsync(runStore, runId, 1, TimeSpan.FromSeconds(10));

                // GRACEFUL stop while step 1 runs: the parking job unblocks on the cancellation, the step
                // throws OCE, and the closure leaves the run in-flight.
                await hostA.StopAsync(TimeSpan.FromSeconds(5));

                (await runStore.GetAsync(runId, CancellationToken.None))!.Status
                    .Should().BeNull("a graceful stop mid-local-job must leave the run in-flight (Status null), not Cancelled");
            }
            finally
            {
                hostA.Dispose();
            }

            // Release stayed UNSET through host A (it was interrupted via the host cancellation token, not the
            // release), so host B's re-run of step 1 is what consumes the release below and completes.
            var hostB = BuildGraceHost(dbPath);
            await MigrateAsync(hostB);
            await hostB.StartAsync();
            try
            {
                var runStore = hostB.Services.GetRequiredService<IBatchRunStore>();
                GraceMarkers.Release.TrySetResult();   // let host B's re-run of the parking job complete

                var run = await AwaitRunTerminalAsync(runStore, runId, TimeSpan.FromSeconds(60));
                run.Status.Should().Be(JobStatus.Completed, "the resumed run finished after the graceful restart");

                GraceMarkers.CountFor(step0Id).Should().Be(1, "ResumeForward skips the already-completed step 0");
                GraceMarkers.CountFor(step1Id).Should().Be(2, "step 1 ran on host A (interrupted) and re-ran on host B");
            }
            finally
            {
                await StopGracefullyAsync(hostB);
            }
        }
        finally
        {
            GraceMarkers.Release.TrySetResult();
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    [Fact]
    public async Task DurableRunRecovery_ResumesInFlightRun_AcrossHostRestart()
    {
        RunMarkers.Reset();
        var dbPath = Path.Combine(Path.GetTempPath(), $"ukbatch-resume-e2e-{Guid.NewGuid():N}.db");
        // Stable definition + step ids, PERSISTED to the EF store so both hosts resolve the SAME topology
        // (a code-defined batch gets fresh random ids per host, which cannot be resumed across a restart —
        // durable resume targets a persisted dashboard/api-created definition). The jobs are registered by
        // NAME in both hosts; the definition references them by name.
        const string defId = "e2e-def";
        const string step0Id = "e2e-step-0";
        const string step1Id = "e2e-step-1";
        var definition = TestData.BatchDef(
            defId, "resume.e2e",
            steps: new[]
            {
                TestData.JobStep(step0Id, order: 0, jobName: typeof(Step0Job).FullName!),
                TestData.JobStep(step1Id, order: 1, jobName: typeof(Step1Job).FullName!),
            });

        try
        {
            string runId;

            // ===== Host A: persist the definition, trigger the run, complete step 0, park in step 1, then "crash". =====
            var hostA = BuildHost(dbPath);
            await MigrateAsync(hostA);  // create the schema before starting (the Core scheduler queries it on start)
            await hostA.StartAsync();
            try
            {
                var runner = hostA.Services.GetRequiredService<IJobRunner>();
                var runStore = hostA.Services.GetRequiredService<IBatchRunStore>();
                var defStore = hostA.Services.GetRequiredService<IBatchDefinitionStore>();
                await defStore.CreateAsync(definition, CancellationToken.None);

                runId = await runner.TriggerBatchAsync(defId, null, "tester", CancellationToken.None);

                // Wait until step 1 is actually executing — step 0 has completed by then.
                await RunMarkers.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
                RunMarkers.CountFor(nameof(Step0Job)).Should().Be(1, "step 0 ran once on host A");

                // The normal trigger path advances the cursor itself: completing step 0 records cursor=1.
                // No test-only cursor write — recovery resumes from the genuinely-recorded progress.
                await WaitForCursorAsync(runStore, runId, 1, TimeSpan.FromSeconds(10));

                // The run row must still be in-flight (Status null) at the crash.
                (await runStore.GetAsync(runId, CancellationToken.None))!.Status
                    .Should().BeNull("the run is mid-step-1 and has not completed");
            }
            finally
            {
                // Crash: dispose host A WITHOUT a graceful StopAsync, so the run's completion finalizer never
                // runs and the run row stays in-flight. The detached, parked step-1 task leaks until released
                // below — exactly what a killed process leaves behind.
                hostA.Dispose();
            }

            // Release the parked step-1 job so the leaked host-A task unwinds, and so host B's re-run of
            // step 1 returns immediately instead of parking again.
            RunMarkers.Release.TrySetResult();

            // ===== Host B: cold boot over the SAME file → DurableRunRecovery auto-resumes the in-flight run. =====
            var hostB = BuildHost(dbPath);
            await MigrateAsync(hostB);  // idempotent: the file already has the schema (no started migrator here)
            await hostB.StartAsync();   // hosted services start: DurableRunRecovery → schema guard → reaper
            try
            {
                var runStore = hostB.Services.GetRequiredService<IBatchRunStore>();
                var jobStore = hostB.Services.GetRequiredService<IJobStore>();

                // Recovery resumed the run; wait for it to reach a terminal stored status.
                var run = await AwaitRunTerminalAsync(runStore, runId, TimeSpan.FromSeconds(60));

                run.Status.Should().Be(JobStatus.Completed,
                    "DurableRunRecovery resumed the in-flight run with ResumeForward and it finished");

                // THE idempotency proof: step 0 ran exactly ONCE in total (host A only); resume skipped it.
                RunMarkers.CountFor(nameof(Step0Job)).Should().Be(1,
                    "ResumeForward skips the already-completed step 0 — it must NOT run a second time");
                // Step 1 re-ran on host B (it had not completed before the crash).
                RunMarkers.CountFor(nameof(Step1Job)).Should().Be(2,
                    "step 1 ran on host A (parked) and re-ran to completion on host B");

                // The orphaned step-1 attempt is collapsed out of the recorded counts (latest-per-step),
                // while the run completes successfully.
                run.Failed.Should().Be(0, "the orphaned step-1 attempt is superseded by the completed re-run");
                run.Succeeded.Should().Be(2, "step 0 (completed on host A) + step 1's completed re-run = 2");

                // Sanity: the store holds the orphan + re-run rows for step 1 (the row history is honest),
                // but only the latest attempt feeds the terminal verdict above.
                var rows = await jobStore.QueryAsync(new JobQuery { BatchId = runId, Limit = int.MaxValue, Offset = 0 }, CancellationToken.None);
                rows.Count(r => r.BatchStepId == step1Id).Should().BeGreaterThanOrEqualTo(2,
                    "step 1 has both the orphaned attempt and the completed re-run");
                rows.Count(r => r.BatchStepId == step0Id).Should().Be(1, "step 0 was dispatched once");
            }
            finally
            {
                await StopGracefullyAsync(hostB);
            }
        }
        finally
        {
            RunMarkers.Release.TrySetResult();   // defensive: never leave a job parked
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }
        }
    }

    private static IHost BuildHost(string dbPath)
    {
        return new HostBuilder()
            .ConfigureLogging(lb => lb.ClearProviders().SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddUKBatch(b =>
                {
                    // Jobs are registered by name in both hosts; the persisted definition references them.
                    b.AddJob<Step0Job>();
                    b.AddJob<Step1Job>();
                    b.Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
                });
                services.AddUKBatchEntityFrameworkCoreStores(o =>
                {
                    o.UseSqlite($"DataSource={dbPath}");
                    // Migrate explicitly below (before StartAsync) — the Core scheduler queries the schema on
                    // start, which runs before the EF hosted migrator. DurableRunRecovery runs regardless.
                    o.MigrateOnStartup = false;
                });
            })
            .Build();
    }

    private static async Task MigrateAsync(IHost host)
    {
        var factory = host.Services.GetRequiredService<IDbContextFactory<UKBatchDbContext>>();
        await using var ctx = await factory.CreateDbContextAsync();
        await ctx.Database.MigrateAsync();
    }

    private static async Task WaitForCursorAsync(IBatchRunStore store, string runId, int expected, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            var run = await store.GetAsync(runId, CancellationToken.None);
            if (run?.CurrentStepIndex == expected) return;
            await Task.Delay(20).ConfigureAwait(false);
        }
        (await store.GetAsync(runId, CancellationToken.None))!.CurrentStepIndex
            .Should().Be(expected, "the cursor write must commit before the crash");
    }

    private static async Task<BatchRun> AwaitRunTerminalAsync(IBatchRunStore store, string runId, TimeSpan timeout)
    {
        BatchRun? run = null;
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            run = await store.GetAsync(runId, CancellationToken.None);
            if (run is { Status: not null }) return run;
            await Task.Delay(25).ConfigureAwait(false);
        }
        run.Should().NotBeNull();
        run!.Status.Should().NotBeNull("the resumed run must reach a terminal stored status (60s backstop)");
        return run;
    }

    private static async Task StopGracefullyAsync(IHost host)
    {
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException)
        {
            // Shutdown timeout — acceptable in test teardown.
        }
        finally
        {
            host.Dispose();
        }
    }

    /// <summary>Builds a host wired with the graceful-scenario jobs (quick + parking), over the SAME SQLite file.</summary>
    private static IHost BuildGraceHost(string dbPath)
    {
        return new HostBuilder()
            .ConfigureLogging(lb => lb.ClearProviders().SetMinimumLevel(LogLevel.Warning))
            .ConfigureServices(services =>
            {
                services.AddUKBatch(b =>
                {
                    b.AddJob<GraceQuickJob>();
                    b.AddJob<GraceParkingJob>();
                    b.Configure(o => o.ShutdownTimeout = TimeSpan.FromSeconds(2));
                });
                services.AddUKBatchEntityFrameworkCoreStores(o =>
                {
                    o.UseSqlite($"DataSource={dbPath}");
                    o.MigrateOnStartup = false;
                });
            })
            .Build();
    }

    /// <summary>Polls until a pending approval gate for <paramref name="stepId"/> appears, or fails on the timeout.</summary>
    private static async Task WaitForPendingGateAsync(IApprovalGateService gateService, string stepId, TimeSpan timeout)
    {
        var ok = await Waits_ForAsync(async () =>
        {
            var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
            return pending.Any(p => p.BatchStepId == stepId);
        }, timeout);
        ok.Should().BeTrue("the approval gate must register as pending within the timeout");
    }

    /// <summary>Deadline-bounded async poll (no fixed delay-then-assert): true when the predicate succeeds, else false.</summary>
    private static async Task<bool> Waits_ForAsync(Func<Task<bool>> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (await predicate().ConfigureAwait(false))
            {
                return true;
            }
            await Task.Delay(25).ConfigureAwait(false);
        }
        return await predicate().ConfigureAwait(false);
    }
}
