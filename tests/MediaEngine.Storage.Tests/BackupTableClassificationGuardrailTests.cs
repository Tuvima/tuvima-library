using MediaEngine.Storage.Backup;
using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Guardrails for the backup table classification: every data store table has a rule saying whether a backup keeps
/// it, the column rules point at real columns, and the public architecture page lists every classified table.
/// </summary>
public sealed class BackupTableClassificationGuardrailTests
{
    private const string ArchitectureDocPath = "docs/architecture/backup-and-restore.md";

    /// <summary>
    /// PersonalState tables that genuinely have no owner column of their own. Adding a table here needs a reason.
    /// </summary>
    private static readonly HashSet<string> TablesWithoutOwnerColumn = new(StringComparer.OrdinalIgnoreCase)
    {
        // Rows belong to a bookmark pack and take their owner from bookmark_packs.profile_id.
        "bookmark_pack_items",
    };

    [Fact]
    public void EveryTable_IsClassified()
    {
        WithFreshStore(connection =>
        {
            var missing = ReadTables(connection)
                .Where(table => BackupTableClassification.Find(table) is null)
                .OrderBy(table => table, StringComparer.Ordinal)
                .ToList();

            Assert.True(
                missing.Count == 0,
                "Tables without a backup rule: " + string.Join(", ", missing) + ". " +
                string.Join(" ", missing.Select(table => $"Add '{table}' to BackupTableClassification (Configuration, PersonalState, Catalogue or Transient).")));
        });
    }

    [Fact]
    public void ClassifiedColumns_ExistInTheirTables()
    {
        WithFreshStore(connection =>
        {
            var tables = ReadTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();

            foreach (var rule in BackupTableClassification.Rules.Values)
            {
                if (!tables.Contains(rule.Table))
                {
                    if (!BackupTableClassification.RegisteredAhead.Contains(rule.Table))
                    {
                        problems.Add($"'{rule.Table}' has a rule but no table, and is not in RegisteredAhead.");
                    }

                    continue;
                }

                var columns = ReadColumns(connection, rule.Table);
                var named = rule.CatalogueReferences.Select(reference => reference.Column)
                    .Concat(rule.ExcludedColumns)
                    .Concat(rule.OwnerColumn is null ? [] : [rule.OwnerColumn]);
                foreach (var column in named.Where(column => !columns.Contains(column)))
                {
                    problems.Add($"'{rule.Table}' has a column rule for '{column}', which is not a column of that table.");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void RegisteredAheadTables_AreOnlyUsedWhileTheTableIsMissing()
    {
        WithFreshStore(connection =>
        {
            var tables = ReadTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var landed = BackupTableClassification.RegisteredAhead.Where(tables.Contains).ToList();

            Assert.True(
                landed.Count == 0,
                "These tables now exist, so remove them from BackupTableClassification.RegisteredAhead: " + string.Join(", ", landed));
            Assert.All(BackupTableClassification.RegisteredAhead, table => Assert.NotNull(BackupTableClassification.Find(table)));
        });
    }

    [Fact]
    public void PersonalStateTables_NameTheirOwnerColumn()
    {
        WithFreshStore(connection =>
        {
            var tables = ReadTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();

            foreach (var rule in BackupTableClassification.OfKind(BackupDataKind.PersonalState))
            {
                if (!tables.Contains(rule.Table) || TablesWithoutOwnerColumn.Contains(rule.Table))
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(rule.OwnerColumn))
                {
                    problems.Add($"'{rule.Table}' is PersonalState but names no OwnerColumn.");
                }
                else if (!ReadColumns(connection, rule.Table).Contains(rule.OwnerColumn))
                {
                    problems.Add($"'{rule.Table}' names OwnerColumn '{rule.OwnerColumn}', which is not a column of that table.");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void OwnerColumn_IsOnlySetOnPersonalStateTables()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.Kind != BackupDataKind.PersonalState && rule.OwnerColumn is not null)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(offenders.Count == 0, "OwnerColumn belongs to PersonalState tables only: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CatalogueAndTransientTables_CarryNoColumnRules()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.Kind is BackupDataKind.Catalogue or BackupDataKind.Transient)
            .Where(rule => rule.CatalogueReferences.Count > 0
                || rule.ExcludedColumns.Count > 0
                || rule.OwnerColumn is not null
                || rule.KeepWhere is not null)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Catalogue and Transient tables are never kept, so they carry no column or row rules: " + string.Join(", ", offenders));
    }

    [Fact]
    public void KeepWhere_IsValidSql()
    {
        WithFreshStore(connection =>
        {
            var tables = ReadTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();

            foreach (var rule in BackupTableClassification.Rules.Values.Where(rule => rule.KeepWhere is not null && tables.Contains(rule.Table)))
            {
                try
                {
                    using var command = connection.CreateCommand();
                    command.CommandText = $"SELECT COUNT(*) FROM {rule.Table} WHERE {rule.KeepWhere}";
                    command.ExecuteScalar();
                }
                catch (SqliteException ex)
                {
                    problems.Add($"KeepWhere for '{rule.Table}' is not valid SQL: {ex.Message}");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void ArchitectureDoc_ListsEveryClassifiedTable()
    {
        var docPath = Path.Combine(FindRepoRoot(), ArchitectureDocPath);
        Assert.True(File.Exists(docPath), $"{ArchitectureDocPath} is missing.");

        var doc = File.ReadAllText(docPath);
        var missing = BackupTableClassification.Rules.Keys
            .Where(table => !doc.Contains($"`{table}`", StringComparison.Ordinal))
            .OrderBy(table => table, StringComparer.Ordinal)
            .ToList();

        Assert.True(
            missing.Count == 0,
            $"{ArchitectureDocPath} does not list these classified tables in backticks: " + string.Join(", ", missing));
    }

    private static void WithFreshStore(Action<SqliteConnection> assertions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_backup_classification_{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            using (var pragma = connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }

            new DataStoreUpgradeRunner().Run(
                connection,
                DataStoreUpgradeCatalog.ForProduct(),
                new DataStoreUpgradeOptions { EngineVersion = "backup-table-classification" });
            assertions(connection);
        }
        finally
        {
            TestTemp.DeleteDatabase(path);
        }
    }

    /// <summary>
    /// Every real table: not SQLite's own, and not the hidden storage tables of a search (FTS5) table, which SQLite
    /// itself reports as shadow tables.
    /// </summary>
    private static List<string> ReadTables(SqliteConnection connection)
    {
        var tables = new List<string>();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name FROM pragma_table_list
            WHERE schema = 'main' AND type IN ('table', 'virtual') AND name NOT LIKE 'sqlite\_%' ESCAPE '\'
            ORDER BY name;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static HashSet<string> ReadColumns(SqliteConnection connection, string table)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM pragma_table_info($table);";
        command.Parameters.AddWithValue("$table", table);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not find repository root.");
    }
}
