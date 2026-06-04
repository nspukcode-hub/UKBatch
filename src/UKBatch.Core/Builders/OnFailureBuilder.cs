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

    /// <summary>Returns the assembled compensation step list.</summary>
    internal IReadOnlyList<BatchStep> Build() => _steps;
}
