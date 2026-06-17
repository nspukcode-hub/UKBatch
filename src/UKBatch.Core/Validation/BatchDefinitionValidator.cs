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

        for (var i = 0; i < def.Steps.Count; i++)
        {
            ValidateStep(def.Steps[i], $"Steps[{i}]", errors, allowParallel: true);
        }

        // Compensation steps are persisted verbatim and run via the same step dispatch as the main
        // sequence, so they need the same per-step shape checks — otherwise a blank JobName slips
        // through REST create/update and only surfaces as a silent runtime failure.
        for (var i = 0; i < def.OnFailureSteps.Count; i++)
        {
            ValidateStep(def.OnFailureSteps[i], $"OnFailureSteps[{i}]", errors, allowParallel: true);
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

    private static void ValidateStep(BatchStep step, string path, List<ValidationError> errors, bool allowParallel)
    {
        if (string.IsNullOrWhiteSpace(step.StepId))
        {
            errors.Add(new ValidationError($"{path}.StepId", "must be non-empty"));
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
                    ValidateStep(step.ParallelGroup.Steps[j], $"{path}.ParallelGroup.Steps[{j}]", errors, allowParallel: false);
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
    }
}
