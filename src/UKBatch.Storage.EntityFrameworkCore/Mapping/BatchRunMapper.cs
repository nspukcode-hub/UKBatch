using UKBatch.Abstractions.Models;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Mapping;

/// <summary>Pure static entity ⇄ <see cref="BatchRun"/> mapping. No DbContext dependency — unit-testable in isolation.</summary>
internal static class BatchRunMapper
{
    public static BatchRunEntity ToEntity(BatchRun model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new BatchRunEntity
        {
            BatchId = model.BatchId,
            BatchDefinitionId = model.BatchDefinitionId,
            BatchName = model.BatchName,
            Status = model.Status,
            TriggeredBy = model.TriggeredBy,
            StartedAtUtc = model.StartedAtUtc,
            CompletedAtUtc = model.CompletedAtUtc,
            StepCount = model.StepCount,
            Total = model.Total,
            Succeeded = model.Succeeded,
            Failed = model.Failed,
            Cancelled = model.Cancelled,
        };
    }

    public static BatchRun ToModel(BatchRunEntity e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return new BatchRun
        {
            BatchId = e.BatchId,
            BatchDefinitionId = e.BatchDefinitionId,
            BatchName = e.BatchName,
            Status = e.Status,
            TriggeredBy = e.TriggeredBy,
            StartedAtUtc = e.StartedAtUtc,
            CompletedAtUtc = e.CompletedAtUtc,
            StepCount = e.StepCount,
            Total = e.Total,
            Succeeded = e.Succeeded,
            Failed = e.Failed,
            Cancelled = e.Cancelled,
        };
    }
}
