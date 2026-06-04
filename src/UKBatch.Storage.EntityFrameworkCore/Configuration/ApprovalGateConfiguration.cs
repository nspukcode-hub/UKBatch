using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using UKBatch.Storage.EntityFrameworkCore.Entities;
using UKBatch.Storage.EntityFrameworkCore.Json;

namespace UKBatch.Storage.EntityFrameworkCore.Configuration;

/// <summary>
/// EF mapping for <see cref="ApprovalGateEntity"/>: PK, enum→string (incl. nullable
/// <see cref="ApprovalGateEntity.Outcome"/>), the JSON <c>Config</c> column, the SQLite ISO-8601
/// <see cref="DateTimeOffset"/> converters, and the <c>Status</c> index for the
/// <c>ListPendingAsync</c> hot path.
/// </summary>
internal sealed class ApprovalGateConfiguration : IEntityTypeConfiguration<ApprovalGateEntity>
{
    private readonly string _jsonType;
    private readonly bool _isSqlite;

    public ApprovalGateConfiguration(string jsonType, bool isSqlite)
    {
        _jsonType = jsonType;
        _isSqlite = isSqlite;
    }

    public void Configure(EntityTypeBuilder<ApprovalGateEntity> b)
    {
        b.ToTable("ApprovalGates");
        b.HasKey(e => e.ApprovalId);
        b.Property(e => e.ApprovalId).HasMaxLength(64);
        b.Property(e => e.BatchId).HasMaxLength(64).IsRequired();
        b.Property(e => e.BatchStepId).HasMaxLength(64).IsRequired();
        b.Property(e => e.BatchDefinitionId).HasMaxLength(64);
        b.Property(e => e.Status).HasConversion<string>().HasMaxLength(32).IsRequired();
        b.Property(e => e.Outcome).HasConversion<string>().HasMaxLength(32);      // nullable enum→string
        b.Property(e => e.DecidedBy).HasMaxLength(256);
        b.Property(e => e.Note).HasMaxLength(4096);

        var (cConv, cCmp) = JsonColumn.ForApprovalConfig();
        b.Property(e => e.Config).HasConversion(cConv, cCmp).HasColumnType(_jsonType).IsRequired();

        if (_isSqlite)
        {
            b.Property(e => e.PendingSinceUtc).HasConversion(new Iso8601UtcDateTimeOffsetConverter());
            b.Property(e => e.DeadlineUtc).HasConversion(new Iso8601UtcNullableDateTimeOffsetConverter());
            b.Property(e => e.DecidedAtUtc).HasConversion(new Iso8601UtcNullableDateTimeOffsetConverter());
        }

        b.HasIndex(e => e.Status);   // ListPendingAsync hot path
    }
}
