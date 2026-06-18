using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Client-side mirror of the server's <c>BatchDefinitionValidator</c> (Core, internal). Lets the
/// wizard reject locally before the round-trip and drives the per-step <c>Next</c> gating. The server
/// remains the authority — any server-only failure maps back via the submit catch.
/// </summary>
/// <remarks>
/// <b>Parity discipline:</b> the message strings here are NOT
/// a contract — the parity test asserts that, for the same model, this validator produces the same
/// SET of property-paths as the server validator (path-set equality, not wording). The parity matrix
/// is constrained to wizard-emittable models (server-only paths the wizard cannot reach — null payloads,
/// <c>Enum.IsDefined</c>, non-empty <c>Id</c> — are out of scope).
/// <para>
/// <b>Intentional asymmetry:</b> parameter-key checks (blank key with a value, duplicate keys) are
/// CLIENT-ONLY. The wizard's dictionary projection would silently drop or collide such rows, so the
/// wizard surfaces them up front; the server does not inspect parameter keys (it stays authoritative
/// on step shape, not parameter content). Parameter-key paths are therefore excluded from parity.
/// </para>
/// </remarks>
public static class BatchDefinitionClientValidator
{
    /// <summary>Returns property-path → messages, mirroring the server validator's rules.</summary>
    public static IReadOnlyDictionary<string, string[]> Validate(BatchWizardModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var errors = new List<(string Path, string Msg)>();
        if (string.IsNullOrWhiteSpace(model.Name)) errors.Add(("Name", "must be non-empty"));
        if (model.Steps.Count == 0) errors.Add(("Steps", "must contain at least one step"));
        ValidateCatchUpWindow(model, errors);
        for (var i = 0; i < model.Steps.Count; i++)
            ValidateStep(model.Steps[i], $"Steps[{i}]", errors, allowParallel: true);
        // Validate OnFailureSteps (Compensate branch). The server validator currently has a parallel
        // gap here; the wizard MUST surface blank JobName etc. so the operator
        // doesn't ship a runtime-fail definition. Path prefix routes to the FailurePolicy step.
        for (var i = 0; i < model.OnFailureSteps.Count; i++)
            ValidateStep(model.OnFailureSteps[i], $"OnFailureSteps[{i}]", errors, allowParallel: false);
        return errors
            .GroupBy(e => e.Path, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Select(e => e.Msg).ToArray(), StringComparer.Ordinal);
    }

    private static void ValidateStep(WizardStepDraft step, string path, List<(string, string)> errors, bool allowParallel)
    {
        if (string.IsNullOrWhiteSpace(step.StepId)) errors.Add(($"{path}.StepId", "must be non-empty"));
        switch (step.StepType)
        {
            case BatchStepType.Job:
                if (string.IsNullOrWhiteSpace(step.JobName))
                    errors.Add(($"{path}.Job.JobName", "must be non-empty"));
                ValidateParameters(step, path, errors);
                break;

            case BatchStepType.ParallelGroup:
                if (!allowParallel)
                {
                    errors.Add(($"{path}.StepType", "Nested ParallelGroup steps are forbidden in v0.1"));
                    break;
                }
                if (step.Children.Count < 2)
                    errors.Add(($"{path}.ParallelGroup.Steps", "ParallelGroup must contain >=2 children"));
                if (step.JoinPolicy == ParallelJoinPolicy.WaitMajority && step.Children.Count < 3)
                    errors.Add(($"{path}.ParallelGroup.Steps", "WaitMajority requires >=3 children (degenerate with fewer; use WaitAll instead)"));
                for (var j = 0; j < step.Children.Count; j++)
                    ValidateStep(step.Children[j], $"{path}.ParallelGroup.Steps[{j}]", errors, allowParallel: false);
                break;

            case BatchStepType.ApprovalGate:
                if (string.IsNullOrWhiteSpace(step.ApprovalTitle))
                    errors.Add(($"{path}.Approval.Title", "must be non-empty"));
                // An on-timeout action other than Fail only fires when a timeout is set. Picking
                // AutoApprove or Hold with no duration leaves the gate waiting forever while the UI
                // implies the action will run — reject the inconsistent combination up front. (Fail with
                // no timeout is a legitimate indefinite wait that only ends on a manual reject.)
                var hasTimeout = step.TimeoutSecondsApproval is { } secs && secs > 0;
                if (step.OnTimeout != ApprovalTimeoutAction.Fail && !hasTimeout)
                    errors.Add(($"{path}.Approval.Timeout", "required when the on-timeout action is AutoApprove or Hold"));
                break;
        }
    }

    // The schedule catch-up window is a CLIENT-ONLY check (excluded from server parity, like the
    // parameter-key rules below). A negative magnitude can't express a real window, and a window with no
    // schedule has no effect at runtime — the wizard surfaces both up front so the operator notices,
    // rather than silently coercing the value to null. The path routes the Next-gating to the Schedule step.
    private static void ValidateCatchUpWindow(BatchWizardModel model, List<(string, string)> errors)
    {
        if (model.CatchUpWindowValue is { } v && v < 0)
        {
            errors.Add(("Schedule.CatchUpWindow", "must be zero or greater"));
            return; // a negative value is the primary problem; don't also nag about the schedule
        }

        var hasWindow = model.CatchUpWindowValue is { } w && w > 0;
        if (hasWindow && string.IsNullOrWhiteSpace(model.Schedule))
            errors.Add(("Schedule.CatchUpWindow", "has no effect without a cron schedule"));
    }

    // The conversion to a parameter dictionary drops blank-key rows and resolves duplicate keys
    // last-wins (so it never throws on the render path). These rules tell the operator what the
    // conversion would silently do: a duplicate non-blank key (Ordinal, matching dictionary semantics)
    // and a blank key paired with a real value are flagged; a fully-blank row is just an empty editor
    // row and is tolerated.
    private static void ValidateParameters(WizardStepDraft step, string path, List<(string, string)> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < step.Parameters.Count; i++)
        {
            var key = step.Parameters[i].Key;
            var value = step.Parameters[i].Value;
            if (string.IsNullOrWhiteSpace(key))
            {
                if (!string.IsNullOrWhiteSpace(value))
                    errors.Add(($"{path}.Job.Parameters[{i}].Key", "must be non-empty when a value is set"));
                continue;
            }
            if (!seen.Add(key))
                errors.Add(($"{path}.Job.Parameters[{i}].Key", $"duplicate parameter key '{key}'"));
        }
    }
}
