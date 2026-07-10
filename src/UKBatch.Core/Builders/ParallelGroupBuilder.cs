using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Internal;

namespace UKBatch.Builders;

/// <summary>Sub-builder for <see cref="BatchBuilder.ThenInParallel"/>.</summary>
public sealed class ParallelGroupBuilder
{
    private readonly List<BatchStep> _children = new();
    private ParallelJoinPolicy _joinPolicy = ParallelJoinPolicy.WaitAll;

    /// <summary>Group-level compensator, copied onto the group's step by the parent batch builder.</summary>
    internal CompensationStepData? Compensation { get; private set; }

    /// <summary>Adds a child job step (only Job steps are allowed inside a parallel group — nesting forbidden).</summary>
    public ParallelGroupBuilder RunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
    {
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        ThrowIfChildHasCompensator(stepBuilder);
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
        ThrowIfChildHasCompensator(stepBuilder);
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
    /// Adds a partitioned-job child step by type. Partitioned jobs implement
    /// <see cref="IPartitionedJob{TItem}"/>, so the <see cref="IJob"/>-constrained
    /// <see cref="RunJob{TJob}"/> cannot accept them — this is the typed counterpart for data-parallel
    /// jobs. The job must be registered via <c>AddPartitionedJob&lt;TJob, TItem&gt;()</c>; the child step
    /// is resolved by the job's type name, which matches that registration's default name.
    /// </summary>
    public ParallelGroupBuilder RunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => RunJob(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>
    /// Attaches a GROUP-level compensator: the job that undoes the whole group's work when a LATER step
    /// fails and the batch's failure policy is <c>Compensate</c>. The group compensates as one unit —
    /// per-child compensators are not supported (attach the compensator to the group instead).
    /// </summary>
    public ParallelGroupBuilder CompensateWith<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
    {
        Compensation = JobStepBuilder.BuildCompensationData(typeof(TJob).FullName ?? typeof(TJob).Name, configure);
        return this;
    }

    /// <summary>
    /// Attaches a GROUP-level compensator by job name (cross-service compensators pair with
    /// <c>c.OnService(...)</c>). See <see cref="CompensateWith{TJob}"/> for semantics.
    /// </summary>
    public ParallelGroupBuilder CompensateWith(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        Compensation = JobStepBuilder.BuildCompensationData(jobName, configure);
        return this;
    }

    /// <summary>
    /// Attaches a partitioned-job GROUP-level compensator by type. See
    /// <see cref="CompensateWith{TJob}"/> for semantics.
    /// </summary>
    public ParallelGroupBuilder CompensateWithPartitioned<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
    {
        Compensation = JobStepBuilder.BuildCompensationData(typeof(TJob).FullName ?? typeof(TJob).Name, configure);
        return this;
    }

    /// <summary>
    /// A parallel-group CHILD cannot carry its own compensator — the group is the atomic unit of
    /// compensation (concurrent children have no defined order to unwind in). Fail fast at build time
    /// rather than letting the validator reject the definition later.
    /// </summary>
    private static void ThrowIfChildHasCompensator(JobStepBuilder stepBuilder)
    {
        if (stepBuilder.Compensation is not null)
        {
            throw new InvalidOperationException(
                "Parallel-group children cannot have compensators; attach the compensator to the group.");
        }
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
