using System.Globalization;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

internal static class StorageStructure
{
    /// <summary>
    /// True when the data store holds any table of its own. The applied-steps record is ignored: a data store whose
    /// first step has not committed has that record and nothing else, and counts as new.
    /// </summary>
    public static bool HasUserTables(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*)
            FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%' AND name <> 'data_store_upgrades';
            """;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }
}
