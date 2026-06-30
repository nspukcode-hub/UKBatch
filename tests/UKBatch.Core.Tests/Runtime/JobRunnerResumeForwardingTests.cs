using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// End-to-end durable-resume forwarding through <c>IJobRunner.ResumeBatchAsync</c>. A run interrupted after
/// step 0 carries its forwarded state (the batch-initial parameters and step 0's accumulated outputs) under
/// reserved keys on <see cref="BatchRun.ForwardedState"/>. On resume, step 1 must see BOTH: the batch-initial
/// parameters restored (the fix for the prior bug where a resumed run got <see cref="JobParameters.Empty"/>)
/// and step 0's output forwarded forward.
/// </summary>
public class JobRunnerResumeForwardingTests
{
    /// <summary>Captures the parameters each resumed step received, keyed by step id.</summary>
    private sealed class CapturingStepJob : IJob
    {
        public static readonly ConcurrentDictionary<string, JobParameters> ByStep = new(StringComparer.Ordinal);
        public static void Reset() => ByStep.Clear();
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            ByStep[context.BatchStepId ?? context.JobName] = context.Parameters;
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

    private static async Task<IHost> StartTwoStepHostAsync(string batchName)
    {
        CapturingStepJob.Reset();
        return await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<CapturingStepJob>();
            b.AddBatch(batchName, x => x
                .RunJob<CapturingStepJob>()
                .ThenRunJob<CapturingStepJob>()
                .FailurePolicy(BatchFailurePolicy.StopOnFailure));
        });
    }

    /// <summary>Seeds an in-progress run (Status null) at the given cursor with the supplied forwarded state.</summary>
    private static async Task SeedInProgressRunWithStateAsync(
        IBatchRunStore runStore, string runId, BatchDefinition def, int cursor,
        IReadOnlyDictionary<string, object?> forwardedState)
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
        await runStore.UpdateForwardedStateAsync(runId, forwardedState, CancellationToken.None);
    }

    /// <summary>Inserts a terminal Completed shadow row for step 0 so the resumed run aggregates cleanly.</summary>
    private static Task SeedCompletedStep0RowAsync(IJobStore jobStore, string runId, string step0Id)
    {
        var internalStore = (IJobStoreInternal)jobStore;
        return internalStore.InsertAsync(new JobExecution
        {
            ExecutionId = Guid.NewGuid().ToString("N"),
            JobName = "SeededStep0",
            BatchId = runId,
            BatchStepId = step0Id,
            BatchDefinitionId = null,
            Status = JobStatus.Completed,
            Parameters = new Dictionary<string, object?>(),
            EnqueuedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            AttemptNumber = 1,
            MaxRetries = 0,
            LastError = null,
            Processed = 0,
            Failed = 0,
            Total = null,
            TriggeredBy = "tester",
            WorkerName = null,
        }, CancellationToken.None);
    }

    [Fact]
    public async Task ResumeForward_RestoresInitialParameters_AndForwardsStep0Output()
    {
        var host = await StartTwoStepHostAsync("fwd.resume.e2e");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("fwd.resume.e2e")!;
            var step0Id = def.Steps[0].StepId;
            var step1Id = def.Steps[1].StepId;

            // Forwarded state as it would be persisted after step 0: the batch-initial parameters and the
            // accumulated outputs, under the reserved keys.
            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] = new Dictionary<string, object?> { ["region"] = "EU" },
                [ForwardedStateKeys.ForwardedOutputs] = new Dictionary<string, object?> { ["orderId"] = 5 },
            };

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunWithStateAsync(runStore, runId, def, cursor: 1, forwardedState);
            await SeedCompletedStep0RowAsync(jobStore, runId, step0Id);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            var step1Params = CapturingStepJob.ByStep[step1Id];
            step1Params.GetRequired<string>("region").Should().Be("EU",
                "a resumed run restores the batch-initial parameters from the forwarded state (not Empty)");
            step1Params.GetRequired<int>("orderId").Should().Be(5,
                "step 0's output is forwarded into the resumed step's parameters");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeForward_OnlyInitialParametersRecorded_StepSeesInitial()
    {
        // A crash BEFORE the first step completed leaves only the batch-initial parameters recorded (the
        // create-time write), no accumulated outputs. A RestartAll-style replay from step 0 must still see
        // those initial parameters — the headline of the Empty-bug fix.
        var host = await StartTwoStepHostAsync("fwd.resume.initialonly");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("fwd.resume.initialonly")!;
            var step0Id = def.Steps[0].StepId;

            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] = new Dictionary<string, object?> { ["tenant"] = "acme" },
            };

            var runId = Guid.NewGuid().ToString("N");
            // Cursor 0 → nothing completed yet → ResumeForward replays from step 0.
            await SeedInProgressRunWithStateAsync(runStore, runId, def, cursor: 0, forwardedState);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            CapturingStepJob.ByStep[step0Id].GetRequired<string>("tenant").Should().Be("acme",
                "the batch-initial parameters are rehydrated on resume even with no accumulated outputs");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }

    [Fact]
    public async Task ResumeForward_JsonElementValuedForwardedState_ForwardsTypedValue()
    {
        // After a real restart on a JSON-backed store, ForwardedState values come back as JsonElement, not
        // live CLR dictionaries. This drives a resume off a JsonElement-valued ForwardedState all the way to a
        // typed GetRequired<int>/<string> on the resumed step — exercising the AsDict JsonElement branch and
        // the JSON-aware JobParameters read end to end (the round-trip the in-memory-seeded tests skip).
        var host = await StartTwoStepHostAsync("fwd.resume.jsonelement");
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var runStore = host.Services.GetRequiredService<IBatchRunStore>();
            var jobStore = host.Services.GetRequiredService<IJobStore>();
            var lookup = host.Services.GetRequiredService<IBatchDefinitionLookup>();
            var def = lookup.TryGetByName("fwd.resume.jsonelement")!;
            var step0Id = def.Steps[0].StepId;
            var step1Id = def.Steps[1].StepId;

            // Reserved-key values as JsonElement, exactly as a JSON store deserializes them on restart.
            var forwardedState = new Dictionary<string, object?>
            {
                [ForwardedStateKeys.InitialParameters] =
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["region"] = "EU" }),
                [ForwardedStateKeys.ForwardedOutputs] =
                    JsonSerializer.SerializeToElement(new Dictionary<string, object?> { ["orderId"] = 5 }),
            };

            var runId = Guid.NewGuid().ToString("N");
            await SeedInProgressRunWithStateAsync(runStore, runId, def, cursor: 1, forwardedState);
            await SeedCompletedStep0RowAsync(jobStore, runId, step0Id);

            await runner.ResumeBatchAsync(runId, ResumePolicy.ResumeForward, CancellationToken.None);

            var run = await AwaitRunTerminalAsync(runStore, runId);
            run.Status.Should().Be(JobStatus.Completed);

            var step1Params = CapturingStepJob.ByStep[step1Id];
            step1Params.GetRequired<string>("region").Should().Be("EU",
                "a JsonElement-valued initial-parameters payload rehydrates and reads back typed on resume");
            step1Params.GetRequired<int>("orderId").Should().Be(5,
                "a JsonElement-valued forwarded output reads back as int on the resumed step");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host);
        }
    }
}
