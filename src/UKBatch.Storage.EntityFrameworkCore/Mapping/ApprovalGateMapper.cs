using UKBatch.Abstractions.Storage;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Mapping;

/// <summary>
/// Pure static entity ⇄ <see cref="PersistedApprovalGate"/> mapping.
/// </summary>
internal static class ApprovalGateMapper
{
    public static ApprovalGateEntity ToEntity(PersistedApprovalGate model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new ApprovalGateEntity
        {
            ApprovalId = model.ApprovalId,
            BatchId = model.BatchId,
            BatchStepId = model.BatchStepId,
            BatchDefinitionId = model.BatchDefinitionId,
            Config = model.Config,
            Status = model.Status,
            PendingSinceUtc = model.PendingSinceUtc,
            DeadlineUtc = model.DeadlineUtc,
            Outcome = model.Outcome,
            DecidedBy = model.DecidedBy,
            DecidedAtUtc = model.DecidedAtUtc,
            Note = model.Note,
        };
    }

    public static PersistedApprovalGate ToModel(ApprovalGateEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new PersistedApprovalGate
        {
            ApprovalId = entity.ApprovalId,
            BatchId = entity.BatchId,
            BatchStepId = entity.BatchStepId,
            BatchDefinitionId = entity.BatchDefinitionId,
            Config = entity.Config,
            Status = entity.Status,
            PendingSinceUtc = entity.PendingSinceUtc,
            DeadlineUtc = entity.DeadlineUtc,
            Outcome = entity.Outcome,
            DecidedBy = entity.DecidedBy,
            DecidedAtUtc = entity.DecidedAtUtc,
            Note = entity.Note,
        };
    }

    /// <summary>
    /// Copies all non-PK fields from an incoming gate onto the tracked entity (the idempotent
    /// <c>SaveAsync</c> upsert-overwrite path). Writes EXACTLY: <c>BatchId, BatchStepId,
    /// BatchDefinitionId, Config, Status, PendingSinceUtc, DeadlineUtc, Outcome, DecidedBy,
    /// DecidedAtUtc, Note</c>. Does NOT write <c>ApprovalId</c> (PK). <see cref="ApprovalGateEntity"/>
    /// has no concurrency token (gate records are single-writer per lifecycle event), so all non-PK
    /// fields are safe to overwrite.
    /// </summary>
    public static void CopyInto(PersistedApprovalGate src, ApprovalGateEntity dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        dst.BatchId = src.BatchId;
        dst.BatchStepId = src.BatchStepId;
        dst.BatchDefinitionId = src.BatchDefinitionId;
        dst.Config = src.Config;
        dst.Status = src.Status;
        dst.PendingSinceUtc = src.PendingSinceUtc;
        dst.DeadlineUtc = src.DeadlineUtc;
        dst.Outcome = src.Outcome;
        dst.DecidedBy = src.DecidedBy;
        dst.DecidedAtUtc = src.DecidedAtUtc;
        dst.Note = src.Note;
    }
}
