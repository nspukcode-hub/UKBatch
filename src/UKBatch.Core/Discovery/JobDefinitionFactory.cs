using Polly;
using Polly.Retry;
using UKBatch.Abstractions.Jobs;
using UKBatch.Abstractions.Models;

namespace UKBatch.Discovery;

/// <summary>
/// Builds <see cref="JobDefinition"/> records from raw inputs (attribute discovery or fluent
/// configuration) and the cached per-item retry <see cref="ResiliencePipeline"/> used by
/// partitioned jobs / <c>ChannelFanout</c> when the item error policy is <see cref="ItemErrorPolicy.RetryThenContinue"/>.
/// </summary>
/// <remarks>
/// <para><see cref="JobDefinition.DefaultParameters"/> is defensive-copied here, so
/// later <c>JobParameters.WrapWithoutCopy</c> use on the scheduler hot path is safe by construction.</para>
/// <para>The per-item Polly pipeline is built once per JobDefinition at registration
/// time and cached in <see cref="Registry.JobDefinitionRegistry"/>; per-item code reuses the same instance.</para>
/// </remarks>
internal static class JobDefinitionFactory
{
    /// <summary>
    /// Builds a <see cref="JobDefinition"/> record. The provided <paramref name="defaultParameters"/>
    /// dictionary is defensive-copied so caller mutation is harmless.
    /// </summary>
    public static JobDefinition Build(
        string name,
        Type implementationType,
        bool isPartitioned,
        string? schedule,
        int maxRetries,
        int timeoutSeconds,
        int partitionWorkerCount,
        ItemErrorPolicy itemErrorPolicy,
        IReadOnlyDictionary<string, object?>? defaultParameters,
        IReadOnlyList<string>? tags)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(implementationType);

        var copiedParams = defaultParameters is null || defaultParameters.Count == 0
            ? (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(0, StringComparer.Ordinal)
            : new Dictionary<string, object?>(defaultParameters, StringComparer.Ordinal);

        var copiedTags = tags is null || tags.Count == 0
            ? (IReadOnlyList<string>)Array.Empty<string>()
            : tags.ToArray();

        return new JobDefinition
        {
            Name = name,
            ImplementationTypeName = implementationType.AssemblyQualifiedName,
            IsPartitioned = isPartitioned,
            Schedule = schedule,
            MaxRetries = maxRetries,
            TimeoutSeconds = timeoutSeconds,
            PartitionWorkerCount = partitionWorkerCount,
            ItemErrorPolicy = itemErrorPolicy,
            DefaultParameters = copiedParams,
            Tags = copiedTags,
            SourceService = null,
        };
    }

    /// <summary>
    /// Builds the per-item retry <see cref="ResiliencePipeline"/> used by <c>ChannelFanout</c>
    /// when the policy is <see cref="ItemErrorPolicy.RetryThenContinue"/>. Built once per
    /// JobDefinition; cached in the registry.
    /// </summary>
    public static ResiliencePipeline BuildItemRetryPipeline(JobDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);
        return new ResiliencePipelineBuilder()
            .AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = Math.Max(0, def.MaxRetries),
                BackoffType = DelayBackoffType.Exponential,
                Delay = TimeSpan.FromMilliseconds(100),
                UseJitter = true,
            })
            .Build();
    }
}
