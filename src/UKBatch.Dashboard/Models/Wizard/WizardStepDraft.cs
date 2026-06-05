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
/// Step Output Forwarding is v0.2: <see cref="Parameters"/> are static string key/values (emitted
/// as <c>object?</c> = the string). No typed/JSON value parsing in v0.1.
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

    // ── ParallelGroup ──
    /// <summary>Fan-in join semantics.</summary>
    public ParallelJoinPolicy JoinPolicy { get; set; } = ParallelJoinPolicy.WaitAll;

    /// <summary>Job-only child drafts (single-level — nested groups forbidden in v0.1).</summary>
    public List<WizardStepDraft> Children { get; set; } = new();

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
        },
        BatchStepType.ParallelGroup => new BatchStep
        {
            StepId = StepId, Order = order, StepType = BatchStepType.ParallelGroup,
            ParallelGroup = new ParallelGroupData
            {
                JoinPolicy = JoinPolicy,
                Steps = Children.Select((c, i) => c.ToBatchStep(i)).ToList(),
            },
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
                break;
            case BatchStepType.ParallelGroup:
                draft.JoinPolicy = step.ParallelGroup?.JoinPolicy ?? ParallelJoinPolicy.WaitAll;
                draft.Children = step.ParallelGroup?.Steps
                    .OrderBy(c => c.Order)
                    .Select(FromBatchStep)
                    .ToList() ?? new();
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

    private static string StringifyValue(object? value) => value switch
    {
        null => string.Empty,
        string s => s,
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
