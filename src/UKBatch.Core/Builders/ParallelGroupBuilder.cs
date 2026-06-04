using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Internal;

namespace UKBatch.Builders;

/// <summary>Sub-builder for <see cref="BatchBuilder.ThenInParallel"/>.</summary>
public sealed class ParallelGroupBuilder
{
    private readonly List<BatchStep> _children = new();
    private ParallelJoinPolicy _joinPolicy = ParallelJoinPolicy.WaitAll;

    /// <summary>Adds a child job step (only Job steps are allowed inside a parallel group — nesting forbidden).</summary>
    public ParallelGroupBuilder RunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
    {
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        var jobName = typeof(TJob).FullName ?? typeof(TJob).Name;
        _children.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _children.Count,
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

    /// <summary>
    /// String-name overload for cross-service parallel-group children. See
    /// <see cref="BatchBuilder.RunJob(string, Action{JobStepBuilder}?)"/> for semantics.
    /// </summary>
    public ParallelGroupBuilder RunJob(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        _children.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _children.Count,
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

    /// <summary>Sets the join policy. Default is <see cref="ParallelJoinPolicy.WaitAll"/>.</summary>
    public ParallelGroupBuilder JoinPolicy(ParallelJoinPolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "unknown ParallelJoinPolicy");
        }
        _joinPolicy = policy;
        return this;
    }

    /// <summary>Builds the immutable <see cref="ParallelGroupData"/>.</summary>
    internal ParallelGroupData Build() => new()
    {
        Steps = _children,
        JoinPolicy = _joinPolicy,
    };
}
