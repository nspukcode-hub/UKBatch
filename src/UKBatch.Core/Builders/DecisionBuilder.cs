using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Jobs;
using UKBatch.Internal;

namespace UKBatch.Builders;

/// <summary>
/// Sub-builder for <see cref="BatchBuilder.Decide"/>: composes the ordered branches of a decision step. Open a
/// branch with <see cref="When"/> (a condition) or <see cref="Otherwise"/> (the else/default), then close it
/// with a <c>RunJob</c> call:
/// <code>
/// batch.Decide(d => d
///     .When("amount", ConditionOperator.GreaterThan, 1000).RunJob&lt;ShipExpress&gt;()
///     .When("amount", ConditionOperator.LessThanOrEqual, 1000).RunJob&lt;ShipStandard&gt;()
///     .Otherwise().RunJob&lt;NotifyOnly&gt;());   // Otherwise() is optional
/// </code>
/// </summary>
public sealed class DecisionBuilder
{
    private readonly List<DecisionBranch> _branches = new();
    private StepCondition? _pendingWhen;
    private bool _hasPending;

    /// <summary>Decision-level compensator, copied onto the decision's step by the parent batch builder.</summary>
    internal CompensationStepData? Compensation { get; private set; }

    /// <summary>Decision-level run-if condition, copied onto the decision's step by the parent batch builder.</summary>
    internal StepCondition? Condition { get; private set; }

    /// <summary>
    /// Opens a conditional branch: it runs when the value at <paramref name="parameterKey"/> (an earlier step's
    /// forwarded output or a trigger parameter) satisfies <paramref name="op"/> against
    /// <paramref name="value"/>. Close the branch with a <c>RunJob</c> call before opening another.
    /// </summary>
    public DecisionBuilder When(string parameterKey, ConditionOperator op, object? value = null)
    {
        ThrowIfBranchOpen();
        _pendingWhen = JobStepBuilder.BuildCondition(parameterKey, op, value);
        _hasPending = true;
        return this;
    }

    /// <summary>
    /// Opens the else/default branch: it runs when no earlier branch matched. At most one else branch is
    /// allowed and it must be the last branch. Close it with a <c>RunJob</c> call.
    /// </summary>
    public DecisionBuilder Otherwise()
    {
        ThrowIfBranchOpen();
        if (_branches.Any(b => b.When is null))
        {
            throw new InvalidOperationException("A decision may have at most one Otherwise (else) branch.");
        }
        _pendingWhen = null;
        _hasPending = true;
        return this;
    }

    /// <summary>Closes the open branch with a job step by type.</summary>
    public DecisionBuilder RunJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
        => AddBranch(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>
    /// Closes the open branch with a job step by string name (cross-service branches pair with
    /// <c>step.OnService(...)</c>). See <see cref="BatchBuilder.RunJob(string, Action{JobStepBuilder}?)"/>.
    /// </summary>
    public DecisionBuilder RunJob(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        return AddBranch(jobName, configure);
    }

    /// <summary>
    /// Closes the open branch with a partitioned-job step by type. Partitioned jobs implement
    /// <see cref="IPartitionedJob{TItem}"/>, so the <see cref="IJob"/>-constrained <see cref="RunJob{TJob}"/>
    /// cannot accept them — this is the typed counterpart for data-parallel branch jobs.
    /// </summary>
    public DecisionBuilder RunPartitionedJob<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
        => AddBranch(typeof(TJob).FullName ?? typeof(TJob).Name, configure);

    /// <summary>
    /// Attaches a DECISION-level compensator: the job that undoes whichever branch ran when a LATER step fails
    /// and the batch's failure policy is <c>Compensate</c>. The decision compensates as one unit — a branch is
    /// not compensated individually — so the compensator must undo whichever branch won (discriminate via the
    /// forwarded outputs) or be idempotent.
    /// </summary>
    public DecisionBuilder CompensateWith<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IJob
    {
        Compensation = JobStepBuilder.BuildCompensationData(typeof(TJob).FullName ?? typeof(TJob).Name, configure);
        return this;
    }

    /// <summary>
    /// Attaches a DECISION-level compensator by job name (cross-service compensators pair with
    /// <c>c.OnService(...)</c>). See <see cref="CompensateWith{TJob}"/> for semantics.
    /// </summary>
    public DecisionBuilder CompensateWith(string jobName, Action<JobStepBuilder>? configure = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(jobName);
        Compensation = JobStepBuilder.BuildCompensationData(jobName, configure);
        return this;
    }

    /// <summary>
    /// Attaches a partitioned-job DECISION-level compensator by type. See <see cref="CompensateWith{TJob}"/>.
    /// </summary>
    public DecisionBuilder CompensateWithPartitioned<TJob>(Action<JobStepBuilder>? configure = null)
        where TJob : class, IPartitionedJobMarker
    {
        Compensation = JobStepBuilder.BuildCompensationData(typeof(TJob).FullName ?? typeof(TJob).Name, configure);
        return this;
    }

    /// <summary>
    /// Guards the WHOLE decision with a run-if condition: the entire decision is skipped as one unit (no branch
    /// runs, no routing happens) when the value at <paramref name="parameterKey"/> does not satisfy
    /// <paramref name="op"/> against <paramref name="value"/>. Distinct from <see cref="When"/>, which routes
    /// among branches once the decision runs.
    /// </summary>
    public DecisionBuilder RunIf(string parameterKey, ConditionOperator op, object? value = null)
    {
        Condition = JobStepBuilder.BuildCondition(parameterKey, op, value);
        return this;
    }

    private DecisionBuilder AddBranch(string jobName, Action<JobStepBuilder>? configure)
    {
        if (!_hasPending)
        {
            throw new InvalidOperationException("Open a branch with When(...) or Otherwise() before RunJob(...).");
        }
        var stepBuilder = new JobStepBuilder();
        configure?.Invoke(stepBuilder);
        ThrowIfBranchHasCompensator(stepBuilder);
        ThrowIfBranchHasCondition(stepBuilder);
        _branches.Add(new DecisionBranch
        {
            StepId = IdGenerator.NewStepId(),
            Label = null,
            When = _pendingWhen,
            Job = new JobStepData
            {
                JobName = jobName,
                TargetService = stepBuilder.TargetService,
                Parameters = stepBuilder.Parameters,
                MaxRetries = stepBuilder.MaxRetries,
                TimeoutSeconds = stepBuilder.TimeoutSeconds,
            },
        });
        _hasPending = false;
        _pendingWhen = null;
        return this;
    }

    private void ThrowIfBranchOpen()
    {
        if (_hasPending)
        {
            throw new InvalidOperationException(
                "The previous branch has no job yet — call RunJob(...) before opening another When(...)/Otherwise().");
        }
    }

    /// <summary>
    /// A branch's job cannot carry its own compensator — the decision is the atomic unit of compensation. Fail
    /// fast rather than silently dropping the compensator (<see cref="JobStepData"/> has no compensator slot).
    /// </summary>
    private static void ThrowIfBranchHasCompensator(JobStepBuilder stepBuilder)
    {
        if (stepBuilder.Compensation is not null)
        {
            throw new InvalidOperationException(
                "Decision branches cannot have their own compensator; attach the compensator to the decision.");
        }
    }

    /// <summary>
    /// A branch's job cannot carry its own run-if condition — the branch's <c>When</c> is its condition. Fail
    /// fast rather than silently dropping it (<see cref="JobStepData"/> has no condition slot).
    /// </summary>
    private static void ThrowIfBranchHasCondition(JobStepBuilder stepBuilder)
    {
        if (stepBuilder.Condition is not null)
        {
            throw new InvalidOperationException(
                "Decision branches cannot have their own run-if condition; use When(...) to condition the branch.");
        }
    }

    /// <summary>Builds the immutable <see cref="DecisionStepData"/>.</summary>
    internal DecisionStepData Build()
    {
        if (_hasPending)
        {
            throw new InvalidOperationException("The last branch has no job — call RunJob(...) to complete it.");
        }
        if (_branches.Count == 0)
        {
            throw new InvalidOperationException("A decision must have at least one branch.");
        }
        for (var i = 0; i < _branches.Count; i++)
        {
            if (_branches[i].When is null && i != _branches.Count - 1)
            {
                throw new InvalidOperationException("The Otherwise (else) branch must be the last branch.");
            }
        }
        return new DecisionStepData { Branches = _branches };
    }
}
