using System.Globalization;
using Microsoft.Extensions.Logging;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;
using UKBatch.Registry;

namespace UKBatch.Runtime;

/// <summary>
/// Drives an <see cref="IPartitionedJob{TItem}"/> via the shared <see cref="ChannelFanout"/>.
/// Resolves the open generic <c>SourceAsync</c> / <c>ProcessAsync</c> via reflection-free typed
/// helpers — callers pass concrete instances via the static <see cref="RunAsync{TItem}"/>.
/// </summary>
internal static class PartitionedJobRunner
{
    /// <summary>
    /// Reserved job-parameter key for a PER-RUN partition worker-count override. When a
    /// trigger / batch step supplies <c>{ "ukbatch.workers": N }</c>, the run uses N concurrent
    /// consumers instead of the registration-time <c>WithParallelism(...)</c> value. The value crosses
    /// transports as <c>object?</c> (often a <c>JsonElement</c> number or string), so it is parsed via
    /// <c>ToString()</c> + invariant <c>int.TryParse</c>. Invalid / non-positive values fall back to
    /// the registered count (warn-logged); values above <see cref="MaxWorkerOverride"/> are clamped.
    /// Namespaced (<c>ukbatch.</c>) so it cannot collide with a consumer job's own parameters.
    /// </summary>
    internal const string WorkerCountParameterKey = "ukbatch.workers";

    /// <summary>Upper clamp for the per-run override — a typo'd "5000" must not spawn 5000 tasks.</summary>
    internal const int MaxWorkerOverride = 128;

    /// <summary>
    /// Executes a partitioned job end-to-end. Uses the cached per-item Polly pipeline from the
    /// registry when the configured <see cref="ItemErrorPolicy"/> is
    /// <see cref="ItemErrorPolicy.RetryThenContinue"/>. Worker count comes from the registration
    /// (<c>WithParallelism</c>) unless the run carries a <see cref="WorkerCountParameterKey"/> override.
    /// </summary>
    public static async Task RunAsync<TItem>(
        IPartitionedJob<TItem> job,
        JobContext context,
        JobDefinition definition,
        JobDefinitionRegistry registry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);

        var workers = ResolveWorkerCount(definition, context, logger);
        var channelCapacity = Math.Max(1, workers * 32);
        var pipeline = definition.ItemErrorPolicy == ItemErrorPolicy.RetryThenContinue
            ? registry.TryGetItemRetryPipeline(definition.Name)
            : null;
        await ChannelFanout.RunAsync<TItem>(
            job.SourceAsync(context, cancellationToken),
            workers,
            (item, ct) => job.ProcessAsync(item, context, ct),
            definition.ItemErrorPolicy,
            channelCapacity,
            context.Progress,
            pipeline,
            logger,
            cancellationToken).ConfigureAwait(false);

        // Unit-of-work commit point: the fan-out is fully over (every consumer awaited),
        // so the job's optional FinalizeAsync (default no-op) runs exactly once, single-threaded.
        // Deliberately NOT reached when ChannelFanout threw (FailFast / cancellation) — an aborted run
        // must not commit (all-or-nothing; see the IPartitionedJob<TItem>.FinalizeAsync contract).
        await job.FinalizeAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Resolves the effective worker count: the per-run <see cref="WorkerCountParameterKey"/> parameter
    /// when present and valid (clamped to <see cref="MaxWorkerOverride"/>), else the registration-time
    /// <see cref="JobDefinition.PartitionWorkerCount"/>.
    /// </summary>
    private static int ResolveWorkerCount(JobDefinition definition, JobContext context, ILogger logger)
    {
        var registered = Math.Max(1, definition.PartitionWorkerCount);
        if (!context.Parameters.Values.TryGetValue(WorkerCountParameterKey, out var raw) || raw is null)
        {
            return registered;
        }

        // Cross-transport values arrive as object? (JsonElement number/string over the broker) —
        // ToString() + invariant parse covers both.
        if (!int.TryParse(raw.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var requested)
            || requested < 1)
        {
            logger.LogWarning(
                "Partitioned job {Job}: invalid '{Key}' override '{Raw}' — using registered worker count {Registered}.",
                definition.Name, WorkerCountParameterKey, raw, registered);
            return registered;
        }

        var effective = Math.Min(requested, MaxWorkerOverride);
        if (effective != requested)
        {
            logger.LogWarning(
                "Partitioned job {Job}: '{Key}'={Requested} clamped to {Max}.",
                definition.Name, WorkerCountParameterKey, requested, MaxWorkerOverride);
        }

        logger.LogInformation(
            "Partitioned job {Job}: worker count {Workers} (per-run '{Key}' override; registered default {Registered}).",
            definition.Name, effective, WorkerCountParameterKey, registered);
        return effective;
    }
}

/// <summary>
/// Reflection-free dispatcher to <see cref="PartitionedJobRunner.RunAsync{TItem}"/> when the
/// generic item type is only known via reflection (e.g. resolving from the DI container).
/// </summary>
internal static class PartitionedJobDispatcher
{
    /// <summary>
    /// Invokes the strongly-typed <see cref="PartitionedJobRunner.RunAsync{TItem}"/> against the
    /// resolved generic item type.
    /// </summary>
    public static Task DispatchAsync(
        object jobInstance,
        Type itemType,
        JobContext context,
        JobDefinition definition,
        JobDefinitionRegistry registry,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobInstance);
        ArgumentNullException.ThrowIfNull(itemType);
        var openMethod = typeof(PartitionedJobRunner)
            .GetMethod(nameof(PartitionedJobRunner.RunAsync), System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?? throw new InvalidOperationException("PartitionedJobRunner.RunAsync<T> not found.");
        var closedMethod = openMethod.MakeGenericMethod(itemType);
        var task = (Task?)closedMethod.Invoke(null, [jobInstance, context, definition, registry, logger, cancellationToken]);
        return task ?? Task.CompletedTask;
    }
}
