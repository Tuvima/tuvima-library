using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal static class StorageEpochGuard
{
    // v6 replaces the pre-beta family-sharing tables and policy columns with the
    // Shared Library contribution workflow. Pre-beta databases are rebuilt rather
    // than carrying both schemas or migrating user state in place.
    // Pre-beta databases are rebuilt instead of being migrated in place.
    // v11 moves the data structure onto numbered upgrade steps (Schema/Upgrades). Data stores from before the
    // baseline step are backed up and reingested once; nothing recognises or adopts them.
    public const string CurrentEpoch = "guid-blob-v11-upgrade-steps";
    public const string ResetEnvironmentVariable = "TUVIMA_STORAGE_RESET";

    public static void EnsureCurrentOrReset(string databasePath)
    {
        if (!File.Exists(databasePath) || new FileInfo(databasePath).Length == 0)
        {
            return;
        }

        var status = ReadStorageEpoch(databasePath);
        if (string.Equals(status.Epoch, CurrentEpoch, StringComparison.Ordinal))
        {
            return;
        }

        if (status.Epoch is null && !status.HasUserTables)
        {
            return;
        }

        if (!ResetRequested())
        {
            throw new InvalidOperationException(
                $"Database '{databasePath}' is not a supported {CurrentEpoch} database. " +
                $"Legacy databases are not migrated in place. Set {ResetEnvironmentVariable}=1 " +
                "to back up this database and reingest into a fresh current schema.");
        }

        BackupAndRemove(databasePath);
    }

    private static (string? Epoch, bool HasUserTables) ReadStorageEpoch(string databasePath)
    {
        using var conn = new SqliteConnection($"Data Source={databasePath};Mode=ReadOnly;Pooling=False");
        conn.Open();

        bool hasUserTables;
        using (var userTablesCmd = conn.CreateCommand())
        {
            userTablesCmd.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> 'data_store_upgrades';
                """;
            hasUserTables = Convert.ToInt32(userTablesCmd.ExecuteScalar()) > 0;
        }

        using (var tableCmd = conn.CreateCommand())
        {
            tableCmd.CommandText = """
                SELECT COUNT(*)
                FROM sqlite_master
                WHERE type = 'table' AND name = 'storage_metadata';
                """;
            if (Convert.ToInt32(tableCmd.ExecuteScalar()) == 0)
            {
                return (null, hasUserTables);
            }
        }

        using var epochCmd = conn.CreateCommand();
        epochCmd.CommandText = """
            SELECT value
            FROM storage_metadata
            WHERE key = 'storage_epoch';
            """;
        return (Convert.ToString(epochCmd.ExecuteScalar()), hasUserTables);
    }

    private static bool ResetRequested()
    {
        var value = Environment.GetEnvironmentVariable(ResetEnvironmentVariable);
        return value is not null
            && (value.Equals("1", StringComparison.OrdinalIgnoreCase)
                || value.Equals("true", StringComparison.OrdinalIgnoreCase)
                || value.Equals("destructive-reingest", StringComparison.OrdinalIgnoreCase));
    }

    private static void BackupAndRemove(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        var stamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
        foreach (var path in new[] { databasePath, $"{databasePath}-wal", $"{databasePath}-shm" })
        {
            if (!File.Exists(path))
            {
                continue;
            }

            var backupPath = UniqueBackupPath(path, stamp);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
            File.Move(path, backupPath);
        }
    }

    private static string UniqueBackupPath(string path, string stamp)
    {
        var candidate = $"{path}.legacy-text-guid.{stamp}.bak";
        if (!File.Exists(candidate))
        {
            return candidate;
        }

        for (var i = 1; ; i++)
        {
            var numbered = $"{candidate}.{i}";
            if (!File.Exists(numbered))
            {
                return numbered;
            }
        }
    }
}
