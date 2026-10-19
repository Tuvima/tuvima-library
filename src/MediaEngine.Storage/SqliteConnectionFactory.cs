using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal sealed class SqliteConnectionFactory
{
    private readonly string _databasePath;

    public SqliteConnectionFactory(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
    }

    public SqliteConnection OpenSharedConnection()
    {
        var conn = new SqliteConnection($"Data Source={_databasePath}");
        try
        {
            conn.Open();
            ApplySharedPragmas(conn);
            return conn;
        }
        catch (SqliteException exception) when (DatabaseIntegrityChecker.IsReadOnly(exception))
        {
            conn.Dispose();
            throw DatabaseIntegrityChecker.ReadOnlyDatabase(_databasePath, exception);
        }
    }

    public SqliteConnection CreateOperationConnection()
    {
        var conn = new SqliteConnection($"Data Source={_databasePath}");
        try
        {
            conn.Open();
            ApplyOperationPragmas(conn);
            return conn;
        }
        catch (SqliteException exception) when (DatabaseIntegrityChecker.IsReadOnly(exception))
        {
            conn.Dispose();
            throw DatabaseIntegrityChecker.ReadOnlyDatabase(_databasePath, exception);
        }
    }

    /// <summary>
    /// Closes this database's idle pooled connections so the file (and its WAL/SHM
    /// sidecars) is released. Connections still checked out close when returned.
    /// </summary>
    public void ClearPool() => ClearPool(_databasePath);

    /// <summary>
    /// Closes the idle pooled connections to one database file. Never clear every pool in the
    /// process instead: Microsoft.Data.Sqlite treats a connection another thread is part-way
    /// through opening as leaked and disposes its handle, so a process-wide clear breaks
    /// unrelated work that happens to be opening a connection at that moment.
    /// </summary>
    public static void ClearPool(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        // Pool lookup is keyed by connection string; this instance is never opened.
        using var key = new SqliteConnection($"Data Source={databasePath}");
        SqliteConnection.ClearPool(key);
    }

    private static void ApplySharedPragmas(SqliteConnection conn)
    {
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode = WAL; " +
            "PRAGMA synchronous = NORMAL; " +
            "PRAGMA foreign_keys = ON; " +
            "PRAGMA temp_store = MEMORY; " +
            "PRAGMA busy_timeout = 5000; " +
            "PRAGMA cache_size = -16384; " +
            "PRAGMA mmap_size = 268435456;";
        pragmaCmd.ExecuteNonQuery();
    }

    private static void ApplyOperationPragmas(SqliteConnection conn)
    {
        using var pragmaCmd = conn.CreateCommand();
        pragmaCmd.CommandText =
            "PRAGMA journal_mode = WAL; " +
            "PRAGMA synchronous = NORMAL; " +
            "PRAGMA foreign_keys = ON; " +
            "PRAGMA temp_store = MEMORY; " +
            "PRAGMA busy_timeout = 5000; " +
            "PRAGMA cache_size = -16384; " +
            "PRAGMA mmap_size = 268435456;";
        pragmaCmd.ExecuteNonQuery();
    }
}
