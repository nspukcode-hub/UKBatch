using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Polly;
using UKBatch.Abstractions.Jobs;
using UKBatch.Registry;

namespace UKBatch.Discovery;

/// <summary>
/// Scans loaded assemblies (plus any extras configured via
/// <see cref="UKBatchOptions.AdditionalAssembliesToScan"/>) for types decorated with
/// <see cref="JobAttribute"/> implementing <see cref="IJob"/> or <see cref="IPartitionedJob{TItem}"/>,
/// and registers them into the supplied <see cref="JobDefinitionRegistry"/> + DI container.
/// </summary>
/// <remarks>
/// Runs at <c>AddUKBatch</c> time, NOT at host start, because the discovered types must be
/// resolvable from the DI container before <c>StartAsync</c> opens the per-execution scopes.
/// </remarks>
internal static class AttributeJobDiscovery
{
    /// <summary>Discovers and registers every <c>[Job]</c>-decorated job type. Idempotent across calls.</summary>
    public static void DiscoverAndRegister(
        IServiceCollection services,
        JobDefinitionRegistry registry,
        UKBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(options);

        var scanned = new HashSet<Assembly>();
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            scanned.Add(asm);
        }
        foreach (var asm in options.AdditionalAssembliesToScan)
        {
            scanned.Add(asm);
        }

        // Types already registered (typically explicitly, through the builder) keep ONLY that
        // registration. The builder may have renamed the job, so the name-based check inside the
        // loop cannot see such a registration — re-registering the type here would resurrect the
        // attribute's defaults under a second name, and a [Job(Schedule = ...)] job would then be
        // armed twice and fire twice per cron occurrence.
        var registeredTypes = new HashSet<Type>();
        foreach (var existing in registry.All())
        {
            if (registry.TryGetImplementationType(existing.Name) is { } impl)
            {
                registeredTypes.Add(impl);
            }
        }

        foreach (var asm in scanned)
        {
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException tlex)
            {
                types = tlex.Types.OfType<Type>().ToArray();
            }

            foreach (var type in types)
            {
                if (type is null || type.IsAbstract || type.IsInterface)
                {
                    continue;
                }
                var attr = type.GetCustomAttribute<JobAttribute>(inherit: false);
                if (attr is null)
                {
                    continue;
                }

                var isPartitioned = IsPartitionedJob(type);
                if (!isPartitioned && !typeof(IJob).IsAssignableFrom(type))
                {
                    // Has [Job] but neither IJob nor IPartitionedJob<T> — skip silently.
                    continue;
                }

                if (registeredTypes.Contains(type))
                {
                    // The implementation type is already registered (under whatever name the
                    // builder chose) — the explicit registration wins.
                    continue;
                }

                if (registry.TryGet(attr.Name ?? type.FullName ?? type.Name) is not null)
                {
                    // Already registered under the same name — skip to avoid duplicate.
                    continue;
                }

                var jobName = attr.Name ?? type.FullName ?? type.Name;
                var def = JobDefinitionFactory.Build(
                    name: jobName,
                    implementationType: type,
                    isPartitioned: isPartitioned,
                    schedule: attr.Schedule,
                    maxRetries: attr.MaxRetries ?? options.DefaultMaxRetries,
                    timeoutSeconds: attr.TimeoutSeconds ?? options.DefaultTimeoutSeconds,
                    partitionWorkerCount: isPartitioned
                        ? (attr.PartitionWorkerCount > 0 ? attr.PartitionWorkerCount : options.DefaultPartitionWorkerCount)
                        : 0,
                    itemErrorPolicy: isPartitioned ? attr.ItemErrorPolicy : ItemErrorPolicy.FailFast,
                    defaultParameters: null,   // no attribute parity for default parameters (a dictionary is not a legal attribute argument)
                    tags: attr.Tags);

                // Fail-fast on an invalid cron in [Job(Schedule = "...")] — a programmer error, surfaced at
                // AddUKBatch time exactly like the fluent JobBuilder path (rather than crashing host startup).
                if (!string.IsNullOrEmpty(def.Schedule))
                {
                    try
                    {
                        _ = Cronos.CronExpression.Parse(def.Schedule, options.CronFormat);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException(
                            $"Invalid cron expression '{def.Schedule}' for job '{jobName}' against CronFormat={options.CronFormat}: {ex.Message}",
                            ex);
                    }
                }

                // Attribute-discovered partitioned jobs honour ItemErrorPolicy. The per-item retry
                // pipeline is built only for RetryThenContinue with a positive retry budget, exactly
                // as the fluent builder does — every other policy (and every non-partitioned job)
                // registers with no pipeline.
                ResiliencePipeline? itemPipeline =
                    def.IsPartitioned && def.ItemErrorPolicy == ItemErrorPolicy.RetryThenContinue && def.MaxRetries >= 1
                        ? JobDefinitionFactory.BuildItemRetryPipeline(def)
                        : null;
                registry.Register(def, type, itemRetryPipeline: itemPipeline);

                services.AddScoped(type);
            }
        }
    }

    // Only the partitioned/plain distinction matters here: JobDefinition carries no item type,
    // and the partitioned runtime resolves TItem from the implementation instance at dispatch.
    private static bool IsPartitionedJob(Type type)
        => type.GetInterfaces()
            .Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPartitionedJob<>));
}
