using System.Globalization;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace UKBatch.Storage.EntityFrameworkCore.Json;

/// <summary>
/// SQLite-only <see cref="DateTimeOffset"/> ↔ ISO-8601 TEXT converter.
/// Stores the UTC instant as a fixed-width <c>yyyy-MM-ddTHH:mm:ss.fffffffZ</c> string. Fixed width +
/// <c>Z</c> suffix ⇒ SQLite <c>TEXT</c> <c>BINARY</c> collation orders it chronologically, backing the
/// <c>(…, EnqueuedAtUtc)</c> / <c>(…, CreatedAtUtc)</c> / <c>(PendingSinceUtc)</c> indexes correctly.
/// The codebase's all-UTC invariant (<c>_clock.GetUtcNow()</c> everywhere) makes the Z-normalization
/// lossless. NOT UTC-ticks (which risks silent offset drop and is unreadable in <c>sqlite3</c>).
/// PostgreSQL skips this — it uses native <c>timestamptz</c>.
/// </summary>
internal sealed class Iso8601UtcDateTimeOffsetConverter : ValueConverter<DateTimeOffset, string>
{
    internal const string Fmt = "yyyy-MM-ddTHH:mm:ss.fffffffZ";

    public Iso8601UtcDateTimeOffsetConverter()
        : base(
            v => v.ToUniversalTime().ToString(Fmt, CultureInfo.InvariantCulture),
            v => DateTimeOffset.ParseExact(v, Fmt, CultureInfo.InvariantCulture,
                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
    {
    }
}

/// <summary>
/// SQLite-only nullable variant of <see cref="Iso8601UtcDateTimeOffsetConverter"/> (null ↔ NULL).
/// Applied to <c>StartedAtUtc</c>, <c>CompletedAtUtc</c>, <c>DeadlineUtc</c>, <c>DecidedAtUtc</c>.
/// </summary>
internal sealed class Iso8601UtcNullableDateTimeOffsetConverter : ValueConverter<DateTimeOffset?, string?>
{
    public Iso8601UtcNullableDateTimeOffsetConverter()
        : base(
            v => v == null
                ? null
                : v.Value.ToUniversalTime().ToString(Iso8601UtcDateTimeOffsetConverter.Fmt, CultureInfo.InvariantCulture),
            v => v == null
                ? null
                : DateTimeOffset.ParseExact(v, Iso8601UtcDateTimeOffsetConverter.Fmt, CultureInfo.InvariantCulture,
                      DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal))
    {
    }
}
