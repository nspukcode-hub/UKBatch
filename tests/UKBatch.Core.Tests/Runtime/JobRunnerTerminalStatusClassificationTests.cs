using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// <c>JobRunner.TriggerBatchAsync</c> captures the run's terminal verdict on
/// <see cref="BatchCompletionSignalPayload.RuntimeTerminalStatus"/> so the hub fan-out can override a
/// row aggregate that is blind to a gate failure. This pins the classification: a rejected approval
/// gate (StopOnFailure) rethrows out of RunAsync → <see cref="JobStatus.Failed"/>; a graceful host stop
/// while the run is parked on a gate → the run is LEFT IN-FLIGHT (no terminal verdict, run-store
/// <c>Status</c> stays null) so it resumes on the next start; a clean completion → <c>null</c>
/// (trust the rows). An ADMINISTRATIVE cancel — distinct from a host stop — still ends the run
/// <see cref="JobStatus.Cancelled"/> (pinned by <c>JobRunnerBatchRunIntegrationTests</c>).
/// </summary>
public class JobRunnerTerminalStatusClassificationTests
{
    private static readonly TimeSpan SignalDeadline = TimeSpan.FromSeconds(60);

    public sealed class OkJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// A job that signals it started, then parks on the host cancellation token until the host shuts down,
    /// so a run can be caught mid-local-job at a graceful host stop.
    /// </summary>
    public sealed class ParkingJob : IJob
    {
        public static TaskCompletionSource Entered { get; private set; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static void Reset() => Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Entered.TrySetResult();
            // Park until the host stop cancels this token (raises OperationCanceledException). Models work
            // that is still in-flight when a graceful shutdown begins.
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
        }
    }

    private static IBatchCompletionEvents ResolveSignal(IServiceProvider sp)
    {
        var signalType = typeof(IJobRunner).Assembly.GetType("UKBatch.Runtime.BatchCompletionSignal")
            ?? throw new InvalidOperationException("BatchCompletionSignal type not found.");
        return (IBatchCompletionEvents)sp.GetRequiredService(signalType);
    }

    /// <summary>Reads the completion payload for <paramref name="batchId"/>, with a deadlock backstop.</summary>
    private static async Task<BatchCompletionSignalPayload?> ReadPayloadAsync(IServiceProvider sp, string batchId, CancellationToken ct)
    {
        var signal = ResolveSignal(sp);
        await foreach (var payload in signal.CompletedBatchRunIds.ReadAllAsync(ct).ConfigureAwait(false))
        {
            if (payload.BatchRunId == batchId)
            {
                return payload;
            }
        }
        return null;
    }

    private static async Task<string> WaitForPendingGateAsync(IServiceProvider sp)
    {
        var approvals = sp.GetRequiredService<IApprovalGateService>();
        IReadOnlyList<PendingApproval> pending = Array.Empty<PendingApproval>();
        var found = await Waits.ForAsync(async () =>
        {
            pending = await approvals.ListPendingAsync(null, default).ConfigureAwait(false);
            return pending.Count > 0;
        }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
        found.Should().BeTrue("the approval gate must register as pending.");
        return pending[0].ApprovalId;
    }

    [Fact]
    public async Task RejectedGate_StopOnFailure_SignalsRuntimeTerminalFailed()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkJob>();
            b.AddBatch("classify.reject.stop", x => x
                .RunJob<OkJob>()
                .ThenWaitForApproval("Confirm", new[] { "ops" })
                .ThenRunJob<OkJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("classify.reject.stop")!;
            var approvals = host.Services.GetRequiredService<IApprovalGateService>();

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);
            var approvalId = await WaitForPendingGateAsync(host.Services).ConfigureAwait(false);
            await approvals.RejectAsync(approvalId, new ApproverContext { Identity = "ops@x", Roles = new[] { "ops" } }, "rejected", default).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(SignalDeadline);
            var payload = await ReadPayloadAsync(host.Services, batchId, cts.Token).ConfigureAwait(false);

            payload.Should().NotBeNull("the runtime must signal completion (60s deadlock backstop).");
            payload!.RuntimeTerminalStatus.Should().Be(JobStatus.Failed,
                "a rejected gate rethrows out of RunAsync, so the closure classifies the run as Failed.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task HostStopWhileParkedOnGate_LeavesRunInFlight_DoesNotFinalize()
    {
        // A held gate (no timeout) parks the run until cancellation. Stopping the host cancels
        // ApplicationStopping → the gate resolves Cancelled → AwaitApprovalAsync throws
        // OperationCanceledException. Because the PARENT host-stopping token is cancelled, the closure
        // recognises a graceful host shutdown (not an administrative cancel) and LEAVES THE RUN IN-FLIGHT:
        // no terminal verdict on the signal, and the run-store Status stays null so the next host's
        // recovery resumes it.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkJob>();
            b.AddBatch("classify.hoststop", x => x
                .RunJob<OkJob>()
                .ThenWaitForApproval("Hold", new[] { "ops" })   // OnTimeout defaults to Hold, no timeout → parks
                .ThenRunJob<OkJob>());
        }).ConfigureAwait(false);

        var runner = host.Services.GetRequiredService<IJobRunner>();
        var runStore = host.Services.GetRequiredService<IBatchRunStore>();
        var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("classify.hoststop")!;

        var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);
        await WaitForPendingGateAsync(host.Services).ConfigureAwait(false);

        // Start reading BEFORE the stop so the post-shutdown signal is never missed.
        using var cts = new CancellationTokenSource(SignalDeadline);
        var readTask = ReadPayloadAsync(host.Services, batchId, cts.Token);

        // Cancels ApplicationStopping → the parked gate unblocks; the closure leaves the run in-flight.
        await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);

        var payload = await readTask.ConfigureAwait(false);
        payload.Should().NotBeNull("the run must still signal the in-process run is over after host stop (60s deadlock backstop).");
        payload!.RuntimeTerminalStatus.Should().BeNull(
            "a graceful host shutdown leaves the run in-flight (no terminal verdict), so recovery can resume it.");

        // THE regression lock: the run-store record is NOT finalized — Status stays null (in-flight) so the
        // next host's DurableRunRecovery resumes it instead of skipping a terminal Cancelled record.
        var run = await runStore.GetAsync(batchId, CancellationToken.None).ConfigureAwait(false);
        run.Should().NotBeNull("the run record was created at trigger time.");
        run!.Status.Should().BeNull(
            "graceful host shutdown must leave the run in-progress (Status null), NOT finalize it Cancelled.");
        run.CompletedAtUtc.Should().BeNull("an in-flight run has no completion time.");
    }

    [Fact]
    public async Task HostStop_WhileMidLocalJob_LeavesRunInFlight()
    {
        // The same discrimination for a run caught mid-LOCAL-JOB (not parked on a gate): a graceful host
        // stop interrupts the running job with OCE; because the host-stopping token is cancelled, the run
        // is left in-flight (Status null), not finalized Cancelled.
        ParkingJob.Reset();
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<ParkingJob>();
            b.AddBatch("classify.hoststop.job", x => x.RunJob<ParkingJob>());
        }).ConfigureAwait(false);

        var runner = host.Services.GetRequiredService<IJobRunner>();
        var runStore = host.Services.GetRequiredService<IBatchRunStore>();
        var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("classify.hoststop.job")!;

        var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);
        // Wait until the job is actually executing, so the host stop genuinely interrupts it mid-step.
        await ParkingJob.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

        using var cts = new CancellationTokenSource(SignalDeadline);
        var readTask = ReadPayloadAsync(host.Services, batchId, cts.Token);

        await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);

        var payload = await readTask.ConfigureAwait(false);
        payload.Should().NotBeNull("the run must signal the in-process run is over after host stop (60s deadlock backstop).");
        payload!.RuntimeTerminalStatus.Should().BeNull(
            "a graceful host shutdown mid-local-job leaves the run in-flight, not Cancelled.");

        var run = await runStore.GetAsync(batchId, CancellationToken.None).ConfigureAwait(false);
        run!.Status.Should().BeNull(
            "graceful host shutdown mid-local-job must leave the run in-progress (Status null) for recovery.");
    }

    [Fact]
    public async Task CleanCompletion_SignalsNullRuntimeTerminal()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkJob>();
            b.AddBatch("classify.clean", x => x.RunJob<OkJob>());
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("classify.clean")!;

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            using var cts = new CancellationTokenSource(SignalDeadline);
            var payload = await ReadPayloadAsync(host.Services, batchId, cts.Token).ConfigureAwait(false);

            payload.Should().NotBeNull("the runtime must signal completion (60s deadlock backstop).");
            payload!.RuntimeTerminalStatus.Should().BeNull(
                "a clean completion carries no verdict — the hub fan-out trusts the rows.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }
}
