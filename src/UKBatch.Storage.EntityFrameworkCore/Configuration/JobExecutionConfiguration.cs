using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Json;

namespace UKBatch.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// EF mapping for <see cref="JobExecutionEntity"/>: PK, length caps, enum→string, the JSON
/// <c>Parameters</c> column, the SQLite ISO-8601 <see cref="DateTimeOffset"/> converters, and the
/// pagination index plan. Provider-specific column types are injected so ONE config
/// serves both providers.
/// </summary>
internal sealed class JobExecutionConfiguration : IEntityTypeConfiguration<JobExecutionEntity>
{
    private readonly string _jsonType;
    private readonly bool _isSqlite;

    public JobExecutionConfiguration(string jsonType, bool isSqlite)
    {
        _jsonType = jsonType;
        _isSqlite = isSqlite;
    }

    public void Configure(EntityTypeBuilder<JobExecutionEntity> b)
    {
        b.ToTable("JobExecutions");
        b.HasKey(e => e.ExecutionId);
        b.Property(e => e.ExecutionId).HasMaxLength(64);             // UUIDv7 "N" = 32; 64 headroom
        b.Property(e => e.JobName).HasMaxLength(512).IsRequired();
        b.Property(e => e.BatchId).HasMaxLength(64);
        b.Property(e => e.BatchStepId).HasMaxLength(64);
        b.Property(e => e.BatchDefinitionId).HasMaxLength(64);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();   // enum→string
        b.Property(e => e.TriggeredBy).HasMaxLength(256);
        b.Property(e => e.WorkerName).HasMaxLength(256);

        var (conv, cmp) = JsonColumn.ForDictionary();
        b.Property(e => e.Parameters).HasConversion(conv, cmp).HasColumnType(_jsonType).IsRequired();
        b.Property(e => e.Outputs).HasConversion(conv, cmp).HasColumnType(_jsonType).IsRequired(false);   // job-produced outputs (nullable)

        if (_isSqlite)
        {
            b.Property(e => e.EnqueuedAtUtc).HasConversion(new Iso8601UtcDateTimeOffsetConverter());
            b.Property(e => e.StartedAtUtc).HasConversion(new Iso8601UtcNullableDateTimeOffsetConverter());
            b.Property(e => e.CompletedAtUtc).HasConversion(new Iso8601UtcNullableDateTimeOffsetConverter());
        }

        // Index plan (pagination perf — JobQuery filter/sort surface):
        b.HasIndex(e => new { e.Status, e.EnqueuedAtUtc });             // status filter + time sort
        b.HasIndex(e => new { e.BatchDefinitionId, e.EnqueuedAtUtc });  // "last N runs of a definition" (dashboard detail)
        b.HasIndex(e => new { e.JobName, e.EnqueuedAtUtc });            // per-job history
        b.HasIndex(e => e.BatchId);                                     // batch run roll-up
    }
}
