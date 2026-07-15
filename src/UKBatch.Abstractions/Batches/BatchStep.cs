namespace UKBatch.Abstractions.Batches;

/// <summary>
/// A single step inside a <see cref="BatchDefinition"/>. The active variant is determined by
/// <see cref="StepType"/>.
/// </summary>
/// <remarks>
/// Discriminated by <see cref="StepType"/>. For known step types, exactly one of <see cref="Job"/>,
/// <see cref="ParallelGroup"/>, <see cref="Approval"/>, <see cref="Decision"/> is non-null. Future versions may
/// add new <see cref="BatchStepType"/> variants with their own payload fields. Consumers reading a step whose
/// <see cref="StepType"/> is unrecognised MUST NOT throw at deserialization; the runtime executor will
/// fail that step with a structured error and continue per <see cref="BatchFailurePolicy"/>.
/// Storage adapters MUST round-trip <see cref="Metadata"/> verbatim so a v0.1 reader does not destroy
/// v0.2 step data on update.
/// </remarks>
public sealed record class BatchStep
{
    /// <summary>Stable identifier within the batch (GUID or "step-1" slug). Caller-supplied.</summary>
    public required string StepId { get; init; }

    /// <summary>Zero-based ordering within the parent step list.</summary>
    public required int Order { get; init; }

    /// <summary>Discriminator tag.</summary>
    public required BatchStepType StepType { get; init; }

    /// <summary>Set when <see cref="StepType"/> is <see cref="BatchStepType.Job"/>; otherwise <c>null</c>.</summary>
    public JobStepData? Job { get; init; }

    /// <summary>Set when <see cref="StepType"/> is <see cref="BatchStepType.ParallelGroup"/>; otherwise <c>null</c>.</summary>
    public ParallelGroupData? ParallelGroup { get; init; }

    /// <summary>Set when <see cref="StepType"/> is <see cref="BatchStepType.ApprovalGate"/>; otherwise <c>null</c>.</summary>
    public ApprovalGateConfig? Approval { get; init; }

    /// <summary>Set when <see cref="StepType"/> is <see cref="BatchStepType.Decision"/>; otherwise <c>null</c>.</summary>
    public DecisionStepData? Decision { get; init; }

    /// <summary>
    /// Optional compensator for this step, honored only on a top-level <see cref="BatchStepType.Job"/>,
    /// <see cref="BatchStepType.ParallelGroup"/>, or <see cref="BatchStepType.Decision"/> step (a parallel group
    /// and a decision each compensate as one unit). A decision-level compensator is branch-blind — it runs when
    /// a later step fails regardless of which branch won, so it must undo whichever branch ran (discriminate via
    /// the forwarded outputs) or be idempotent. <c>null</c> means "cannot be undone" — the step is skipped
    /// during a reverse unwind. Forbidden on ApprovalGate steps, parallel-group CHILDREN, decision branches, and
    /// OnFailure (compensation-chain) steps; the validator rejects those. Inert unless
    /// <see cref="BatchDefinition.FailurePolicy"/> is <see cref="BatchFailurePolicy.Compensate"/>.
    /// </summary>
    public CompensationStepData? Compensation { get; init; }

    /// <summary>
    /// Optional run-if guard: the step runs only when this condition holds at dispatch time; otherwise it is
    /// skipped (recorded as <see cref="Models.JobStatus.Skipped"/>) and the batch proceeds to the next step.
    /// <c>null</c> means the step always runs (unchanged default behavior). Honored on a top-level Job,
    /// ParallelGroup (the whole group is skipped as one unit), or ApprovalGate step; forbidden on
    /// parallel-group children and OnFailure steps — the validator rejects those. A skipped step is never
    /// compensated during a saga unwind.
    /// </summary>
    public StepCondition? Condition { get; init; }

    /// <summary>
    /// Storage-adapter opaque metadata. Round-tripped verbatim by adapters; reserved for future
    /// step-type payloads and per-step annotations. Consumers in v0.1 SHOULD NOT depend on keys here.
    /// </summary>
    public IReadOnlyDictionary<string, object?>? Metadata { get; init; }
}

/// <summary>Payload for a <see cref="BatchStepType.Job"/> step.</summary>
public sealed record class JobStepData
{
    /// <summary>Logical job name to dispatch.</summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Target service for cross-service transport; <c>null</c> means local execution
    /// (matches <see cref="Transport.JobMessage.TargetService"/>).
    /// </summary>
    public string? TargetService { get; init; }

    /// <summary>Static parameters merged with runtime parameters at dispatch. <c>null</c> means no parameters.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>
    /// Max retry attempts for this step's execution; <c>null</c> means inherit the job's
    /// <see cref="Jobs.JobAttribute.MaxRetries"/> or runtime default.
    /// </summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Wall-clock timeout for this step in seconds; <c>null</c> means inherit job/runtime default.
    /// <c>0</c> means explicitly no timeout.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}

/// <summary>Payload for a <see cref="BatchStepType.ParallelGroup"/> step.</summary>
public sealed record class ParallelGroupData
{
    /// <summary>
    /// Child steps run concurrently. Nested <see cref="BatchStepType.ParallelGroup"/> steps are
    /// rejected by the runtime at registration time in v0.1 (single-level only).
    /// </summary>
    public required IReadOnlyList<BatchStep> Steps { get; init; }

    /// <summary>Join semantics when fanning back in.</summary>
    public required ParallelJoinPolicy JoinPolicy { get; init; }
}

/// <summary>
/// Optional compensator for a top-level <see cref="BatchStep"/>: the job to run if a LATER step fails and
/// the batch's <see cref="BatchDefinition.FailurePolicy"/> is <see cref="BatchFailurePolicy.Compensate"/>.
/// Compensators run in REVERSE order of the completed steps, so the most recently completed step is undone
/// first. A compensator runs ONLY for a step that itself completed — the failed step is never compensated
/// (a step that failed part-way is responsible for rolling back its own partial writes; the saga undoes only
/// whole completed steps). A step with no compensator is simply skipped during unwind ("some work cannot be
/// undone"). Modelled separately from <see cref="JobStepData"/> so a compensator can evolve independently.
/// </summary>
public sealed record class CompensationStepData
{
    /// <summary>Logical job name to dispatch as the compensator.</summary>
    public required string JobName { get; init; }

    /// <summary>
    /// Target service for a cross-service compensator; <c>null</c> means local execution
    /// (matches <see cref="Transport.JobMessage.TargetService"/>).
    /// </summary>
    public string? TargetService { get; init; }

    /// <summary>Static parameters merged with the run's forwarded state at dispatch. <c>null</c> means none.</summary>
    public IReadOnlyDictionary<string, object?>? Parameters { get; init; }

    /// <summary>Max retry attempts for the compensator; <c>null</c> inherits the job/runtime default.</summary>
    public int? MaxRetries { get; init; }

    /// <summary>
    /// Wall-clock timeout for the compensator in seconds; <c>null</c> inherits job/runtime default.
    /// <c>0</c> means explicitly no timeout.
    /// </summary>
    public int? TimeoutSeconds { get; init; }
}
