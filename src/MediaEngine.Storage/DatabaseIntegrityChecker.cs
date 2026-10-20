using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal sealed class DatabaseIntegrityChecker
{
    public void EnsureWritable(SqliteConnection conn, string databasePath)
    {
        var transactionStarted = false;
        try
        {
            using (var begin = conn.CreateCommand())
            {
                begin.CommandText = "BEGIN IMMEDIATE;";
                begin.ExecuteNonQuery();
                transactionStarted = true;
            }

            using (var writeProbe = conn.CreateCommand())
            {
                var probeName = $"__tuvima_write_probe_{Guid.NewGuid():N}";
                writeProbe.CommandText = $"CREATE TABLE main.\"{probeName}\" (value INTEGER);";
                writeProbe.ExecuteNonQuery();
            }

            using var rollback = conn.CreateCommand();
            rollback.CommandText = "ROLLBACK;";
            rollback.ExecuteNonQuery();
            transactionStarted = false;
        }
        catch (SqliteException exception) when (IsReadOnly(exception))
        {
            throw ReadOnlyDatabase(databasePath, exception);
        }
        finally
        {
            if (transactionStarted)
            {
                try
                {
                    using var rollback = conn.CreateCommand();
                    rollback.CommandText = "ROLLBACK;";
                    rollback.ExecuteNonQuery();
                }
                catch (SqliteException)
                {
                    // Preserve the original startup failure.
                }
            }
        }
    }

    /// <exception cref="InvalidOperationException">
    /// Thrown when <c>PRAGMA integrity_check</c> returns anything other than "ok".
    /// </exception>
    public void EnsureIntact(SqliteConnection conn, string databasePath)
    {
        using var integrityCmd = conn.CreateCommand();
        integrityCmd.CommandText = "PRAGMA integrity_check;";
        var result = integrityCmd.ExecuteScalar()?.ToString();

        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"SQLite integrity_check failed for '{databasePath}': {result}");
        }
    }

    public void Optimize(SqliteConnection conn)
    {
        using var optimizeCmd = conn.CreateCommand();
        optimizeCmd.CommandText = "PRAGMA optimize;";
        optimizeCmd.ExecuteNonQuery();
    }

    internal static bool IsReadOnly(SqliteException exception) =>
        exception.SqliteErrorCode == 8;

    internal static DatabaseWriteAccessException ReadOnlyDatabase(string databasePath, SqliteException exception) =>
        new(
            $"Library cannot start because the SQLite database '{Path.GetFullPath(databasePath)}' is read-only. " +
            "Give the Engine process write access to the database file and its parent directory, then start Library again.",
            exception);
}
