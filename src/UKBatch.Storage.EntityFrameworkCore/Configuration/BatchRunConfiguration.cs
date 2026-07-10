using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Json;

namespace UKBatch.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// EF mapping for <see cref="BatchRunEntity"/>: PK, length caps, nullable enum→string status, the SQLite
/// ISO-8601 <see cref="System.DateTimeOffset"/> converters, the two run-history indexes, and the JSON
/// <c>ForwardedState</c> column (durable forwarded parameters + outputs for resume). Provider-specific
/// column types are injected so ONE config serves both providers.
/// </summary>
internal sealed class BatchRunConfiguration : IEntityTypeConfiguration<BatchRunEntity>
{
    private readonly string _jsonType;
    private readonly bool _isSqlite;

    public BatchRunConfiguration(string jsonType, bool isSqlite)
    {
        _jsonType = jsonType;
        _isSqlite = isSqlite;
    }

    public void Configure(EntityTypeBuilder<BatchRunEntity> b)
    {
        b.ToTable("BatchRuns");
        b.HasKey(e => e.BatchId);
        b.Property(e => e.BatchId).HasMaxLength(64);                 // UUIDv7 "N" = 32; 64 headroom
        b.Property(e => e.BatchDefinitionId).HasMaxLength(64).IsRequired();
        b.Property(e => e.BatchName).HasMaxLength(512).IsRequired();
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32);   // nullable enum→string; null column allowed
        b.Property(e => e.TriggeredBy).HasMaxLength(256);
        b.Property(e => e.CurrentStepIndex);                                  // nullable int resume cursor; no index needed
        b.Property(e => e.CompensationStepIndex);                             // nullable int unwind cursor
        b.Property(e => e.RetryOfBatchId).HasMaxLength(64);                   // nullable retry lineage link

        var (conv, cmp) = JsonColumn.ForDictionary();
        b.Property(e => e.ForwardedState).HasConversion(conv, cmp).HasColumnType(_jsonType).IsRequired(false);

        if (_isSqlite)
        {
            b.Property(e => e.StartedAtUtc).HasConversion(new Iso8601UtcDateTimeOffsetConverter());
            b.Property(e => e.CompletedAtUtc).HasConversion(new Iso8601UtcNullableDateTimeOffsetConverter());
        }

        // Run-history indexes:
        b.HasIndex(e => new { e.BatchDefinitionId, e.StartedAtUtc });   // runs of one definition, newest first
        b.HasIndex(e => new { e.Status, e.StartedAtUtc });              // status-filtered run lists
    }
}
