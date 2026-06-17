using UKBatch.Abstractions.Batches;

namespace UKBatch.Runtime;

/// <summary>
/// Internal record holding the in-flight state of a pending approval gate. One per active gate.
/// </summary>
internal sealed record class ApprovalGateRegistration
{
    /// <summary>Caller-issued gate identifier.</summary>
    public required string ApprovalId { get; init; }

    /// <summary>Parent batch id.</summary>
    public required string BatchId { get; init; }

    /// <summary>Parent batch step id.</summary>
    public required string StepId { get; init; }

    /// <summary>
    /// Definition display name of the parent batch — surfaced on the pending-approval snapshot the
    /// dashboard renders. Threaded from <c>BatchExecutor</c> (which holds the <c>BatchDefinition</c>),
    /// because <see cref="BatchId"/> is a RUN id and cannot resolve the name via the definition lookup.
    /// </summary>
    public required string BatchName { get; init; }

    /// <summary>Definition id of the parent batch — persisted on the durable record.</summary>
    public required string BatchDefinitionId { get; init; }

    /// <summary>Configuration that created the gate.</summary>
    public required ApprovalGateConfig Config { get; init; }

    /// <summary>UTC time the gate became pending.</summary>
    public required DateTimeOffset PendingSinceUtc { get; init; }

    /// <summary>UTC deadline; <c>null</c> for indefinite gates.</summary>
    public required DateTimeOffset? DeadlineUtc { get; init; }

    /// <summary>Completion source resolved by Approve / Reject / Timeout / Cancel.</summary>
    public required TaskCompletionSource<ApprovalOutcome> Tcs { get; init; }

    /// <summary>CTS that cancels the per-gate timeout Task.Delay.</summary>
    public required CancellationTokenSource GateCts { get; init; }

    /// <summary>
    /// Decision identity captured by <c>ApproveAsync</c>/<c>RejectAsync</c> just before they
    /// resolve <see cref="Tcs"/>, so the centralized durable-record write in the
    /// <c>AwaitApprovalAsync</c> resolution path can attribute the outcome. <c>null</c> for outcomes
    /// produced WITHOUT a human (auto-approve / timeout-fail / cancellation) — those use sentinels.
    /// Made single-writer by <see cref="TryClaimDecision"/>: only the caller that wins the claim writes
    /// these fields, so two concurrent decisions can never cross outcome and attribution.
    /// </summary>
    public string? DecidedBy { get; set; }

    /// <summary>Decision note (approve) or reason (reject) captured before TCS resolution; <c>null</c> otherwise.</summary>
    public string? DecisionNote { get; set; }

    private int _decisionClaimed;

    /// <summary>
    /// Atomically claims the right to record the human decision (set <see cref="DecidedBy"/>/
    /// <see cref="DecisionNote"/> and resolve <see cref="Tcs"/>). Returns <c>true</c> for the FIRST caller,
    /// <c>false</c> for any concurrent later one — which must then no-op. Without this, two concurrent
    /// approve/reject calls both write the attribution fields and the LOSER (whose
    /// <c>TrySetResult</c> didn't take) could overwrite the WINNER's — the outcome stays correct (the TCS
    /// has a single winner) but the audit record would record the wrong decider/note. The timeout and
    /// cancellation paths deliberately do NOT claim: they attribute with sentinels
    /// (&lt;system&gt;/&lt;cancelled&gt;) and never read these fields.
    /// </summary>
    public bool TryClaimDecision() => Interlocked.CompareExchange(ref _decisionClaimed, 1, 0) == 0;
}
