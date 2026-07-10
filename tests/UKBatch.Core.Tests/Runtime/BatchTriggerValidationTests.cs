using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Runtime;
using UKBatch.Abstractions.Storage;
using UKBatch.Core.Tests.Helpers;
using UKBatch.Runtime;
using Xunit;

namespace UKBatch.Core.Tests.Runtime;

/// <summary>
/// A batch trigger runs a synchronous pre-flight: a batch referencing an UNREGISTERED local job (or
/// a structurally invalid definition) throws <see cref="BatchTriggerValidationException"/> before the
/// fire-and-forget run, so a trigger endpoint can return 400 with the errors instead of accepting a
/// trigger that would produce zero executions. Cross-service steps (a remote job) are skipped by the
/// registration check, and a fully valid batch triggers normally.
/// </summary>
public class BatchTriggerValidationTests
{
    public sealed class RegisteredJob : IJob
    {
        public static readonly TaskCompletionSource Ran = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task ExecuteAsync(JobContext context, CancellationToken cancellationToken)
        {
            Ran.TrySetResult();
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TriggerBatch_UnregisteredLocalJob_ThrowsValidationWithErrors()
    {
        var host = await TestHostBuilder.StartAsync(b =>
            b.AddBatch("trigger.unregistered", x => x.RunJob("ghost.job"))).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.unregistered")!;

            var act = async () => await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            var thrown = await act.Should().ThrowAsync<BatchTriggerValidationException>().ConfigureAwait(false);
            thrown.Which.Errors.Should().NotBeEmpty();
            thrown.Which.Errors.Should().Contain(e => e.Message.Contains("ghost.job"),
                "the validation error must name the unregistered job.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerBatch_CrossServiceStep_NotLocallyRegistered_DoesNotThrowPreflight()
    {
        // A cross-service step targets a remote worker, so its job is NOT in this process's registry;
        // the pre-flight must skip it. The fire-and-forget run may then fail asynchronously (no real
        // transport here), but the synchronous trigger must NOT throw a validation error.
        var host = await TestHostBuilder.StartAsync(b =>
            b.AddBatch("trigger.crossservice", x => x.RunJob("RemoteOnly", step => step.OnService("billing-worker")))).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.crossservice")!;

            var act = async () => await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            await act.Should().NotThrowAsync<BatchTriggerValidationException>().ConfigureAwait(false);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerBatch_AllRegistered_TriggersAndRuns()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RegisteredJob>();
            b.AddBatch("trigger.valid", x => x.RunJob<RegisteredJob>());
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.valid")!;

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            batchId.Should().NotBeNullOrEmpty("a valid batch triggers and returns a run id.");
            await RegisteredJob.Ran.Task.WaitAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
            RegisteredJob.Ran.Task.IsCompletedSuccessfully.Should().BeTrue("the run proceeds for an all-registered batch.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    // ===== per-step compensator registration pre-flight =====

    [Fact]
    public async Task TriggerBatch_UnregisteredLocalCompensator_ThrowsNamingStepAndJob()
    {
        // A LOCAL compensator naming an unregistered job would otherwise only surface when the run FAILS
        // and unwinds — the worst moment to discover a typo. The pre-flight must reject the trigger.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RegisteredJob>();
            b.AddBatch("trigger.comp.unregistered", x => x
                .RunJob<RegisteredJob>(s => s.CompensateWith("ghost.comp")));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.comp.unregistered")!;
            var stepId = def.Steps[0].StepId;

            var act = async () => await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            var thrown = await act.Should().ThrowAsync<BatchTriggerValidationException>().ConfigureAwait(false);
            thrown.Which.Errors.Should().Contain(
                e => e.Path.Contains(stepId) && e.Path.Contains("compensator") && e.Message.Contains("ghost.comp"),
                "the validation error must attribute the unregistered compensator to its parent step.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerBatch_CrossServiceCompensator_NotLocallyRegistered_DoesNotThrowPreflight()
    {
        // A cross-service compensator targets a remote worker, so its job is NOT in this process's
        // registry; the pre-flight must skip it, mirroring the cross-service main-step rule.
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RegisteredJob>();
            b.AddBatch("trigger.comp.crossservice", x => x
                .RunJob<RegisteredJob>(s => s.CompensateWith("RemoteComp", c => c.OnService("billing-worker"))));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.comp.crossservice")!;

            var act = async () => await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            await act.Should().NotThrowAsync<BatchTriggerValidationException>().ConfigureAwait(false);
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }

    [Fact]
    public async Task TriggerBatch_RegisteredLocalCompensator_Passes()
    {
        var host = await TestHostBuilder.StartAsync(b =>
        {
            b.AddJob<RegisteredJob>();
            b.AddBatch("trigger.comp.registered", x => x
                .RunJob<RegisteredJob>(s => s.CompensateWith<RegisteredJob>()));
        }).ConfigureAwait(false);
        try
        {
            var runner = host.Services.GetRequiredService<IJobRunner>();
            var def = host.Services.GetRequiredService<IBatchDefinitionLookup>().TryGetByName("trigger.comp.registered")!;

            var batchId = await runner.TriggerBatchAsync(def.Id, null, "test", default).ConfigureAwait(false);

            batchId.Should().NotBeNullOrEmpty("a batch whose compensator is locally registered triggers normally.");
        }
        finally
        {
            await TestHostBuilder.StopGracefullyAsync(host).ConfigureAwait(false);
        }
    }
}
