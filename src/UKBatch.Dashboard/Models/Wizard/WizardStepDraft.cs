using System.Globalization;
using UKBatch.Abstractions.Batches;

namespace UKBatch.Dashboard.Models.Wizard;

/// <summary>
/// Mutable per-step draft for the wizard (Blazor two-way binding needs settable properties; the
/// Abstractions <see cref="BatchStep"/> records are <c>init</c>-only). A discriminated union over
/// Job / ParallelGroup / ApprovalGate, projected to <see cref="BatchStep"/> on submit and back via
/// <see cref="FromBatchStep"/> on edit-load.
/// </summary>
/// <remarks>
/// <see cref="Parameters"/> are static string key/values (emitted as <c>object?</c> = the string) with
/// no typed/JSON value parsing. At run time they merge LAST, so a static value here overrides a
/// same-named output forwarded from an earlier step.
/// </remarks>
public sealed class WizardStepDraft
{
    /// <summary>Stable id within the batch. New drafts get a short slug; edit-load round-trips the existing id.</summary>
    public string StepId { get; set; } = NewStepId();

    /// <summary>Discriminator.</summary>
    public BatchStepType StepType { get; set; } = BatchStepType.Job;

    // ── Job ──
    /// <summary>Logical job name (required for Job steps).</summary>
    public string JobName { get; set; } = string.Empty;

    /// <summary>Cross-service target; <c>null</c>/empty = Local.</summary>
    public string? TargetService { get; set; }

    /// <summary>Static parameters as string key/values.</summary>
    public List<KeyValuePair<string, string>> Parameters { get; set; } = new();

    /// <summary>Per-step retry budget; <c>null</c> = inherit.</summary>
    public int? MaxRetries { get; set; }

    /// <summary>Wall-clock timeout in seconds; <c>0</c> = no timeout, <c>null</c> = inherit.</summary>
    public int? TimeoutSeconds { get; set; }

    // ── Compensation (top-level Job / ParallelGroup only) ──
    /// <summary>
    /// Optional compensator for a top-level Job or ParallelGroup step; <c>null</c> = none. Ignored (never
    /// emitted) for ApprovalGate steps, parallel-group children, and compensation-chain steps. Round-tripped
    /// through edit-load so a builder-authored batch does not lose its compensators when re-saved from the UI.
    /// </summary>
    public CompensationDraft? Compensation { get; set; }

    // ── Condition (run-if guard; top-level Job / ParallelGroup / ApprovalGate) ──
    /// <summary>
    /// Optional run-if condition for a top-level step; <c>null</c> = the step always runs. Ignored (never
    /// emitted) for parallel-group children and compensation-chain steps. Round-tripped through edit-load so
    /// a builder-authored condition is not lost when the batch is re-saved from the UI.
    /// </summary>
    public ConditionDraft? Condition { get; set; }

    // ── ParallelGroup ──
    /// <summary>Fan-in join semantics.</summary>
    public ParallelJoinPolicy JoinPolicy { get; set; } = ParallelJoinPolicy.WaitAll;

    /// <summary>Job-only child drafts (single-level — nested groups forbidden in v0.1).</summary>
    public List<WizardStepDraft> Children { get; set; } = new();

    // ── Decision ──
    /// <summary>
    /// Ordered decision branches (one job each). The first whose <see cref="DecisionBranchDraft.When"/> holds
    /// runs; the rest are skipped. A branch with a null condition is the else/default — at most one, last.
    /// Round-tripped through edit-load so a builder-authored decision survives a re-save from the UI.
    /// </summary>
    public List<DecisionBranchDraft> DecisionBranches { get; set; } = new();

    // ── ApprovalGate ──
    /// <summary>Gate heading (required for ApprovalGate steps).</summary>
    public string ApprovalTitle { get; set; } = string.Empty;

    /// <summary>Optional long-form description.</summary>
    public string? ApprovalDescription { get; set; }

    /// <summary>Explicit allowed role names (ignored when <see cref="AnyAuthenticatedUser"/> is set).</summary>
    public List<string> AllowedRoles { get; set; } = new();

    /// <summary>When true, emits the <c>"*"</c> sentinel (any authenticated user) and ignores <see cref="AllowedRoles"/>.</summary>
    public bool AnyAuthenticatedUser { get; set; }

    /// <summary>Approval timeout in seconds; <c>null</c>/0 = wait indefinitely.</summary>
    public int? TimeoutSecondsApproval { get; set; }

    /// <summary>Action when the approval times out.</summary>
    public ApprovalTimeoutAction OnTimeout { get; set; } = ApprovalTimeoutAction.Fail;

    /// <summary>
    /// True when this draft was loaded from a <see cref="BatchStep"/> whose <see cref="BatchStep.StepType"/>
    /// the wizard does not understand (v0.2 data). Such definitions are blocked from editing to avoid
    /// lossy round-trips. Newly-created drafts are always editable.
    /// </summary>
    public bool IsUnsupported { get; set; }

    /// <summary>Generates a fresh short step-slug (12 chars of a Guid:N).</summary>
    public static string NewStepId() => $"step-{Guid.NewGuid():N}"[..12];

    /// <summary>Projects this draft into an Abstractions <see cref="BatchStep"/> with the given order.</summary>
    public BatchStep ToBatchStep(int order) => StepType switch
    {
        BatchStepType.Job => new BatchStep
        {
            StepId = StepId, Order = order, StepType = BatchStepType.Job,
            Job = new JobStepData
            {
                JobName = JobName.Trim(),
                TargetService = string.IsNullOrWhiteSpace(TargetService) ? null : TargetService,
                Parameters = BuildParameters(),
                MaxRetries = MaxRetries,
                TimeoutSeconds = TimeoutSeconds,
            },
            Compensation = BuildCompensation(),
            Condition = BuildCondition(),
        },
        BatchStepType.ParallelGroup => new BatchStep
        {
            StepId = StepId, Order = order, StepType = BatchStepType.ParallelGroup,
            ParallelGroup = new ParallelGroupData
            {
                JoinPolicy = JoinPolicy,
                Steps = Children.Select((c, i) => c.ToBatchStep(i)).ToList(),
            },
            Compensation = BuildCompensation(),
            Condition = BuildCondition(),
        },
        BatchStepType.ApprovalGate => new BatchStep
        {
            StepId = StepId, Order = order, StepType = BatchStepType.ApprovalGate,
            Approval = new ApprovalGateConfig
            {
                Title = ApprovalTitle.Trim(),
                Description = string.IsNullOrWhiteSpace(ApprovalDescription) ? null : ApprovalDescription,
                AllowedRoles = AnyAuthenticatedUser
                    ? new[] { ApprovalGateConfig.AnyAuthenticatedUser }
                    : AllowedRoles.Where(r => !string.IsNullOrWhiteSpace(r)).ToList(),
                TimeoutAfter = TimeoutSecondsApproval is { } s and > 0 ? TimeSpan.FromSeconds(s) : null,
                OnTimeout = OnTimeout,
            },
            Condition = BuildCondition(),
        },
        BatchStepType.Decision => new BatchStep
        {
            StepId = StepId, Order = order, StepType = BatchStepType.Decision,
            Decision = BuildDecision(),
            Compensation = BuildCompensation(),
            Condition = BuildCondition(),
        },
        _ => throw new InvalidOperationException($"Unsupported draft type {StepType}"),
    };

    /// <summary>Inverse projection: builds a mutable draft from a fetched <see cref="BatchStep"/> (edit-load).</summary>
    public static WizardStepDraft FromBatchStep(BatchStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        var draft = new WizardStepDraft { StepId = step.StepId, StepType = step.StepType };
        switch (step.StepType)
        {
            case BatchStepType.Job:
                draft.JobName = step.Job?.JobName ?? string.Empty;
                draft.TargetService = step.Job?.TargetService;
                draft.MaxRetries = step.Job?.MaxRetries;
                draft.TimeoutSeconds = step.Job?.TimeoutSeconds;
                if (step.Job?.Parameters is { } p)
                {
                    draft.Parameters = p
                        .Select(kv => new KeyValuePair<string, string>(kv.Key, StringifyValue(kv.Value)))
                        .ToList();
                }
                draft.Compensation = ToCompensationDraft(step.Compensation);
                draft.Condition = ToConditionDraft(step.Condition);
                break;
            case BatchStepType.ParallelGroup:
                draft.JoinPolicy = step.ParallelGroup?.JoinPolicy ?? ParallelJoinPolicy.WaitAll;
                draft.Children = step.ParallelGroup?.Steps
                    .OrderBy(c => c.Order)
                    .Select(FromBatchStep)
                    .ToList() ?? new();
                draft.Compensation = ToCompensationDraft(step.Compensation);
                draft.Condition = ToConditionDraft(step.Condition);
                break;
            case BatchStepType.ApprovalGate:
                draft.ApprovalTitle = step.Approval?.Title ?? string.Empty;
                draft.ApprovalDescription = step.Approval?.Description;
                var roles = step.Approval?.AllowedRoles ?? [];
                draft.AnyAuthenticatedUser = roles.Contains(ApprovalGateConfig.AnyAuthenticatedUser);
                draft.AllowedRoles = draft.AnyAuthenticatedUser
                    ? new()
                    : roles.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
                draft.TimeoutSecondsApproval = step.Approval?.TimeoutAfter is { } t ? (int)t.TotalSeconds : null;
                draft.OnTimeout = step.Approval?.OnTimeout ?? ApprovalTimeoutAction.Fail;
                draft.Condition = ToConditionDraft(step.Condition);
                break;
            case BatchStepType.Decision:
                draft.DecisionBranches = step.Decision?.Branches
                    .Select(ToBranchDraft)
                    .ToList() ?? new();
                draft.Compensation = ToCompensationDraft(step.Compensation);
                draft.Condition = ToConditionDraft(step.Condition);
                break;
            default:
                // Unknown future step type — mark unsupported so the wizard blocks editing.
                draft.IsUnsupported = true;
                break;
        }
        return draft;
    }

    /// <summary>
    /// Projects the editor parameter rows into the step's parameter dictionary. Render-safe: this runs
    /// during render (the Review step and the visual editor canvas project drafts to preview the DAG),
    /// so it must NEVER throw — an exception here tears down the Blazor circuit and loses the unsaved
    /// batch. Blank-key rows (the editor seeds new rows with an empty key) are dropped; duplicate keys
    /// are last-wins via indexer assignment. Returns <c>null</c> when no usable parameter remains, so a
    /// step with only empty editor rows emits no Parameters (same as having added none).
    /// </summary>
    private Dictionary<string, object?>? BuildParameters()
    {
        if (Parameters.Count == 0) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key)) continue;
            result[p.Key] = p.Value;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// Projects the compensator draft into a <see cref="CompensationStepData"/>. Render-safe (mirrors
    /// <see cref="BuildParameters"/>): never throws, so a preview render on an in-progress draft is safe.
    /// Returns <c>null</c> when there is no compensator or its job name is blank (a blank compensator is
    /// treated as "none" rather than emitting an invalid step).
    /// </summary>
    private CompensationStepData? BuildCompensation()
    {
        if (Compensation is not { } c || string.IsNullOrWhiteSpace(c.JobName)) return null;
        var parms = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in c.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key)) continue;
            parms[p.Key] = p.Value;
        }
        return new CompensationStepData
        {
            JobName = c.JobName.Trim(),
            TargetService = string.IsNullOrWhiteSpace(c.TargetService) ? null : c.TargetService,
            Parameters = parms.Count == 0 ? null : parms,
            MaxRetries = c.MaxRetries,
            TimeoutSeconds = c.TimeoutSeconds,
        };
    }

    // Inverse of BuildCompensation: rehydrates a mutable compensator draft from the fetched step so an
    // edit round-trips a builder-authored compensator instead of silently dropping it.
    private static CompensationDraft? ToCompensationDraft(CompensationStepData? comp)
    {
        if (comp is null) return null;
        return new CompensationDraft
        {
            JobName = comp.JobName,
            TargetService = comp.TargetService,
            MaxRetries = comp.MaxRetries,
            TimeoutSeconds = comp.TimeoutSeconds,
            Parameters = comp.Parameters?
                .Select(kv => new KeyValuePair<string, string>(kv.Key, StringifyValue(kv.Value)))
                .ToList() ?? new(),
        };
    }

    /// <summary>
    /// Projects the decision branch drafts into a <see cref="DecisionStepData"/>. Render-safe (mirrors
    /// <see cref="BuildParameters"/>): never throws, so a preview render on an in-progress draft is safe.
    /// Emits every branch verbatim (a blank job name / a blank condition key is surfaced by the validator, not
    /// dropped here — dropping a branch would silently change the routing) so the preview matches what saves.
    /// </summary>
    private DecisionStepData BuildDecision()
    {
        var branches = DecisionBranches.Select(BuildBranch).ToList();
        return new DecisionStepData { Branches = branches };
    }

    private static DecisionBranch BuildBranch(DecisionBranchDraft draft)
    {
        var parms = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in draft.Parameters)
        {
            if (string.IsNullOrWhiteSpace(p.Key)) continue;
            parms[p.Key] = p.Value;
        }
        return new DecisionBranch
        {
            StepId = draft.StepId,
            Label = string.IsNullOrWhiteSpace(draft.Label) ? null : draft.Label.Trim(),
            // A blank/whitespace parameter key means "no condition" — the else/default branch (mirrors
            // BuildCondition; the client validator's else detection uses the same rule for parity).
            When = draft.When is { } c && !string.IsNullOrWhiteSpace(c.ParameterKey)
                ? new StepCondition
                {
                    ParameterKey = c.ParameterKey.Trim(),
                    Operator = c.Operator,
                    Value = string.IsNullOrEmpty(c.Value) ? null : c.Value,
                }
                : null,
            Job = new JobStepData
            {
                JobName = draft.JobName.Trim(),
                TargetService = string.IsNullOrWhiteSpace(draft.TargetService) ? null : draft.TargetService,
                Parameters = parms.Count == 0 ? null : parms,
                MaxRetries = draft.MaxRetries,
                TimeoutSeconds = draft.TimeoutSeconds,
            },
        };
    }

    // Inverse of BuildBranch: rehydrates a mutable branch draft from a fetched branch so an edit round-trips
    // a builder-authored decision instead of silently dropping its branches.
    private static DecisionBranchDraft ToBranchDraft(DecisionBranch branch) => new()
    {
        StepId = branch.StepId,
        Label = branch.Label,
        When = ToConditionDraft(branch.When),
        JobName = branch.Job.JobName,
        TargetService = branch.Job.TargetService,
        MaxRetries = branch.Job.MaxRetries,
        TimeoutSeconds = branch.Job.TimeoutSeconds,
        Parameters = branch.Job.Parameters?
            .Select(kv => new KeyValuePair<string, string>(kv.Key, StringifyValue(kv.Value)))
            .ToList() ?? new(),
    };

    /// <summary>
    /// Projects the condition draft into a <see cref="StepCondition"/>. Render-safe (mirrors
    /// <see cref="BuildCompensation"/>): never throws, so a preview render on an in-progress draft is safe.
    /// Returns <c>null</c> when there is no condition or its parameter key is blank (treated as "no
    /// condition" rather than emitting an invalid one). A blank comparand becomes <c>null</c>; the validator
    /// flags a comparison operator that needs one.
    /// </summary>
    private StepCondition? BuildCondition()
    {
        if (Condition is not { } c || string.IsNullOrWhiteSpace(c.ParameterKey)) return null;
        return new StepCondition
        {
            ParameterKey = c.ParameterKey.Trim(),
            Operator = c.Operator,
            Value = string.IsNullOrEmpty(c.Value) ? null : c.Value,
        };
    }

    // Inverse of BuildCondition: rehydrates a mutable condition draft from a fetched step so an edit
    // round-trips a builder-authored condition instead of silently dropping it.
    private static ConditionDraft? ToConditionDraft(StepCondition? cond)
    {
        if (cond is null) return null;
        return new ConditionDraft
        {
            ParameterKey = cond.ParameterKey,
            Operator = cond.Operator,
            Value = cond.Value ?? string.Empty,
        };
    }

    private static string StringifyValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
