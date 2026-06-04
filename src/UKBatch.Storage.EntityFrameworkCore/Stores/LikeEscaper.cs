namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// Escapes the SQL <c>LIKE</c> wildcards (<c>%</c>, <c>_</c>) and the escape char itself in user
/// <c>SearchText</c> so a literal <c>%</c> in a job name matches literally — preserving the InMemory
/// <c>OrdinalIgnoreCase</c> substring semantics. The escape char is <c>\</c>, declared via
/// <c>EF.Functions.Like(col, pattern, "\\")</c> at the call site.
/// </summary>
internal static class LikeEscaper
{
    /// <summary>The LIKE escape character paired with the <c>ESCAPE '\'</c> clause at the call site.</summary>
    public const char EscapeChar = '\\';

    /// <summary>Escapes <c>\</c>, <c>%</c>, and <c>_</c> in <paramref name="value"/> for a substring LIKE pattern.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        // Order matters: escape the escape char FIRST so we don't double-escape the ones we add.
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }
}
