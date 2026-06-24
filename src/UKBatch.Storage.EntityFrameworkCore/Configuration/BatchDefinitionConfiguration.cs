using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Json;

namespace UKBatch.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// EF mapping for <see cref="BatchDefinitionEntity"/>: PK, the JSON <c>Steps</c>/<c>OnFailureSteps</c>
/// columns, the <see cref="BatchDefinitionEntity.Version"/> concurrency token, the unique
/// <c>(Source, Name)</c> index (mirrors InMemory name-per-source uniqueness), and the source-scoped
/// paging index.
/// </summary>
internal sealed class BatchDefinitionConfiguration : IEntityTypeConfiguration<BatchDefinitionEntity>
{
    private readonly string _jsonType;
    private readonly bool _isSqlite;

    public BatchDefinitionConfiguration(string jsonType, bool isSqlite)
    {
        _jsonType = jsonType;
        _isSqlite = isSqlite;
    }

    public void Configure(EntityTypeBuilder<BatchDefinitionEntity> b)
    {
        b.ToTable("BatchDefinitions");
        b.HasKey(e => e.Id);
        b.Property(e => e.Id).HasMaxLength(64);
        b.Property(e => e.Name).HasMaxLength(512).IsRequired();
        b.Property(e => e.Source).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.Schedule).HasMaxLength(256);
        b.Property(e => e.ScheduleEnabled).HasDefaultValue(true);   // existing rows backfill to enabled
        b.Property(e => e.ScheduleCatchUpWindowTicks);   // nullable ticks; no special mapping needed
        b.Property(e => e.FailurePolicy).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.CreatedBy).HasMaxLength(256);

        var (sConv, sCmp) = JsonColumn.ForStepList();
        b.Property(e => e.Steps).HasConversion(sConv, sCmp).HasColumnType(_jsonType).IsRequired();
        var (fConv, fCmp) = JsonColumn.ForStepList();
        b.Property(e => e.OnFailureSteps).HasConversion(fConv, fCmp).HasColumnType(_jsonType).IsRequired();

        // Metadata JSON column. Nullable at the DB level so existing rows from
        // earlier migrations carry NULL until first write; the mapper normalizes null/empty round-trips
        // so the JsonColumn.ForDictionary() non-null factory stays unchanged.
        var (mConv, mCmp) = JsonColumn.ForDictionary();
        b.Property(e => e.Metadata).HasConversion(mConv, mCmp).HasColumnType(_jsonType);

        if (_isSqlite)
        {
            b.Property(e => e.CreatedAtUtc).HasConversion(new Iso8601UtcDateTimeOffsetConverter());
        }

        // OPTIMISTIC CONCURRENCY: Version as the token (provider-agnostic).
        b.Property(e => e.Version).IsConcurrencyToken();

        // Name unique-within-source (mirrors InMemory _byNamePerSource). Named explicitly so the
        // exception classifier can disambiguate a name collision from a PK collision.
        b.HasIndex(e => new { e.Source, e.Name })
            .IsUnique()
            .HasDatabaseName("IX_BatchDefinitions_Source_Name");
        b.HasIndex(e => new { e.Source, e.CreatedAtUtc });   // ListAsync paging by source
    }
}
