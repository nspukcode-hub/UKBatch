using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Abstractions.Transport;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// Edge-case resume idempotency: a resumed run must not re-open an approval gate that was already decided
/// before the crash, and must not re-dispatch a cross-service step that already terminated. The probes are
/// consulted ONLY on the resume path, so a NORMAL trigger (no prior decision / no prior terminal row) is
/// byte-for-byte — that first-pass equivalence is pinned alongside the resume behavior.
/// </summary>
public class ResumeIdempotencyTests
{
    /// <summary>Records every step that ran by its step id, with a fresh signal per test.</summary>
    private sealed class StepProbeJob : IJob
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

    /// <summary>Seeds a run-store record in its in-progress state (Status null) with the given cursor.</summary>
    private static async Task SeedInProgressRunAsync(
        IBatchRunStore runStore, string runId, BatchDefinition def, int cursor)
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
            StepCount = def.Steps.Count,
            Total = 0,
            Succeeded = 0,
            Failed = 0,
            Cancelled = 0,
        }, CancellationToken.None);
        await runStore.UpdateCursorAsync(runId, cursor, CancellationToken.None);
    }

    /// <summary>Seeds a DECIDED approval-gate record for a (run, step) into the gate store.</summary>
    private static Task SeedDecidedGateAsync(
        IApprovalGateStore gateStore, string runId, string stepId, ApprovalRecordOutcome outcome)
        => gateStore.SaveAsync(new PersistedApprovalGate
        {
            ApprovalId = Guid.NewGuid().ToString("N"),
            BatchId = runId,
            BatchStepId = stepId,
            BatchDefinitionId = "def",
            Config = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold },
            Status = ApprovalRecordStatus.Decided,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Outcome = outcome,
            DecidedBy = "operator",
            DecidedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-4),
        }, CancellationToken.None);

    /// <summary>Seeds a PENDING approval-gate record for a (run, step) into the gate store (a crash-orphan).</summary>
    private static Task SeedPendingGateAsync(
        IApprovalGateStore gateStore, string runId, string stepId, string approvalId, DateTimeOffset? deadlineUtc = null)
        => gateStore.SaveAsync(new PersistedApprovalGate
        {
            ApprovalId = approvalId,
            BatchId = runId,
            BatchStepId = stepId,
            BatchDefinitionId = "def",
            Config = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold },
            Status = ApprovalRecordStatus.Pending,
            PendingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            DeadlineUtc = deadlineUtc,
        }, CancellationToken.None);

    /// <summary>
    /// Inserts a shadow execution row for a (run, step) into the in-memory store. An optional
    /// <paramref name="outputs"/> set models a completed cross-service step that persisted its returned
    /// values — the durable source resume forwards to the next step. Defaulting to <c>null</c> keeps the
    /// existing callers unchanged.
    /// </summary>
    private static Task SeedShadowRowAsync(
        IJobStore jobStore, string runId, string stepId, JobStatus status, string jobName = "RemoteJob",
        IReadOnlyDictionary<string, object?>? outputs = null)
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
            Outputs = outputs,
            EnqueuedAtUtc = now.AddMinutes(-5),
            StartedAtUtc = now.AddMinutes(-5),
            CompletedAtUtc = JobStatusTransitions.IsTerminal(status) ? now.AddMinutes(-4) : null,
            AttemptNumber = 1,
            MaxRetries = 0,
            LastError = status == JobStatus.Failed ? "orphaned" : null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = "tester",
            WorkerName = "billing",
        }, CancellationToken.None);
    }

    // ===== ApprovalGate resume idempotency =====

    /// <summary>Builds a 3-step batch: Job → ApprovalGate(Hold) → Job. The gate is step index 1.</summary>
    private static async Task<IHost> StartGateBatchHostAsync(string name)
    {
        StepProbeJob.Reset();
        return await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<StepProbeJob>();
            b.AddBatch(name, x => x
                .RunJob<StepProbeJob>()
                .ThenWaitForApproval("Confirm", new[] { "admin" }, timeout: TimeSpan.FromMinutes(5), onTimeout: ApprovalTimeoutAction.Hold)
                .ThenRunJob<StepProbeJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        });
    }

    [Fact]
    public async Task ResumeApprovalGate_AlreadyApproved_Skips()
    {
        var host = await StartGateBatchHostAsync("resume.gate.approved");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.approved")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            // Step 0 done (cursor=1, the gate). A prior Approved decision is on record.
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);
            await SeedDecidedGateAsync(gateStore, runId, gateStepId, ApprovalRecordOutcome.Approved);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "an already-approved gate is skipped and the run finishes");
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(lastStepId,
                "only the post-gate step runs; the gate did not re-open and block");

            // No NEW pending gate was created by resume (the only record is the seeded decided one).
            var gates = await gateStore.ListByBatchAsync(runId, CancellationToken.None);
            gates.Should().ContainSingle("resume must not mint a second gate for an already-decided step");
            gates[0].Status.Should().Be(ApprovalRecordStatus.Decided);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_AlreadyRejected_FailsStep()
    {
        var host = await StartGateBatchHostAsync("resume.gate.rejected");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.rejected")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);
            await SeedDecidedGateAsync(gateStore, runId, gateStepId, ApprovalRecordOutcome.Rejected);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Failed, "a prior Rejected gate decision fails the resumed step");
            StepProbeJob.Ran.Should().BeEmpty("the gate failed the step, so the post-gate step never runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_Interrupted_ReOpensGate()
    {
        // SF-1: Interrupted is a crash-orphan marker (reaper-set), NOT a human decision. Resume must RE-OPEN
        // the gate (a real decision can still be made), not fail-route. Here the re-opened gate is approved
        // mid-run, after which the run completes through the post-gate step.
        var host = await StartGateBatchHostAsync("resume.gate.interrupted");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var gateService = host.Services.GetRequiredService<IApprovalGateService>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.interrupted")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);
            // A reaper-style Interrupted record for the gate step (crash-orphan, not a human no).
            await SeedDecidedGateAsync(gateStore, runId, gateStepId, ApprovalRecordOutcome.Interrupted);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            // The gate re-opened: a fresh PENDING gate appears for the step. Approve it.
            string? freshGateId = null;
            var opened = await Waits.ForAsync(async () =>
            {
                var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
                freshGateId = pending.FirstOrDefault(p => p.BatchStepId == gateStepId)?.ApprovalId;
                return freshGateId is not null;
            }, TimeSpan.FromSeconds(30));
            opened.Should().BeTrue("an Interrupted gate must RE-OPEN as a fresh pending gate on resume");

            await gateService.ApproveAsync(
                freshGateId!, new ApproverContext { Identity = "operator", Roles = new[] { "admin" } }, "ok", CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the re-opened gate was approved and the run finished");
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(lastStepId,
                "after the re-opened gate is approved, the post-gate step runs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_NoPriorDecision_AwaitsNormally()
    {
        // First-pass equivalence: with no prior decided record the probe returns null/empty, so the gate
        // arm opens a fresh gate exactly as the original run would. Approving it lets the run complete.
        var host = await StartGateBatchHostAsync("resume.gate.noprior");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateService = host.Services.GetRequiredService<IApprovalGateService>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.noprior")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);   // no gate record seeded

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            string? gateId = null;
            var opened = await Waits.ForAsync(async () =>
            {
                var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
                gateId = pending.FirstOrDefault(p => p.BatchStepId == gateStepId)?.ApprovalId;
                return gateId is not null;
            }, TimeSpan.FromSeconds(30));
            opened.Should().BeTrue("with no prior decision the gate opens normally (byte-for-byte first pass)");

            await gateService.ApproveAsync(
                gateId!, new ApproverContext { Identity = "operator", Roles = new[] { "admin" } }, "ok", CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(lastStepId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_PendingGateExists_ReattachesToSameGate_NoSecondPending()
    {
        // A crash within the reaper grace window can leave the gate PENDING (no decided record). Resume must
        // RE-ATTACH to the existing pending gate, not mint a second one — the operator must see exactly ONE
        // approval for the step. Approving the re-attached gate lets the run finish through the post-gate step.
        var host = await StartGateBatchHostAsync("resume.gate.pending");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var gateService = host.Services.GetRequiredService<IApprovalGateService>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.pending")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);
            // A pending gate from the prior attempt that was never decided (crash-orphan within grace).
            var priorGateId = Guid.NewGuid().ToString("N");
            await SeedPendingGateAsync(gateStore, runId, gateStepId, priorGateId);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            // Approve the re-attached gate, polling until it is LIVE (the reattach runs on the resume's
            // fire-and-forget task, so there is a brief window where only the store record is visible and
            // _gates has not yet registered the awaiter). ApprovalNotFoundException ⇒ not yet live ⇒ retry.
            // The SAME approval id the operator already saw is what we approve — re-attach did not re-mint.
            var approved = await Waits.ForAsync(async () =>
            {
                var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
                var forStep = pending.Where(p => p.BatchStepId == gateStepId).ToList();
                // There is never more than one gate for the step — the store record and the live re-attached
                // awaiter share the SAME id (live wins on dedupe). Two would mean a second gate was minted.
                forStep.Should().HaveCountLessThanOrEqualTo(1, "re-attach must not produce a second pending gate for the step");
                var live = forStep.FirstOrDefault();
                if (live is null)
                {
                    return false;
                }
                live.ApprovalId.Should().Be(priorGateId, "the re-attached gate keeps the SAME approval id the operator already saw");
                try
                {
                    await gateService.ApproveAsync(
                        live.ApprovalId, new ApproverContext { Identity = "operator", Roles = new[] { "admin" } }, "ok", CancellationToken.None);
                    return true;
                }
                catch (ApprovalNotFoundException)
                {
                    return false;   // the live awaiter has not registered yet — keep polling
                }
            }, TimeSpan.FromSeconds(30));
            approved.Should().BeTrue("the one re-attached pending gate must become approvable");

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the re-attached gate was approved and the run finished");
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(lastStepId,
                "after the re-attached gate is approved, the post-gate step runs");

            // No second gate RECORD was created for the step.
            var gates = await gateStore.ListByBatchAsync(runId, CancellationToken.None);
            gates.Count(g => g.BatchStepId == gateStepId).Should().Be(1,
                "re-attach reuses the existing record; it must not create a second gate record for the step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_PendingGate_DecidedApprovedBeforeReattach_RoutesOnFinalOutcome()
    {
        // Race close-out: ReattachApprovalAsync reads the stored record by id; if it has since been DECIDED
        // (a dashboard click landed on the recovered pending record, or a reaper decided it) the re-attach
        // routes on the now-final outcome instead of re-registering an awaiter. An Approved decision → the
        // step proceeds without opening any gate. Driven directly against the internal coordinator with a
        // seeded Decided record, modelling "decided between the probe read and the re-attach".
        var host = await StartGateBatchHostAsync("resume.gate.reattach.race");
        try
        {
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var coordinator = host.Services.GetRequiredService<IApprovalGateCoordinator>();
            var gateService = host.Services.GetRequiredService<IApprovalGateService>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.reattach.race")!;
            var gateStepId = def.Steps[1].StepId;

            var runId = Guid.NewGuid().ToString("N");
            // The id was Pending when the probe saw it, but is DECIDED (Approved) by the time re-attach reads.
            var raceGateId = Guid.NewGuid().ToString("N");
            await SeedDecidedGateAsync(gateStore, runId, gateStepId, ApprovalRecordOutcome.Approved);
            // Re-key the seeded decided record onto the id the executor would re-attach to.
            await gateStore.SaveAsync(new PersistedApprovalGate
            {
                ApprovalId = raceGateId,
                BatchId = runId,
                BatchStepId = gateStepId,
                BatchDefinitionId = def.Id,
                Config = new ApprovalGateConfig { Title = "Confirm", AllowedRoles = new[] { "admin" }, OnTimeout = ApprovalTimeoutAction.Hold },
                Status = ApprovalRecordStatus.Decided,
                PendingSinceUtc = DateTimeOffset.UtcNow.AddMinutes(-2),
                Outcome = ApprovalRecordOutcome.Approved,
                DecidedBy = "operator",
                DecidedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            }, CancellationToken.None);

            // Re-attach returns immediately (already approved) — no exception, and no new pending gate opens.
            await coordinator.ReattachApprovalAsync(
                raceGateId, runId, gateStepId, def.Steps[1].Approval!, def.Name, def.Id, CancellationToken.None);

            var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
            pending.Where(p => p.BatchStepId == gateStepId).Should().BeEmpty(
                "re-attaching to an already-Approved record proceeds without opening any gate");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeApprovalGate_DecidedCancelledGate_ReopensFreshGate()
    {
        // The decided-arm re-open still holds with the new arm ordering: a Cancelled record is a
        // crash-orphan teardown marker (not a human decision), so the decided arm re-opens a fresh gate —
        // and the new Pending-adoption check sits AFTER the decided arm, so it never intercepts this.
        // Approving the re-opened gate lets the run finish.
        var host = await StartGateBatchHostAsync("resume.gate.decided.cancelled");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var gateStore = host.Services.GetRequiredService<IApprovalGateStore>();
            var gateService = host.Services.GetRequiredService<IApprovalGateService>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.gate.decided.cancelled")!;
            var gateStepId = def.Steps[1].StepId;
            var lastStepId = def.Steps[2].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 1);
            await SeedDecidedGateAsync(gateStore, runId, gateStepId, ApprovalRecordOutcome.Cancelled);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            string? freshGateId = null;
            var opened = await Waits.ForAsync(async () =>
            {
                var pending = await gateService.ListPendingAsync(null, CancellationToken.None);
                freshGateId = pending.FirstOrDefault(p => p.BatchStepId == gateStepId)?.ApprovalId;
                return freshGateId is not null;
            }, TimeSpan.FromSeconds(30));
            opened.Should().BeTrue("a Cancelled (crash-orphan) record must RE-OPEN a fresh gate on resume");

            await gateService.ApproveAsync(
                freshGateId!, new ApproverContext { Identity = "operator", Roles = new[] { "admin" } }, "ok", CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the re-opened gate was approved and the run finished");
            StepProbeJob.Ran.Should().ContainSingle().Which.Should().Be(lastStepId);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== Cross-service resume idempotency =====

    private static ITransport SubstituteTransport(JobStatus replyStatus)
    {
        var transport = Substitute.For<ITransport>();
        transport.Name.Returns("Test");
        transport.RequestReplyAsync(Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(new JobResult
            {
                ExecutionId = "remote-exec",
                Status = replyStatus,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            });
        return transport;
    }

    /// <summary>Builds a single-step cross-service batch (RemoteJob on "billing").</summary>
    private static async Task<IHost> StartCrossServiceHostAsync(string name, ITransport transport)
    {
        return await TestHostBuilder.StartAsync(
            b =>
            {
                b.Configure(o => o.ThisServiceName = "orchestrator");
                b.AddBatch(name, x => x.RunJob("RemoteJob", step => step.OnService("billing")));
            },
            services =>
            {
                services.RemoveAll<ITransport>();
                services.AddSingleton(transport);
            });
    }

    /// <summary>A local step that captures its invocation parameters and signals when it ran.</summary>
    private sealed class ForwardCaptureJob : IJob
    {
        public static JobParameters? Captured;
        public static TaskCompletionSource Ran = New();
        public static void Reset() { Captured = null; Ran = New(); }
        private static TaskCompletionSource New() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Captured = context.Parameters;
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>Builds a cross-service step (RemoteJob on "billing") followed by a local capturing step.</summary>
    private static async Task<IHost> StartCrossServiceThenLocalHostAsync(string name, ITransport transport)
    {
        ForwardCaptureJob.Reset();
        return await TestHostBuilder.StartAsync(
            b =>
            {
                b.Configure(o => o.ThisServiceName = "orchestrator");
                b.AddJob<ForwardCaptureJob>();
                b.AddBatch(name, x => x
                    .RunJob("RemoteJob", step => step.OnService("billing"))
                    .ThenRunJob<ForwardCaptureJob>());
            },
            services =>
            {
                services.RemoveAll<ITransport>();
                services.AddSingleton(transport);
            });
    }

    [Fact]
    public async Task ResumeCrossService_PriorCompletedShadow_SkipsTransport()
    {
        var transport = SubstituteTransport(JobStatus.Completed);
        var host = await StartCrossServiceHostAsync("resume.cross.completed", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.cross.completed")!;
            var stepId = def.Steps[0].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0);
            // A prior attempt already COMPLETED this cross-service step (Completed shadow row exists).
            await SeedShadowRowAsync(jobStore, runId, stepId, JobStatus.Completed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the prior Completed shadow lets the step skip and the run finish");

            // The decisive assertion: the transport was NOT called — the step was skipped via the shadow.
            await transport.DidNotReceive().RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeCrossService_PriorFailedOrphanShadow_ReDispatches()
    {
        // At-least-once invariant: a reaper-tombstoned Failed shadow row does NOT prove the remote work
        // finished (the row was tombstoned because the orchestrator crashed mid-flight, not because the
        // worker reported a real failure), so resume must RE-DISPATCH — the transport IS called. This is the
        // symmetric counterpart of an Interrupted gate re-opening rather than fail-routing. Only a Completed
        // row proves completion.
        var transport = SubstituteTransport(JobStatus.Completed);
        var host = await StartCrossServiceHostAsync("resume.cross.failed", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.cross.failed")!;
            var stepId = def.Steps[0].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0);
            // A reaper-tombstoned Failed orphan row exists from the interrupted attempt (terminal but
            // ambiguous — does not prove the step actually finished on the remote worker).
            await SeedShadowRowAsync(jobStore, runId, stepId, JobStatus.Failed);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed, "the re-dispatched step completes, finishing the run");

            // The decisive assertion: the transport WAS called — the ambiguous Failed orphan re-dispatched.
            await transport.Received(1).RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeCrossService_PriorRunningShadow_ReDispatches()
    {
        // At-least-once invariant: a non-terminal (Running/orphan) shadow row does NOT prove the remote work
        // finished, so resume re-dispatches — the transport IS called.
        var transport = SubstituteTransport(JobStatus.Completed);
        var host = await StartCrossServiceHostAsync("resume.cross.running", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.cross.running")!;
            var stepId = def.Steps[0].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0);
            // Only a non-terminal (Running) orphan row exists from the interrupted attempt.
            await SeedShadowRowAsync(jobStore, runId, stepId, JobStatus.Running);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            // The decisive assertion: the transport WAS called — the in-flight-at-crash step re-dispatched.
            await transport.Received(1).RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeCrossService_PriorCompletedShadowWithOutputs_ForwardsToNextStep()
    {
        // A prior attempt COMPLETED the cross-service step and persisted its returned values on the shadow
        // row. On resume the step is skipped (transport NOT called), yet its outputs must still forward to the
        // next step — the durable-resume equivalent of the live fold. The output is seeded as the JsonElement
        // shape a JSON-backed store rehydrates, so the downstream read exercises the JSON-aware path.
        var transport = SubstituteTransport(JobStatus.Completed);
        var host = await StartCrossServiceThenLocalHostAsync("resume.cross.outputs", transport);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("resume.cross.outputs")!;
            var stepId = def.Steps[0].StepId;

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunAsync(runStore, runId, def, cursor: 0);
            // Completed shadow for step 0 carrying invoiceId as the durable JsonElement shape.
            var invoiceId = JsonDocument.Parse("\"INV-1\"").RootElement.Clone();
            await SeedShadowRowAsync(jobStore, runId, stepId, JobStatus.Completed,
                outputs: new Dictionary<string, object?> { ["invoiceId"] = invoiceId });

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            await ForwardCaptureJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60));

            // The skipped step still forwarded its persisted output to the downstream local step...
            ForwardCaptureJob.Captured!.GetRequired<string>("invoiceId").Should().Be("INV-1");
            // ...and the transport was NOT called (the prior Completed shadow let the step skip).
            await transport.DidNotReceive().RequestReplyAsync(
                Arg.Any<string>(), Arg.Any<JobMessage>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    // ===== Gate probe "latest decided" ordering (DecidedAtUtc, id tiebreak) =====

    private static ApprovalGateView DecidedGateView(
        string stepId, ApprovalRecordOutcome outcome, string approvalId, DateTimeOffset? decidedAtUtc)
        => new()
        {
            ApprovalId = approvalId,
            BatchId = "run",
            BatchStepId = stepId,
            Status = ApprovalRecordStatus.Decided,
            Outcome = outcome,
            DecidedAtUtc = decidedAtUtc,
        };

    [Fact]
    public async Task GateProbe_TwoDecidedRecords_PicksLatestByDecidedAtUtc()
    {
        const string step = "gate-step";
        var basis = DateTimeOffset.UtcNow;
        // Two decided records for the same step. The EARLIER decision carries the lexicographically HIGHER
        // id, so an id-only sort would wrongly pick it; DecidedAtUtc must win and select the later decision.
        var earlierButHigherId = DecidedGateView(step, ApprovalRecordOutcome.Rejected, approvalId: "zzz", decidedAtUtc: basis);
        var laterButLowerId = DecidedGateView(step, ApprovalRecordOutcome.Approved, approvalId: "aaa", decidedAtUtc: basis.AddMinutes(1));

        var gateService = Substitute.For<IApprovalGateService>();
        gateService.ListForBatchAsync("run", Arg.Any<CancellationToken>())
            .Returns(new[] { earlierButHigherId, laterButLowerId });

        var probe = new ResumeGateProbe(gateService);
        var outcome = await probe.TryGetDecidedOutcomeAsync("run", step, CancellationToken.None);

        outcome.Should().Be(ApprovalRecordOutcome.Approved,
            "the most recent DecidedAtUtc wins regardless of the approval id sort order");
    }

    [Fact]
    public async Task GateProbe_MissingDecidedAtUtc_FallsBackToIdTiebreak()
    {
        const string step = "gate-step";
        // Neither record carries a DecidedAtUtc (old/incomplete records): fall back to the UUIDv7 id, where
        // the higher id is the most recent decision.
        var lowerId = DecidedGateView(step, ApprovalRecordOutcome.Rejected, approvalId: "aaa", decidedAtUtc: null);
        var higherId = DecidedGateView(step, ApprovalRecordOutcome.Approved, approvalId: "zzz", decidedAtUtc: null);

        var gateService = Substitute.For<IApprovalGateService>();
        gateService.ListForBatchAsync("run", Arg.Any<CancellationToken>())
            .Returns(new[] { lowerId, higherId });

        var probe = new ResumeGateProbe(gateService);
        var outcome = await probe.TryGetDecidedOutcomeAsync("run", step, CancellationToken.None);

        outcome.Should().Be(ApprovalRecordOutcome.Approved,
            "with no timestamp the higher UUIDv7 id is the tiebreak for the most recent decision");
    }

    [Fact]
    public async Task GateProbe_TimestampedRecordBeatsUntimestamped()
    {
        const string step = "gate-step";
        // A timestamped record beats one without a timestamp, even if the untimestamped record has the higher
        // id — a present DecidedAtUtc is more authoritative than the id-only fallback.
        var timestampedLowerId = DecidedGateView(step, ApprovalRecordOutcome.Approved, approvalId: "aaa", decidedAtUtc: DateTimeOffset.UtcNow);
        var untimestampedHigherId = DecidedGateView(step, ApprovalRecordOutcome.Rejected, approvalId: "zzz", decidedAtUtc: null);

        var gateService = Substitute.For<IApprovalGateService>();
        gateService.ListForBatchAsync("run", Arg.Any<CancellationToken>())
            .Returns(new[] { untimestampedHigherId, timestampedLowerId });

        var probe = new ResumeGateProbe(gateService);
        var outcome = await probe.TryGetDecidedOutcomeAsync("run", step, CancellationToken.None);

        outcome.Should().Be(ApprovalRecordOutcome.Approved,
            "a record carrying a DecidedAtUtc is preferred over an untimestamped one regardless of id");
    }
}
