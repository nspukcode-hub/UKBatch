using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;

namespace UKBatch.Builders;

/// <summary>Per-job-step options inside a batch (overrides for the job's defaults).</summary>
public sealed class JobStepBuilder
{
    internal int? MaxRetries { get; private set; }
    internal int? TimeoutSeconds { get; private set; }
    internal IReadOnlyDictionary<string, object?>? Parameters { get; private set; }
    internal string? TargetService { get; private set; }
    internal CompensationStepData? Compensation { get; private set; }

    // Set on the INNER builder that configures a compensator, so attaching a compensator to a
    // compensator fails fast (a saga unwind must be acyclic — there is no compensation of compensation).
    private bool _isCompensator;

    /// <summary>Overrides the job's max retries for this step.</summary>
    public JobStepBuilder WithMaxRetries(int maxRetries)
    {
        if (maxRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxRetries), maxRetries, "must be >= 0");
        }
        MaxRetries = maxRetries;
        return this;
    }

    /// <summary>Overrides the job's timeout for this step (in seconds; 0 = no timeout).</summary>
    public JobStepBuilder WithTimeout(int timeoutSeconds)
    {
        if (timeoutSeconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutSeconds), timeoutSeconds, "must be >= 0");
        }
        TimeoutSeconds = timeoutSeconds;
        return this;
    }

    /// <summary>Sets static parameters for this step (defensive-copied).</summary>
    public JobStepBuilder WithParameters(IReadOnlyDictionary<string, object?> parameters)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        Parameters = new Dictionary<string, object?>(parameters, StringComparer.Ordinal);
        return this;
    }

    /// <summary>Specifies the target service for cross-service jobs (worker mode).</summary>
    public JobStepBuilder OnService(string targetService)
    {
        ArgumentException.ThrowIfNullOrEmpty(targetService);
        TargetService = targetService;
        return this;
    }

    /// <summary>
    /// Attaches a compensator job to this step: the job that undoes this step's work when a LATER step
    /// fails and the batch's failure policy is <c>Compensate</c>. Compensators run in reverse order of
    /// the completed steps. <paramref name="configure"/> receives an inner builder for the compensator's
    /// own parameters, retries, timeout, and target service.
    /// </summary>
    public JobStepBuilder CompensateWith<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
        => SetCompensation(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>
    /// Attaches a compensator by job name. Use for cross-service compensators (pair with
    /// <c>c.OnService(...)</c> in <paramref name="configure"/>) or when the job type is not referenceable.
    /// </summary>
    public JobStepBuilder CompensateWith(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return SetCompensation(jobName, configure);
    }

    /// <summary>
    /// Attaches a partitioned-job compensator by type. Partitioned jobs implement
    /// <see cref="IPartitionedJob{TItem}"/>, so the <see cref="IJob"/>-constrained
    /// <see cref="CompensateWith{TJob}"/> cannot accept them — this is the typed counterpart for
    /// data-parallel compensators.
    /// </summary>
    public JobStepBuilder CompensateWithPartitioned<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => SetCompensation(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    private JobStepBuilder SetCompensation(string jobName, Action<JobStepBuilder>? configure)
    {
        if (_isCompensator)
        {
            throw new InvalidOperationException("A compensator cannot itself have a compensator.");
        }
        Compensation = BuildCompensationData(jobName, configure);
        return this;
    }

    /// <summary>
    /// Builds a <see cref="CompensationStepData"/> from an inner compensator-scoped builder. Shared with
    /// the group-level compensator overloads so the inner builder is always compensator-scoped (nested
    /// compensators fail fast everywhere).
    /// </summary>
    internal static CompensationStepData BuildCompensationData(string jobName, Action<JobStepBuilder>? configure)
    {
        var inner = new JobStepBuilder { _isCompensator = true };
        configure?.Invoke(inner);
        return new CompensationStepData
        {
            JobName = jobName,
            TargetService = inner.TargetService,
            Parameters = inner.Parameters,
            MaxRetries = inner.MaxRetries,
            TimeoutSeconds = inner.TimeoutSeconds,
        };
    }
}
