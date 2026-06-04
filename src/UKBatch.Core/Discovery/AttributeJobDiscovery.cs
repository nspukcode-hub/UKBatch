using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
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

                var (isPartitioned, partitionedItemType) = ResolveJobShape(type);
                if (!isPartitioned && !typeof(IJob).IsAssignableFrom(type))
                {
                    // Has [Job] but neither IJob nor IPartitionedJob<T> — skip silently.
                    continue;
                }

                if (registry.TryGet(attr.Name ?? type.FullName ?? type.Name) is not null)
                {
                    // Already explicitly registered via the builder — skip to avoid duplicate.
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
                    partitionWorkerCount: isPartitioned ? options.DefaultPartitionWorkerCount : 0,
                    itemErrorPolicy: ItemErrorPolicy.FailFast,
                    defaultParameters: null,
                    tags: attr.Tags);

                var pipeline = def.ItemErrorPolicy == ItemErrorPolicy.RetryThenContinue
                    ? JobDefinitionFactory.BuildItemRetryPipeline(def)
                    : null;
                registry.Register(def, type, pipeline);

                services.AddScoped(type);
                _ = partitionedItemType;
            }
        }
    }

    private static (bool IsPartitioned, Type? ItemType) ResolveJobShape(Type type)
    {
        var partitioned = type.GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IPartitionedJob<>));
        if (partitioned is not null)
        {
            return (true, partitioned.GetGenericArguments()[0]);
        }
        return (false, null);
    }
}
