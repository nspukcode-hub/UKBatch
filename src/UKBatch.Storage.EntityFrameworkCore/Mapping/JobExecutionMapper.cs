using UKBatch.Abstractions.Models;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Mapping;

/// <summary>
/// Pure static entity ⇄ <see cref="JobExecution"/> mapping. No DbContext dependency — unit-testable in
/// isolation. Field-for-field with the Abstractions record.
/// </summary>
internal static class JobExecutionMapper
{
    public static JobExecutionEntity ToEntity(JobExecution model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new JobExecutionEntity
        {
            ExecutionId = model.ExecutionId,
            JobName = model.JobName,
            BatchId = model.BatchId,
            BatchStepId = model.BatchStepId,
            BatchDefinitionId = model.BatchDefinitionId,
            Status = model.Status,
            Parameters = model.Parameters,
            EnqueuedAtUtc = model.EnqueuedAtUtc,
            StartedAtUtc = model.StartedAtUtc,
            CompletedAtUtc = model.CompletedAtUtc,
            AttemptNumber = model.AttemptNumber,
            MaxRetries = model.MaxRetries,
            LastError = model.LastError,
            Processed = model.Processed,
            Failed = model.Failed,
            Total = model.Total,
            TriggeredBy = model.TriggeredBy,
            WorkerName = model.WorkerName,
        };
    }

    public static JobExecution ToModel(JobExecutionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new JobExecution
        {
            ExecutionId = entity.ExecutionId,
            JobName = entity.JobName,
            BatchId = entity.BatchId,
            BatchStepId = entity.BatchStepId,
            BatchDefinitionId = entity.BatchDefinitionId,
            Status = entity.Status,
            Parameters = entity.Parameters,
            EnqueuedAtUtc = entity.EnqueuedAtUtc,
            StartedAtUtc = entity.StartedAtUtc,
            CompletedAtUtc = entity.CompletedAtUtc,
            AttemptNumber = entity.AttemptNumber,
            MaxRetries = entity.MaxRetries,
            LastError = entity.LastError,
            Processed = entity.Processed,
            Failed = entity.Failed,
            Total = entity.Total,
            TriggeredBy = entity.TriggeredBy,
            WorkerName = entity.WorkerName,
        };
    }
}
