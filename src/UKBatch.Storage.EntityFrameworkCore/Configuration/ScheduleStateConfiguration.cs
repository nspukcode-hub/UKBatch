using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Json;

namespace UKBatch.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// EF mapping for <see cref="ScheduleStateEntity"/>: the definition-id PK and the non-null SQLite
/// ISO-8601 <see cref="System.DateTimeOffset"/> converter for the watermark. No JSON column and no
/// secondary index — the PK is the only lookup key (the store either reads every row or upserts by id).
/// </summary>
internal sealed class ScheduleStateConfiguration : IEntityTypeConfiguration<ScheduleStateEntity>
{
    private readonly bool _isSqlite;

    public ScheduleStateConfiguration(bool isSqlite) => _isSqlite = isSqlite;

    public void Configure(EntityTypeBuilder<ScheduleStateEntity> b)
    {
        b.ToTable("ScheduleStates");
        b.HasKey(e => e.BatchDefinitionId);
        b.Property(e => e.BatchDefinitionId).HasMaxLength(64);

        if (_isSqlite)
        {
            b.Property(e => e.LastFiredOccurrenceUtc).HasConversion(new Iso8601UtcDateTimeOffsetConverter());
        }
    }
}
