using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace UKBatch.Storage.EntityFrameworkCore.Stores;

/// <summary>
/// Provider-aware classification of <see cref="DbUpdateException"/> root causes so both EF stores share
/// ONE place that knows Npgsql's SQLSTATE vs SQLite's error codes.
/// </summary>
/// <remarks>
/// PostgreSQL: <see cref="PostgresException"/> with <c>SqlState == "23505"</c> (unique_violation).
/// SQLite: <see cref="SqliteException"/> with <c>SqliteErrorCode == 19</c> (constraint) and extended
/// code <c>2067</c> (SQLITE_CONSTRAINT_UNIQUE). The name-collision vs PK-collision distinction is
/// made by the CONSTRAINT NAME (<c>IX_BatchDefinitions_Source_Name</c>), available in the message text.
/// </remarks>
internal static class DbExceptionClassifier
{
    /// <summary>The named unique index on <c>BatchDefinitions(Source, Name)</c> (see BatchDefinitionConfiguration).</summary>
    public const string BatchDefinitionsSourceNameIndex = "IX_BatchDefinitions_Source_Name";

    /// <summary><c>true</c> if the exception (or its inner) is a unique-constraint violation on either provider.</summary>
    public static bool IsUniqueViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        return ex.InnerException switch
        {
            PostgresException pg => pg.SqlState == PostgresErrorCodes.UniqueViolation,
            SqliteException sqlite => sqlite.SqliteErrorCode == 19, // SQLITE_CONSTRAINT (covers extended 2067)
            _ => false,
        };
    }

    /// <summary>
    /// <c>true</c> if the unique violation is on the <c>(Source, Name)</c> index specifically (a duplicate
    /// NAME), as opposed to the primary key (a duplicate id).
    /// </summary>
    public static bool IsSourceNameViolation(DbUpdateException ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        if (!IsUniqueViolation(ex))
        {
            return false;
        }

        // The constraint/index name appears in both providers' error text. PostgresException exposes it
        // directly via ConstraintName; SQLite carries it in the message.
        return ex.InnerException switch
        {
            PostgresException pg =>
                string.Equals(pg.ConstraintName, BatchDefinitionsSourceNameIndex, StringComparison.Ordinal),
            SqliteException sqlite =>
                sqlite.Message.Contains(BatchDefinitionsSourceNameIndex, StringComparison.Ordinal)
                // SQLite's column-list form: "BatchDefinitions.Source, BatchDefinitions.Name".
                || (sqlite.Message.Contains("BatchDefinitions.Source", StringComparison.Ordinal)
                    && sqlite.Message.Contains("BatchDefinitions.Name", StringComparison.Ordinal)),
            _ => false,
        };
    }
}
