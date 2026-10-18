using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MediaEngine.Storage.Schema;
using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// One-off proof that the baseline step plus the reference-data top-up build exactly the data store the retired
/// startup routine (schema.sql + SchemaMigrator) built. Deleted together with the old path.
/// </summary>
public sealed class BaselineStepComparisonTests
{
    private static readonly Regex Timestamp = new(
        @"\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}:\d{2}(\.\d+)?Z?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    private readonly ITestOutputHelper _output;

    public BaselineStepComparisonTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void BaselineStepMatchesTheRetiredStartupRoutine()
    {
        var oldPath = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_old_{Guid.NewGuid():N}.db");
        var newPath = Path.Combine(Path.GetTempPath(), $"tuvima_baseline_new_{Guid.NewGuid():N}.db");
        try
        {
            using (var oldWay = new DatabaseConnection(oldPath))
            {
                oldWay.InitializeSchema();
                oldWay.RunStartupChecks();
            }

            var factory = new SqliteConnectionFactory(newPath);
            using (var conn = factory.OpenSharedConnection())
            {
                new DataStoreUpgradeRunner().Run(conn, DataStoreUpgradeCatalog.ForProduct(), new DataStoreUpgradeOptions());
                new ReferenceDataTopUp().Run(conn);
                using var epoch = conn.CreateCommand();
                epoch.CommandText = "INSERT OR REPLACE INTO storage_metadata (key, value) VALUES ('storage_epoch', $epoch)";
                epoch.Parameters.AddWithValue("$epoch", StorageEpochGuard.CurrentEpoch);
                epoch.ExecuteNonQuery();
            }

            SqliteConnection.ClearAllPools();

            using var oldConn = new SqliteConnection($"Data Source={oldPath};Pooling=False");
            using var newConn = new SqliteConnection($"Data Source={newPath};Pooling=False");
            oldConn.Open();
            newConn.Open();

            // Intended differences, and only these.
            var oldOnlyTables = new HashSet<string>(StringComparer.Ordinal) { "schema_migrations" };
            var newOnlyTables = new HashSet<string>(StringComparer.Ordinal) { "data_store_upgrades" };

            var oldMaster = ReadMaster(oldConn, oldOnlyTables);
            var newMaster = ReadMaster(newConn, newOnlyTables);
            Assert.Equal(oldMaster, newMaster);

            var tables = ReadTables(oldConn, oldOnlyTables);
            Assert.Equal(tables, ReadTables(newConn, newOnlyTables));

            var pragmaLines = 0;
            foreach (var table in tables)
            {
                foreach (var pragma in new[] { "table_info", "index_list", "foreign_key_list" })
                {
                    var oldLines = ReadPragma(oldConn, pragma, table);
                    Assert.Equal(oldLines, ReadPragma(newConn, pragma, table));
                    pragmaLines += oldLines.Count;
                }

                foreach (var index in ReadIndexNames(oldConn, table))
                {
                    var oldLines = ReadIndexXInfo(oldConn, index);
                    Assert.Equal(oldLines, ReadIndexXInfo(newConn, index));
                    pragmaLines += oldLines.Count;
                }
            }

            var seededRows = 0;
            var tablesWithRows = 0;
            foreach (var table in tables)
            {
                if (IsVirtual(oldConn, table))
                {
                    continue;
                }

                var oldRows = ReadRows(oldConn, table, oldSide: true);
                var newRows = ReadRows(newConn, table, oldSide: false);
                Assert.True(
                    oldRows.SequenceEqual(newRows, StringComparer.Ordinal),
                    $"Rows of '{table}' differ.{Environment.NewLine}old:{Environment.NewLine}{string.Join(Environment.NewLine, oldRows)}{Environment.NewLine}new:{Environment.NewLine}{string.Join(Environment.NewLine, newRows)}");
                seededRows += oldRows.Count;
                if (oldRows.Count > 0)
                {
                    tablesWithRows++;
                }
            }

            // The baseline carries no marker rows, and the epoch row is written by the connection, not the step.
            Assert.Equal(0L, Scalar(newConn, "SELECT COUNT(*) FROM storage_metadata WHERE key LIKE 'access_native_%seeded'"));
            Assert.Equal(1L, Scalar(newConn, "SELECT COUNT(*) FROM data_store_upgrades"));

            _output.WriteLine(string.Create(
                CultureInfo.InvariantCulture,
                $"COMPARED tables={Count(oldConn, "table")} (including FTS shadow tables; {tables.Count} named tables) indexes={Count(oldConn, "index")} triggers={Count(oldConn, "trigger")} views={Count(oldConn, "view")} sqlite_master_entries={oldMaster.Count} pragma_lines={pragmaLines} rows={seededRows} populated_tables={tablesWithRows}"));
        }
        finally
        {
            TestTemp.DeleteDatabase(oldPath);
            TestTemp.DeleteDatabase(newPath);
        }
    }

    private static long Count(SqliteConnection conn, string type)
        => Scalar(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type = '{type}' AND name NOT LIKE 'sqlite_%' AND name NOT IN ('schema_migrations','data_store_upgrades')");

    private static long Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static bool IsVirtual(SqliteConnection conn, string table)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = $name";
        cmd.Parameters.AddWithValue("$name", table);
        return (cmd.ExecuteScalar() as string)?.StartsWith("CREATE VIRTUAL TABLE", StringComparison.OrdinalIgnoreCase) == true;
    }

    private static List<string> ReadMaster(SqliteConnection conn, HashSet<string> excludedTables)
    {
        var lines = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT type, name, tbl_name, sql FROM sqlite_master WHERE name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(1);
            var tbl = reader.GetString(2);
            if (excludedTables.Contains(name) || excludedTables.Contains(tbl))
            {
                continue;
            }

            var sql = reader.IsDBNull(3) ? "(null)" : Whitespace.Replace(reader.GetString(3), " ").Trim();
            lines.Add($"{reader.GetString(0)}|{name}|{tbl}|{sql}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> ReadTables(SqliteConnection conn, HashSet<string> excludedTables)
    {
        var tables = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%'";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            if (!excludedTables.Contains(reader.GetString(0)))
            {
                tables.Add(reader.GetString(0));
            }
        }

        tables.Sort(StringComparer.Ordinal);
        return tables;
    }

    private static List<string> ReadPragma(SqliteConnection conn, string pragma, string table)
    {
        var lines = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM pragma_{pragma}($table)";
        cmd.Parameters.AddWithValue("$table", table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            lines.Add($"{table}|{pragma}|{Render(reader)}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> ReadIndexNames(SqliteConnection conn, string table)
    {
        var names = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM pragma_index_list($table)";
        cmd.Parameters.AddWithValue("$table", table);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        names.Sort(StringComparer.Ordinal);
        return names;
    }

    private static List<string> ReadIndexXInfo(SqliteConnection conn, string index)
    {
        var lines = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM pragma_index_xinfo($index)";
        cmd.Parameters.AddWithValue("$index", index);
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            lines.Add($"{index}|index_xinfo|{Render(reader)}");
        }

        lines.Sort(StringComparer.Ordinal);
        return lines;
    }

    private static List<string> ReadRows(SqliteConnection conn, string table, bool oldSide)
    {
        var rows = new List<string>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT * FROM \"{table}\"";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var rendered = Render(reader);
            if (table == "storage_metadata")
            {
                // Intended differences: the epoch value, and the two seed markers the baseline no longer needs.
                if (rendered.StartsWith("access_native_", StringComparison.Ordinal))
                {
                    Assert.True(oldSide, "The new data store must not carry the retired seed markers.");
                    continue;
                }

                if (rendered.StartsWith("storage_epoch", StringComparison.Ordinal))
                {
                    rendered = "storage_epoch|<epoch>";
                }
            }

            rows.Add(rendered);
        }

        rows.Sort(StringComparer.Ordinal);
        return rows;
    }

    private static string Render(SqliteDataReader reader)
    {
        var builder = new StringBuilder();
        for (var i = 0; i < reader.FieldCount; i++)
        {
            if (i > 0)
            {
                builder.Append('|');
            }

            if (reader.IsDBNull(i))
            {
                builder.Append("(null)");
            }
            else if (reader.GetFieldType(i) == typeof(byte[]))
            {
                builder.Append(Convert.ToHexString(reader.GetFieldValue<byte[]>(i)));
            }
            else
            {
                builder.Append(Timestamp.Replace(Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture) ?? string.Empty, "<ts>"));
            }
        }

        return builder.ToString();
    }
}
