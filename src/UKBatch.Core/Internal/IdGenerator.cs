#if !NET10_0_OR_GREATER
using System.Security.Cryptography;
#endif

namespace UKBatch.Internal;

/// <summary>
/// Generates k-sortable, time-ordered UUIDv7 identifiers (<c>Guid.CreateVersion7()</c> on
/// net10.0; an RFC 9562 polyfill with the identical byte layout on net8.0). Identifiers are
/// formatted as 32-character hex strings without separators (<c>"N"</c> format) for compact
/// storage and URL safety.
/// </summary>
internal static class IdGenerator
{
    /// <summary>Generates a new execution id.</summary>
    public static string NewExecutionId() => NewV7().ToString("N");

    /// <summary>Generates a new batch id.</summary>
    public static string NewBatchId() => NewV7().ToString("N");

    /// <summary>Generates a new batch-step id.</summary>
    public static string NewStepId() => NewV7().ToString("N");

    /// <summary>Generates a new approval-gate id.</summary>
    public static string NewApprovalId() => NewV7().ToString("N");

    /// <summary>
    /// Generates a new wire-format <c>JobMessage.MessageId</c>. UUIDv7 chosen for
    /// time-ordered + k-sortable + clock-skew-insensitive properties (same as
    /// <see cref="NewExecutionId"/>). Receivers de-duplicate on this id.
    /// </summary>
    public static string NewMessageId() => NewV7().ToString("N");

    private static Guid NewV7()
    {
#if NET10_0_OR_GREATER
        return Guid.CreateVersion7();
#else
        // UUIDv7 (RFC 9562) for net8.0, where Guid.CreateVersion7 is unavailable: 48-bit
        // big-endian Unix-ms timestamp + version 7 + variant 10 + 74 random bits. The
        // big-endian Guid ctor reproduces CreateVersion7's byte layout exactly, so ids
        // generated on net8.0 and net10.0 sort and round-trip identically.
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        var unixMs = (ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        bytes[0] = (byte)(unixMs >> 40);
        bytes[1] = (byte)(unixMs >> 32);
        bytes[2] = (byte)(unixMs >> 24);
        bytes[3] = (byte)(unixMs >> 16);
        bytes[4] = (byte)(unixMs >> 8);
        bytes[5] = (byte)unixMs;
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x70); // version 7
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80); // variant 10xx
        return new Guid(bytes, bigEndian: true);
#endif
    }
}
