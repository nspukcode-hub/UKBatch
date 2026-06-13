namespace UKBatch.Dashboard.Models;

/// <summary>
/// Shortens long identifiers for display. Shared by every page and row component that
/// abbreviates an id so the rule has ONE source of truth.
/// </summary>
/// <remarks>
/// Execution, batch-run, and batch-definition ids are UUIDv7 hex strings whose first
/// 12 characters encode a millisecond timestamp — runs created within the same ~65-second
/// window share their first 8 characters, so a leading prefix cannot tell neighbours apart.
/// The trailing characters are random, so the abbreviation keeps the TAIL
/// (<c>…f9ccba12</c>) instead of the head.
/// </remarks>
public static class IdDisplay
{
    private const int TailLength = 8;

    /// <summary>
    /// Returns the last <c>8</c> characters of <paramref name="id"/> prefixed with an
    /// ellipsis, or the id verbatim when it is already that short.
    /// </summary>
    public static string Shorten(string id)
    {
        ArgumentNullException.ThrowIfNull(id);
        return id.Length <= TailLength ? id : string.Concat("…", id.AsSpan(id.Length - TailLength));
    }
}
