namespace UKBatch.Abstractions.Batches;

/// <summary>
/// A single step inside a <see cref="BatchDefinition"/>. The active variant is determined by
/// <see cref="StepType"/>.
/// </summary>
/// <remarks>
/// Discriminated by <see cref="StepType"/>. For known v0.1 step types, exactly one of <see cref="Job"/>,
/// <see cref="ParallelGroup"/>, <see cref="Approval"/> is non-null. Future versions may add new
/// <see cref="BatchStepType"/> variants with their own payload fields. Consumers reading a step whose
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
