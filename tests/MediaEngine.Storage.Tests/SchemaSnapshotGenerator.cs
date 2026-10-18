using System.Text;
using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Builds the text of <c>schema.snapshot.sql</c>: every product upgrade step applied to an empty temp data store,
/// then each <c>sqlite_master</c> entry's SQL written out in a fixed order. Structure only, no data rows.
/// </summary>
internal static class SchemaSnapshotGenerator
{
    public static string Generate()
    {
        var steps = DataStoreUpgradeCatalog.ForProduct();
        var latest = steps.Count == 0
            ? "none"
            : $"{DataStoreUpgradeCatalog.Format(steps[^1].Number)} {steps[^1].Name}";

        var path = Path.Combine(Path.GetTempPath(), $"tuvima_snapshot_{Guid.NewGuid():N}.db");
        try
        {
            using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                connection.Open();
                Execute(connection, "PRAGMA foreign_keys = ON;");
                new DataStoreUpgradeRunner().Run(connection, steps, new DataStoreUpgradeOptions { EngineVersion = "schema-snapshot" });
                return Compose(latest, ReadEntries(connection));
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TestTemp.DeleteDatabase(path);
        }
    }

    private static string Compose(string latestStep, IReadOnlyList<Entry> entries)
    {
        var text = new StringBuilder();
        text.Append("-- GENERATED FILE: do not edit.\n");
        text.Append($"-- The complete Tuvima data structure after every upgrade step in Schema/Upgrades/ (latest step: {latestStep}).\n");
        text.Append("-- Change the structure by adding a numbered upgrade step, then refresh this file:\n");
        text.Append("--   pwsh -File tools/Update-SchemaSnapshot.ps1\n");
        text.Append('\n');

        foreach (var group in new[] { Group.Table, Group.VirtualTable, Group.Index, Group.Trigger, Group.View })
        {
            foreach (var entry in entries.Where(entry => entry.Group == group).OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                text.Append(entry.Sql.ReplaceLineEndings("\n").TrimEnd());
                text.Append(";\n\n");
            }
        }

        return text.ToString();
    }

    private static List<Entry> ReadEntries(SqliteConnection connection)
    {
        // SQLite itself says which tables are the hidden storage of a virtual table (such as FTS5's _data, _idx,
        // _content, _docsize and _config), so a user table that merely ends in one of those words is kept.
        var shadowTables = new HashSet<string>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT name FROM pragma_table_list WHERE schema = 'main' AND type = 'shadow';";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                shadowTables.Add(reader.GetString(0));
            }
        }

        var entries = new List<Entry>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var type = reader.GetString(0);
                var name = reader.GetString(1);
                var sql = reader.GetString(2);
                if (name.StartsWith("sqlite_", StringComparison.Ordinal) || shadowTables.Contains(name))
                {
                    continue;
                }

                var group = type switch
                {
                    "table" when sql.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase) => Group.VirtualTable,
                    "table" => Group.Table,
                    "index" => Group.Index,
                    "trigger" => Group.Trigger,
                    "view" => Group.View,
                    _ => throw new InvalidOperationException($"Unexpected sqlite_master entry type '{type}' for '{name}'."),
                };
                entries.Add(new Entry(group, name, sql));
            }
        }

        return entries;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private enum Group
    {
        Table,
        VirtualTable,
        Index,
        Trigger,
        View,
    }

    private sealed record Entry(Group Group, string Name, string Sql);
}
