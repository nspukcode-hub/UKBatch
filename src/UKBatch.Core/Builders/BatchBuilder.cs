using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Internal;

namespace UKBatch.Builders;

/// <summary>Fluent batch composition.</summary>
public sealed class BatchBuilder
{
    private readonly UKBatchOptions _options;
    private readonly List<BatchStep> _steps = new();
    private readonly List<BatchStep> _onFailureSteps = new();
    private BatchFailurePolicy _failurePolicy = BatchFailurePolicy.StopOnFailure;
    private string? _schedule;
    private TimeSpan? _catchUpWindow;

    internal BatchBuilder(UKBatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
    }

    /// <summary>Adds a job step as the first or next step.</summary>
    public BatchBuilder RunJob<TJob>(Action<JobStepBuilder>? configure = null)
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
            Compensation = stepBuilder.Compensation,
            Condition = stepBuilder.Condition,
            Metadata = null,
        });
        return this;
    }

    /// <summary>Adds a job step after the previous step (semantic alias for <see cref="RunJob{TJob}"/>).</summary>
    public BatchBuilder ThenRunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
        => RunJob<TJob>(configure);

    /// <summary>
    /// Adds a job step by string name (e.g. <c>"InvoiceProcessing"</c>). Use when
    /// the job type is not referenceable from the orchestrator — typically because the job lives in
    /// a worker microservice in a separate assembly. Pair with <c>step.OnService("billing-worker")</c>
    /// to route the step to a remote service via the registered <see cref="Abstractions.Transport.ITransport"/>.
    /// </summary>
    /// <remarks>
    /// <para><b>No local registration required.</b> Unlike <see cref="RunJob{TJob}"/>, the string
    /// overload does NOT cross-check against the local <c>JobDefinitionRegistry</c> — for cross-service
    /// steps, the job is registered on the WORKER side, not the orchestrator.</para>
    /// <para><b>Local cross-service forbidden:</b> if you call this overload WITHOUT
    /// <c>step.OnService(...)</c>, the batch validator at host-start does NOT reject — but the
    /// runtime fails the step at BatchExecutor dispatch time with a clear
    /// <see cref="UKBatch.Runtime.JobNotRegisteredException"/>.</para>
    /// <para><b>Cross-service example:</b>
    /// <code>batch.RunJob("InvoiceProcessing", step => step.OnService("billing-worker"));</code></para>
    /// </remarks>
    /// <param name="jobName">Logical job name. Non-empty (<see cref="ArgumentException"/> on empty/whitespace).</param>
    /// <param name="configure">Optional per-step builder (parameters, target service, retries, timeout).</param>
    public BatchBuilder RunJob(string jobName, Action<JobStepBuilder>? configure = null)
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
            Compensation = stepBuilder.Compensation,
            Condition = stepBuilder.Condition,
            Metadata = null,
        });
        return this;
    }

    /// <summary>Semantic alias for <see cref="RunJob(string, Action{JobStepBuilder}?)"/>.</summary>
    public BatchBuilder ThenRunJob(string jobName, Action<JobStepBuilder>? configure = null)
        => RunJob(jobName, configure);

    /// <summary>
    /// Adds a partitioned-job step by type. Partitioned jobs implement
    /// <see cref="IPartitionedJob{TItem}"/>, so the type-parameter constrained
    /// <see cref="RunJob{TJob}"/> (which requires <see cref="IJob"/>) cannot accept them — this is the
    /// typed counterpart for data-parallel jobs. The job must be registered via
    /// <c>AddPartitionedJob&lt;TJob, TItem&gt;()</c>; the step is resolved by the job's type name, which
    /// matches that registration's default name.
    /// </summary>
    public BatchBuilder RunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => RunJob(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>Semantic alias for <see cref="RunPartitionedJob{TJob}"/>.</summary>
    public BatchBuilder ThenRunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => RunPartitionedJob<TJob>(configure);

    /// <summary>Adds a parallel-group step.</summary>
    public BatchBuilder ThenInParallel(Action<ParallelGroupBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var group = new ParallelGroupBuilder();
        configure(group);
        var data = group.Build();
        _steps.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _steps.Count,
            StepType = BatchStepType.ParallelGroup,
            Job = null,
            ParallelGroup = data,
            Approval = null,
            Compensation = group.Compensation,
            Condition = group.Condition,
            Metadata = null,
        });
        return this;
    }

    /// <summary>Adds an approval-gate step.</summary>
    public BatchBuilder ThenWaitForApproval(
        string title,
        string[] roles,
        TimeSpan? timeout = null,
        ApprovalTimeoutAction onTimeout = ApprovalTimeoutAction.Fail,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(title);
        ArgumentNullException.ThrowIfNull(roles);
        _steps.Add(new BatchStep
        {
            StepId = IdGenerator.NewStepId(),
            Order = _steps.Count,
            StepType = BatchStepType.ApprovalGate,
            Job = null,
            ParallelGroup = null,
            Approval = new ApprovalGateConfig
            {
                Title = title,
                Description = description,
                AllowedRoles = roles.ToArray(),
                TimeoutAfter = timeout,
                OnTimeout = onTimeout,
            },
            Metadata = null,
        });
        return this;
    }

    /// <summary>
    /// Configures the batch's failure policy. <c>ContinueOnFailure</c> swallows the failed step
    /// and proceeds; it does NOT invoke <see cref="OnFailure"/> compensation. Use <c>Compensate</c>
    /// for compensation.
    /// </summary>
    public BatchBuilder FailurePolicy(BatchFailurePolicy policy)
    {
        if (!Enum.IsDefined(policy))
        {
            throw new ArgumentOutOfRangeException(nameof(policy), policy, "unknown BatchFailurePolicy");
        }
        _failurePolicy = policy;
        return this;
    }

    /// <summary>Adds compensating steps invoked when <see cref="FailurePolicy"/> is <see cref="BatchFailurePolicy.Compensate"/>.</summary>
    public BatchBuilder OnFailure(Action<OnFailureBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var b = new OnFailureBuilder();
        configure(b);
        _onFailureSteps.AddRange(b.Build());
        return this;
    }

    /// <summary>
    /// Sets a cron schedule on the batch (validated immediately; see
    /// <see cref="JobBuilder.WithSchedule"/>).
    /// </summary>
    public BatchBuilder WithSchedule(string cronExpression)
    {
        ArgumentException.ThrowIfNullOrEmpty(cronExpression);
        try
        {
            _ = Cronos.CronExpression.Parse(cronExpression, _options.CronFormat);
        }
        catch (Exception ex)
        {
            throw new ArgumentException($"Invalid cron expression '{cronExpression}': {ex.Message}", nameof(cronExpression), ex);
        }
        _schedule = cronExpression;
        return this;
    }

    /// <summary>
    /// Opts this scheduled batch in to catching up a single missed fire on restart, bounded by
    /// <paramref name="window"/>. If a scheduled occurrence was due while the process was down, on the
    /// next start the most recent occurrence missed within the window is replayed exactly once (coalesced,
    /// never double-fired). Has effect only with the EF storage adapter (the durable last-fire watermark)
    /// and only when a <see cref="WithSchedule"/> cron is set. A zero window disables catch-up.
    /// </summary>
    /// <param name="window">Maximum age of a missed occurrence to replay. Must be non-negative.</param>
    public BatchBuilder CatchUpMissedWithin(TimeSpan window)
    {
        if (window < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(window), window, "catch-up window must be non-negative");
        }
        _catchUpWindow = window;
        return this;
    }

    /// <summary>Builds the immutable <see cref="BatchDefinition"/>.</summary>
    internal BatchDefinition Build(string id, string name, DateTimeOffset createdAtUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentException.ThrowIfNullOrEmpty(name);
        return new BatchDefinition
        {
            Id = id,
            Name = name,
            Source = BatchSource.Code,
            Schedule = _schedule,
            ScheduleCatchUpWindow = _catchUpWindow,
            Steps = _steps,
            FailurePolicy = _failurePolicy,
            OnFailureSteps = _onFailureSteps,
            CreatedAtUtc = createdAtUtc,
            CreatedBy = null,
            Version = 1,
        };
    }
}
