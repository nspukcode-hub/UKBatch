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

        return new ValidationResult(errors);
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
