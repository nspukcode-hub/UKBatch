using System.Diagnostics;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Runtime;

namespace UKBatch.AspNetCore.Triggering;

/// <summary>
/// Extension methods on <see cref="IJobRunner"/> that resolve the request identity via
/// <see cref="IJobTriggerContext"/>, snapshot the ambient <see cref="Activity"/>, and forward
/// to the underlying trigger.
/// </summary>
public static class JobRunnerHttpContextExtensions
{
    /// <summary>
    /// Triggers a job, populating <see cref="JobExecution.TriggeredBy"/> from the current request
    /// context. Snapshots <see cref="Activity.Current"/> BEFORE awaiting
    /// <see cref="IJobRunner.TriggerAsync"/> and stashes it under the returned execution id for later
    /// <c>JobContext.RestoreRequestActivity()</c> consumption.
    /// </summary>
    public static async Task<JobExecution> TriggerWithRequestContextAsync(
        this IJobRunner runner,
        IJobTriggerContext triggerContext,
        IJobTraceContext traceContext,
        string jobName,
        JobParameters parameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(triggerContext);
        ArgumentNullException.ThrowIfNull(traceContext);
        var triggeredBy = triggerContext.GetTriggeredByOrNull();
        // Snapshot the Activity BEFORE awaiting. The continuation may run on a thread where
        // Activity.Current differs or has already been stopped.
        var captured = Activity.Current;
        var execution = await runner
            .TriggerAsync(jobName, parameters, triggeredBy, cancellationToken)
            .ConfigureAwait(false);
        traceContext.CaptureActivity(execution.ExecutionId, captured);
        return execution;
    }

    /// <summary>
    /// Triggers a batch, populating <see cref="JobExecution.TriggeredBy"/> on every child execution
    /// from the current request context. The single Activity snapshot is stored under the returned
    /// batch id key.
    /// </summary>
    public static async Task<string> TriggerBatchWithRequestContextAsync(
        this IJobRunner runner,
        IJobTriggerContext triggerContext,
        IJobTraceContext traceContext,
        string batchDefinitionId,
        JobParameters? initialParameters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(triggerContext);
        ArgumentNullException.ThrowIfNull(traceContext);
        var triggeredBy = triggerContext.GetTriggeredByOrNull();
        // Snapshot the Activity BEFORE awaiting.
        var captured = Activity.Current;
        var batchId = await runner
            .TriggerBatchAsync(batchDefinitionId, initialParameters, triggeredBy, cancellationToken)
            .ConfigureAwait(false);
        traceContext.CaptureActivity(batchId, captured);
        return batchId;
    }
}
