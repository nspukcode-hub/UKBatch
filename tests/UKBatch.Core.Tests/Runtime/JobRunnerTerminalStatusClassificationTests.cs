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
/// gate (StopOnFailure) rethrows out of RunAsync → <see cref="JobStatus.Failed"/>; a host-stop while the
/// run is parked on a gate → <see cref="JobStatus.Cancelled"/>; a clean completion → <c>null</c>
/// (trust the rows).
/// </summary>
public class JobRunnerTerminalStatusClassificationTests
{
    private static readonly TimeSpan SignalDeadline = TimeSpan.FromSeconds(60);

    public sealed class OkJob : IJob
    {
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken) => Task.CompletedTask;
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
    public async Task HostStopWhileParkedOnGate_SignalsRuntimeTerminalCancelled()
    {
        // A held gate (no timeout) parks the run until cancellation. Stopping the host cancels
        // ApplicationStopping → the gate resolves Cancelled → AwaitApprovalAsync throws
        // OperationCanceledException → the closure's OCE catch classifies the run as Cancelled.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<OkJob>();
            b.AddBatch("classify.hoststop", x => x
                .RunJob<OkJob>()
                .ThenWaitForApproval("Hold", new[] { "ops" })   // OnTimeout defaults to Hold, no timeout → parks
                .ThenRunJob<OkJob>());
        }).ConfigureAwait(false);

        var runner = host.Services.GetRequiredService<IJobRunner>();
        var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("classify.hoststop")!;

        var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);
        await WaitForPendingGateAsync(host.Services).ConfigureAwait(false);

        // Start reading BEFORE the stop so the post-cancellation signal is never missed.
        using var cts = new CancellationTokenSource(SignalDeadline);
        var readTask = ReadPayloadAsync(host.Services, batchId, cts.Token);

        // Cancels ApplicationStopping → the parked gate unblocks as Cancelled.
        await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);

        var payload = await readTask.ConfigureAwait(false);
        payload.Should().NotBeNull("the run must signal completion after host stop (60s deadlock backstop).");
        payload!.RuntimeTerminalStatus.Should().Be(JobStatus.Cancelled,
            "host stop cancels the parked gate, so the closure classifies the run as Cancelled, not Failed.");
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
