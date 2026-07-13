using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Internal;

namespace UKBatch.Builders;

/// <summary>
/// Sub-builder for <see cref="BatchBuilder.OnFailure"/>; assembles the compensating step list.
/// </summary>
public sealed class OnFailureBuilder
{
    private readonly List<BatchStep> _steps = new();

    /// <summary>Adds a compensating job step.</summary>
    public OnFailureBuilder RunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
    {
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        ThrowIfChainStepHasCompensator(stepBuilder);
        ThrowIfChainStepHasCondition(stepBuilder);
        var jobName = typeof(TJob).FullName ?? typeof(TJob).Name;
        _steps.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _steps.Count,
            StepType = BatchStepType.Job,
            Job = new JobStepData
            {
                JobName = jobName,
                TargetService = stepBuilder.TargetService,
                Parameters = stepBuilder.Parameters,
                MaxRetries = stepBuilder.MaxRetries,
                TimeoutSeconds = stepBuilder.TimeoutSeconds,
            },
            ParallelGroup = null,
            Approval = null,
            Metadata = null,
        });
        return this;
    }

    /// <summary>Alias for <see cref="RunJob{TJob}"/> — semantic continuation in the fluent chain.</summary>
    public OnFailureBuilder ThenRunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
        => RunJob<TJob>(configure);

    /// <summary>
    /// String-name overload for cross-service compensation steps. See
    /// <see cref="BatchBuilder.RunJob(string, Action{JobStepBuilder}?)"/> for semantics.
    /// </summary>
    public OnFailureBuilder RunJob(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        ThrowIfChainStepHasCompensator(stepBuilder);
        ThrowIfChainStepHasCondition(stepBuilder);
        _steps.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _steps.Count,
            StepType = BatchStepType.Job,
            Job = new JobStepData
            {
                JobName = jobName,
                TargetService = stepBuilder.TargetService,
                Parameters = stepBuilder.Parameters,
                MaxRetries = stepBuilder.MaxRetries,
                TimeoutSeconds = stepBuilder.TimeoutSeconds,
            },
            ParallelGroup = null,
            Approval = null,
            Metadata = null,
        });
        return this;
    }

    /// <summary>Semantic alias for <see cref="RunJob(string, Action{JobStepBuilder}?)"/>.</summary>
    public OnFailureBuilder ThenRunJob(string jobName, Action<JobStepBuilder>? configure = null)
        => RunJob(jobName, configure);

    /// <summary>
    /// Adds a partitioned-job compensation step by type. Partitioned jobs implement
    /// <see cref="IPartitionedJob{TItem}"/>, so the <see cref="IJob"/>-constrained
    /// <see cref="RunJob{TJob}"/> cannot accept them — this is the typed counterpart for data-parallel
    /// jobs. The job must be registered via <c>AddPartitionedJob&lt;TJob, TItem&gt;()</c>; the step is
    /// resolved by the job's type name, which matches that registration's default name.
    /// </summary>
    public OnFailureBuilder RunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => RunJob(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>Semantic alias for <see cref="RunPartitionedJob{TJob}"/>.</summary>
    public OnFailureBuilder ThenRunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => RunPartitionedJob<TJob>(configure);

    /// <summary>
    /// A failure-chain step cannot carry its own compensator — the chain already IS the failure
    /// response, and compensating it would recurse (there is no compensation of compensation). Fail
    /// fast at build time rather than letting the validator reject the definition later.
    /// </summary>
    private static void ThrowIfChainStepHasCompensator(JobStepBuilder stepBuilder)
    {
        if (stepBuilder.Compensation is not null)
        {
            throw new InvalidOperationException("Compensation-chain steps cannot have compensators.");
        }
    }

    /// <summary>
    /// A failure-chain step cannot carry a run-if condition — the chain runs only in response to a failure,
    /// and its steps are unconditional cleanup. Fail fast at build time rather than letting the validator
    /// reject it later.
    /// </summary>
    private static void ThrowIfChainStepHasCondition(JobStepBuilder stepBuilder)
    {
        if (stepBuilder.Condition is not null)
        {
            throw new InvalidOperationException("Compensation-chain steps cannot have run-if conditions.");
        }
    }

    /// <summary>Returns the assembled compensation step list.</summary>
    internal IReadOnlyList<BatchStep> Build() => _steps;
}
