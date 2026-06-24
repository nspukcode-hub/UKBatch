using UKBatch.Abstractions.Batches;
using UKBatch.Storage.EntityFrameworkCore.Entities;

namespace UKBatch.Storage.EntityFrameworkCore.Mapping;

/// <summary>
/// Pure static entity ⇄ <see cref="BatchDefinition"/> mapping.
/// </summary>
internal static class BatchDefinitionMapper
{
    public static BatchDefinitionEntity ToEntity(BatchDefinition model)
    {
        ArgumentNullException.ThrowIfNull(model);
        return new BatchDefinitionEntity
        {
            Id = model.Id,
            Name = model.Name,
            Source = model.Source,
            Schedule = model.Schedule,
            ScheduleEnabled = model.ScheduleEnabled,
            ScheduleCatchUpWindowTicks = model.ScheduleCatchUpWindow?.Ticks,
            Steps = model.Steps,
            FailurePolicy = model.FailurePolicy,
            OnFailureSteps = model.OnFailureSteps,
            CreatedAtUtc = model.CreatedAtUtc,
            CreatedBy = model.CreatedBy,
            Version = model.Version,
            // Null Metadata → empty dict for the JsonColumn non-null factory.
            // ToModel reverses (empty → null) for parity with InMemory's nullable round-trip.
            Metadata = model.Metadata ?? new Dictionary<string, object?>(StringComparer.Ordinal),
        };
    }

    public static BatchDefinition ToModel(BatchDefinitionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return new BatchDefinition
        {
            Id = entity.Id,
            Name = entity.Name,
            Source = entity.Source,
            Schedule = entity.Schedule,
            ScheduleEnabled = entity.ScheduleEnabled,
            ScheduleCatchUpWindow = entity.ScheduleCatchUpWindowTicks is { } t ? TimeSpan.FromTicks(t) : null,
            Steps = entity.Steps,
            FailurePolicy = entity.FailurePolicy,
            OnFailureSteps = entity.OnFailureSteps,
            CreatedAtUtc = entity.CreatedAtUtc,
            CreatedBy = entity.CreatedBy,
            Version = entity.Version,
            // Empty dict → null on read (round-trip parity with ToEntity).
            Metadata = entity.Metadata is { Count: > 0 } md ? md : null,
        };
    }

    /// <summary>
    /// Copies the EDITABLE fields from an incoming update onto the tracked entity (the update path).
    /// Writes EXACTLY: <c>Name, Source, Schedule, ScheduleEnabled, ScheduleCatchUpWindowTicks, Steps,
    /// FailurePolicy, OnFailureSteps, Metadata</c>. Does NOT write <c>Id</c> (PK — identity), <c>Version</c> (the concurrency token
    /// — the update path sets <c>entity.Version = definition.Version + 1</c> AND
    /// <c>OriginalValue</c> explicitly, so an accidental copy here would clobber the tracked
    /// original-value and defeat the token), or <c>CreatedAtUtc</c>/<c>CreatedBy</c> (create-time
    /// immutable; an update never changes creation metadata — matches InMemory).
    /// </summary>
    /// <remarks>
    /// Metadata is EDITABLE on update — the drag-persist path writes
    /// new layout hints via <c>UpdateBatchAsync</c>. Omitting this assignment silently loses every
    /// layout hint the operator drags: the tracked entity's Metadata never changes, so the JSON
    /// column is flushed unchanged and the next page load reads the pre-drag hints. Pinned by
    /// regression test <c>CopyEditableFields_PreservesMetadata_DragPersistRoundTrip</c>.
    /// </remarks>
    public static void CopyEditableFields(BatchDefinition src, BatchDefinitionEntity dst)
    {
        ArgumentNullException.ThrowIfNull(src);
        ArgumentNullException.ThrowIfNull(dst);
        dst.Name = src.Name;
        dst.Source = src.Source;
        dst.Schedule = src.Schedule;
        dst.ScheduleEnabled = src.ScheduleEnabled;
        dst.ScheduleCatchUpWindowTicks = src.ScheduleCatchUpWindow?.Ticks;
        dst.Steps = src.Steps;
        dst.FailurePolicy = src.FailurePolicy;
        dst.OnFailureSteps = src.OnFailureSteps;
        // Metadata MUST be written here or drag-persist silently no-ops.
        // Null-coalesce normalize matches the ToEntity discipline.
        dst.Metadata = src.Metadata ?? new Dictionary<string, object?>(StringComparer.Ordinal);
    }
}
