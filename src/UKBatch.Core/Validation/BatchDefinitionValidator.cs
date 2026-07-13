using System.Globalization;
using UKBatch.Abstractions.Batches;

namespace UKBatch.Validation;

/// <summary>
/// Stateless validator for <see cref="BatchDefinition"/>. Returns a <see cref="ValidationResult"/>
/// — caller decides whether to throw (runtime) or aggregate (REST API 422).
/// Enforces <c>JoinPolicy</c> / <c>FailurePolicy</c> defined-enum checks plus the
/// <c>WaitMajority</c> count guard.
/// </summary>
internal static class BatchDefinitionValidator
{
    /// <summary>Runs every rule and returns the aggregated result.</summary>
    public static ValidationResult Validate(BatchDefinition def)
    {
        ArgumentNullException.ThrowIfNull(def);

        var errors = new List<ValidationError>();
        if (string.IsNullOrWhiteSpace(def.Id))
        {
            errors.Add(new ValidationError("Id", "must be non-empty"));
        }
        if (string.IsNullOrWhiteSpace(def.Name))
        {
            errors.Add(new ValidationError("Name", "must be non-empty"));
        }
        if (def.Steps.Count == 0)
        {
            errors.Add(new ValidationError("Steps", "must contain at least one step"));
        }

        if (!Enum.IsDefined(def.FailurePolicy))
        {
            errors.Add(new ValidationError("FailurePolicy", $"unknown enum value {(int)def.FailurePolicy}"));
        }

        // A catch-up window, when set, must be non-negative (a negative window is meaningless). It is NOT
        // rejected when Schedule is null — an unscheduled batch simply ignores it, so blocking would be a
        // spurious failure for a definition that is merely over-specified.
        if (def.ScheduleCatchUpWindow is { } catchUpWindow && catchUpWindow < TimeSpan.Zero)
        {
            errors.Add(new ValidationError("ScheduleCatchUpWindow", "must be non-negative"));
        }

        for (var i = 0; i < def.Steps.Count; i++)
        {
            ValidateStep(def.Steps[i], $"Steps[{i}]", errors, allowParallel: true, allowCompensation: true, allowCondition: true);
        }

        // Compensation steps are persisted verbatim and run via the same step dispatch as the main
        // sequence, so they need the same per-step shape checks — otherwise a blank JobName slips
        // through REST create/update and only surfaces as a silent runtime failure.
        for (var i = 0; i < def.OnFailureSteps.Count; i++)
        {
            ValidateStep(def.OnFailureSteps[i], $"OnFailureSteps[{i}]", errors, allowParallel: true, allowCompensation: false, allowCondition: false);
        }

        ValidateStepIdUniqueness(def, errors);

        return new ValidationResult(errors);
    }

    /// <summary>
    /// Every step in a definition must carry a unique <see cref="BatchStep.StepId"/> — across the main
    /// sequence, ParallelGroup children, and compensation (OnFailure) steps. The id is the durable
    /// correlation key that ties an execution row (and an approval-gate or cross-service shadow record)
    /// back to its step; a reused id makes that mapping ambiguous and breaks per-step lookups. Tooling
    /// (the fluent builder and the wizard) already generates collision-free ids, so this rejects only a
    /// hand-built or REST-supplied definition that reuses one.
    /// </summary>
    private static void ValidateStepIdUniqueness(BatchDefinition def, List<ValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var duplicates = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stepId in EnumerateStepIds(def))
        {
            // Blank ids are already reported by ValidateStep; skip them here so a definition missing an
            // id is not also flagged as a (spurious) duplicate of another blank.
            if (string.IsNullOrWhiteSpace(stepId))
            {
                continue;
            }
            if (!seen.Add(stepId))
            {
                duplicates.Add(stepId);
            }
        }

        foreach (var dup in duplicates)
        {
            errors.Add(new ValidationError("StepId", $"duplicate step id '{dup}' — every step id in a definition must be unique"));
        }
    }

    /// <summary>
    /// Walks every step id in the definition: top-level steps, single-level ParallelGroup children, and
    /// OnFailure (compensation) steps — the same id space the runtime correlates against.
    /// </summary>
    private static IEnumerable<string> EnumerateStepIds(BatchDefinition def)
    {
        foreach (var step in def.Steps)
        {
            yield return step.StepId;
            // A compensator's execution rows are correlated by a derived id (parent id + fixed suffix), so
            // that derived id lives in the same uniqueness space: a hand-built definition that declares a
            // step id colliding with a compensator's derived id would make the mapping ambiguous.
            if (step.Compensation is not null)
            {
                yield return CompensationStepIds.For(step.StepId);
            }
            if (step.StepType == BatchStepType.ParallelGroup && step.ParallelGroup is not null)
            {
                foreach (var child in step.ParallelGroup.Steps)
                {
                    yield return child.StepId;
                }
            }
        }
        foreach (var step in def.OnFailureSteps)
        {
            yield return step.StepId;
        }
    }

    private static void ValidateStep(BatchStep step, string path, List<ValidationError> errors, bool allowParallel, bool allowCompensation, bool allowCondition)
    {
        if (string.IsNullOrWhiteSpace(step.StepId))
        {
            errors.Add(new ValidationError($"{path}.StepId", "must be non-empty"));
        }
        else if (step.StepId.EndsWith(CompensationStepIds.Suffix, StringComparison.Ordinal))
        {
            // The compensator suffix is a reserved correlation namespace: a compensator's execution rows
            // and dashboard node carry the parent id plus this suffix. A real step id ending in it would
            // collide with a derived compensator id, and dashboards that strip the suffix to map a node
            // back to its parent would mis-resolve it. Reject it up front so the derivation stays unambiguous.
            errors.Add(new ValidationError($"{path}.StepId",
                $"must not end with the reserved compensator suffix '{CompensationStepIds.Suffix}'"));
        }

        switch (step.StepType)
        {
            case BatchStepType.Job:
                if (step.Job is null)
                {
                    errors.Add(new ValidationError($"{path}.Job", "Job step requires Job payload"));
                }
                else if (string.IsNullOrWhiteSpace(step.Job.JobName))
                {
                    errors.Add(new ValidationError($"{path}.Job.JobName", "must be non-empty"));
                }
                break;

            case BatchStepType.ParallelGroup:
                if (!allowParallel)
                {
                    errors.Add(new ValidationError($"{path}.StepType", "Nested ParallelGroup steps are forbidden in v0.1"));
                    break;
                }
                if (step.ParallelGroup is null)
                {
                    errors.Add(new ValidationError($"{path}.ParallelGroup", "ParallelGroup step requires payload"));
                    break;
                }

                // Validate JoinPolicy is a defined enum value.
                if (!Enum.IsDefined(step.ParallelGroup.JoinPolicy))
                {
                    errors.Add(new ValidationError(
                        $"{path}.ParallelGroup.JoinPolicy",
                        $"unknown enum value {(int)step.ParallelGroup.JoinPolicy}"));
                }

                if (step.ParallelGroup.Steps.Count < 2)
                {
                    errors.Add(new ValidationError(
                        $"{path}.ParallelGroup.Steps",
                        "ParallelGroup must contain >=2 children"));
                }

                // WaitMajority with <3 children is degenerate (quorum trivially 1).
                if (step.ParallelGroup.JoinPolicy == ParallelJoinPolicy.WaitMajority
                    && step.ParallelGroup.Steps.Count < 3)
                {
                    errors.Add(new ValidationError(
                        $"{path}.ParallelGroup.Steps",
                        "WaitMajority requires >=3 children (degenerate with fewer; use WaitAll instead)"));
                }

                for (var j = 0; j < step.ParallelGroup.Steps.Count; j++)
                {
                    ValidateStep(step.ParallelGroup.Steps[j], $"{path}.ParallelGroup.Steps[{j}]", errors, allowParallel: false, allowCompensation: false, allowCondition: false);
                }
                break;

            case BatchStepType.ApprovalGate:
                if (step.Approval is null)
                {
                    errors.Add(new ValidationError($"{path}.Approval", "ApprovalGate step requires payload"));
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(step.Approval.Title))
                    {
                        errors.Add(new ValidationError($"{path}.Approval.Title", "must be non-empty"));
                    }

                    // An on-timeout action other than Fail only fires when a timeout is set. AutoApprove
                    // or Hold with no duration leaves the gate waiting forever, contradicting the chosen
                    // action. (Fail with no timeout is a legitimate indefinite wait.)
                    if (step.Approval.OnTimeout != ApprovalTimeoutAction.Fail
                        && (step.Approval.TimeoutAfter is null || step.Approval.TimeoutAfter <= TimeSpan.Zero))
                    {
                        errors.Add(new ValidationError(
                            $"{path}.Approval.Timeout",
                            "required when the on-timeout action is AutoApprove or Hold"));
                    }
                }
                break;

            default:
                // Unknown step type — forward-compat tolerated at validator level; the runtime
                // logs and continues per BatchFailurePolicy.
                break;
        }

        // A compensator is only meaningful where the reverse unwind can honor it: a top-level Job or
        // ParallelGroup step (a group compensates as one unit). Rejecting it elsewhere — parallel
        // children, compensation-chain steps, approval gates — is deliberate: silently ignoring a
        // declared compensator would let a caller believe cleanup is wired when it never runs.
        if (step.Compensation is { } comp)
        {
            if (!allowCompensation)
            {
                errors.Add(new ValidationError($"{path}.Compensation",
                    "compensation is not allowed here (only on top-level Job or ParallelGroup steps)"));
            }
            else if (step.StepType == BatchStepType.ApprovalGate)
            {
                errors.Add(new ValidationError($"{path}.Compensation", "an ApprovalGate step cannot have a compensator"));
            }
            if (string.IsNullOrWhiteSpace(comp.JobName))
            {
                errors.Add(new ValidationError($"{path}.Compensation.JobName", "must be non-empty"));
            }
        }

        // A run-if condition is honored only on a top-level step (Job, ParallelGroup, or ApprovalGate). On a
        // parallel child or an OnFailure step it would be silently ignored, so reject it there — declaring a
        // guard that never runs is worse than an error.
        if (step.Condition is { } cond)
        {
            if (!allowCondition)
            {
                errors.Add(new ValidationError($"{path}.Condition",
                    "a run-if condition is not allowed here (only on top-level steps)"));
            }
            if (string.IsNullOrWhiteSpace(cond.ParameterKey))
            {
                errors.Add(new ValidationError($"{path}.Condition.ParameterKey", "must be non-empty"));
            }
            if (!Enum.IsDefined(cond.Operator))
            {
                errors.Add(new ValidationError($"{path}.Condition.Operator", $"unknown enum value {(int)cond.Operator}"));
            }
            else if (ConditionOperatorNeedsValue(cond.Operator) && string.IsNullOrEmpty(cond.Value))
            {
                errors.Add(new ValidationError($"{path}.Condition.Value", $"required for the {cond.Operator} operator"));
            }
            else if (IsOrderingOperator(cond.Operator)
                && !double.TryParse(cond.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                // An ordering operator compares numerically; a non-numeric comparand makes the evaluator return
                // false on every run — a guard that silently never fires. Reject it up front (a broken guard is
                // worse than an error, and it fails toward "never ship").
                errors.Add(new ValidationError($"{path}.Condition.Value",
                    $"must be a number for the {cond.Operator} operator"));
            }
        }
    }

    /// <summary>
    /// The comparison operators test the value against <see cref="StepCondition.Value"/>; the presence and
    /// boolean operators (Exists / NotExists / IsTrue / IsFalse) do not, so they need no comparand.
    /// </summary>
    private static bool ConditionOperatorNeedsValue(ConditionOperator op) => op switch
    {
        ConditionOperator.Exists or ConditionOperator.NotExists
            or ConditionOperator.IsTrue or ConditionOperator.IsFalse => false,
        _ => true,
    };

    private static bool IsOrderingOperator(ConditionOperator op) => op is
        ConditionOperator.GreaterThan or ConditionOperator.GreaterThanOrEqual or
        ConditionOperator.LessThan or ConditionOperator.LessThanOrEqual;
}
