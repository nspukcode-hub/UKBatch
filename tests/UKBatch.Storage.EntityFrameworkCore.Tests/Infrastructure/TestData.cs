using UKBatch.Abstractions.Batches;
using UKBatch.Abstractions.Models;
using UKBatch.Abstractions.Storage;

namespace UKBatch.Storage.EntityFrameworkCore.Tests.Infrastructure;

/// <summary>Shared builders for the EF store test data (mirrors the InMemory test patterns).</summary>
internal static class TestData
{
    public static JobExecution Execution(
        string executionId,
        string jobName = "Test.Job",
        string? batchId = null,
        string? batchStepId = null,
        string? batchDefinitionId = null,
        JobStatus status = JobStatus.Pending,
        DateTimeOffset? enqueuedAtUtc = null,
        IReadOnlyDictionary<string, object?>? parameters = null,
        int attemptNumber = 1,
        int maxRetries = 0,
        string? lastError = null,
        long processed = 0,
        long failed = 0,
        long? total = null,
        string? workerName = null,
        string? triggeredBy = null) => new()
        {
            ExecutionId = executionId,
            JobName = jobName,
            BatchId = batchId,
            BatchStepId = batchStepId,
            BatchDefinitionId = batchDefinitionId,
            Status = status,
            Parameters = parameters ?? new Dictionary<string, object?>(),
            EnqueuedAtUtc = enqueuedAtUtc ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            AttemptNumber = attemptNumber,
            MaxRetries = maxRetries,
            LastError = lastError,
            Processed = processed,
            Failed = failed,
            Total = total,
            WorkerName = workerName,
            TriggeredBy = triggeredBy,
        };

    public static JobDefinition JobDef(string name = "Test.Job", int maxRetries = 0, IReadOnlyDictionary<string, object?>? parameters = null) => new()
    {
        Name = name,
        IsPartitioned = false,
        MaxRetries = maxRetries,
        TimeoutSeconds = 0,
        DefaultParameters = parameters ?? new Dictionary<string, object?>(),
        Tags = Array.Empty<string>(),
    };

    public static BatchDefinition BatchDef(
        string id,
        string name,
        BatchSource source = BatchSource.Dashboard,
        int version = 0,
        IReadOnlyList<BatchStep>? steps = null,
        string? schedule = null,
        BatchFailurePolicy failurePolicy = BatchFailurePolicy.StopOnFailure,
        IReadOnlyList<BatchStep>? onFailureSteps = null,
        string? createdBy = null) => new()
        {
            Id = id,
            Name = name,
            Source = source,
            Schedule = schedule,
            Steps = steps ?? Array.Empty<BatchStep>(),
            FailurePolicy = failurePolicy,
            OnFailureSteps = onFailureSteps ?? Array.Empty<BatchStep>(),
            CreatedAtUtc = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            CreatedBy = createdBy,
            Version = version,
        };

    public static PersistedApprovalGate Gate(
        string approvalId,
        string batchId = "batch-1",
        string batchStepId = "step-1",
        string? batchDefinitionId = null,
        ApprovalGateConfig? config = null,
        ApprovalRecordStatus status = ApprovalRecordStatus.Pending,
        DateTimeOffset? pendingSinceUtc = null,
        DateTimeOffset? deadlineUtc = null,
        ApprovalRecordOutcome? outcome = null,
        string? decidedBy = null,
        DateTimeOffset? decidedAtUtc = null,
        string? note = null) => new()
        {
            ApprovalId = approvalId,
            BatchId = batchId,
            BatchStepId = batchStepId,
            BatchDefinitionId = batchDefinitionId,
            Config = config ?? GateConfig(),
            Status = status,
            PendingSinceUtc = pendingSinceUtc ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            DeadlineUtc = deadlineUtc,
            Outcome = outcome,
            DecidedBy = decidedBy,
            DecidedAtUtc = decidedAtUtc,
            Note = note,
        };

    public static ApprovalGateConfig GateConfig(
        string title = "Confirm",
        IReadOnlyList<string>? allowedRoles = null,
        TimeSpan? timeoutAfter = null,
        ApprovalTimeoutAction onTimeout = ApprovalTimeoutAction.Hold) => new()
        {
            Title = title,
            AllowedRoles = allowedRoles ?? new[] { "admin" },
            TimeoutAfter = timeoutAfter,
            OnTimeout = onTimeout,
        };

    /// <summary>A job step (single job dispatch).</summary>
    public static BatchStep JobStep(string stepId, int order, string jobName, IReadOnlyDictionary<string, object?>? parameters = null) => new()
    {
        StepId = stepId,
        Order = order,
        StepType = BatchStepType.Job,
        Job = new JobStepData { JobName = jobName, Parameters = parameters },
    };

    /// <summary>A single-level parallel group with the given child job steps.</summary>
    public static BatchStep ParallelStep(string stepId, int order, ParallelJoinPolicy joinPolicy, params BatchStep[] children) => new()
    {
        StepId = stepId,
        Order = order,
        StepType = BatchStepType.ParallelGroup,
        ParallelGroup = new ParallelGroupData { Steps = children, JoinPolicy = joinPolicy },
    };

    /// <summary>An approval-gate step.</summary>
    public static BatchStep ApprovalStep(string stepId, int order, ApprovalGateConfig? config = null) => new()
    {
        StepId = stepId,
        Order = order,
        StepType = BatchStepType.ApprovalGate,
        Approval = config ?? GateConfig(),
    };
}
