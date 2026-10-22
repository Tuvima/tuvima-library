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
                    .Concat(rule.ChoiceColumns)
                    .Concat(rule.ChoiceGatedBy.SelectMany(gate => new[] { gate.Column, gate.Flag }))
                    .Concat(rule.PortableColumns)
                    .Concat(rule.ChoiceRowReference is null ? [] : [rule.ChoiceRowReference.Column])
                    .Concat(rule.ExcludedJsonProperties.Select(property => property.Column))
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
    public void TransientTables_CarryNoRules()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.Kind == BackupDataKind.Transient)
            .Where(rule => rule.CatalogueReferences.Count > 0
                || rule.ExcludedColumns.Count > 0
                || rule.ExcludedJsonProperties.Count > 0
                || rule.ChoiceColumns.Count > 0
                || rule.ChoiceGatedBy.Count > 0
                || rule.PortableColumns.Count > 0
                || rule.OwnerColumn is not null
                || rule.CorrectionWhere is not null
                || rule.KeepWhere is not null)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(offenders.Count == 0, "Transient tables are never kept, so they carry no rules: " + string.Join(", ", offenders));
    }

    [Fact]
    public void CatalogueTables_CarryColumnRulesOnlyForTheCorrectionsAndChoicesTheyKeep()
    {
        var problems = new List<string>();
        foreach (var rule in BackupTableClassification.Rules.Values.Where(rule => rule.Kind == BackupDataKind.Catalogue))
        {
            // A catalogue table is rebuilt by a rescan; it names columns only for what a person's own rows need.
            if (rule.OwnerColumn is not null || rule.KeepWhere is not null || rule.ExcludedJsonProperties.Count > 0 || rule.PortableColumns.Count > 0)
            {
                problems.Add($"'{rule.Table}' carries an owner, keep condition, JSON exclusion or portable column, which a Catalogue table never has.");
            }

            if (rule.ExcludedColumns.Count > 0 && rule.CorrectionWhere is null)
            {
                problems.Add($"'{rule.Table}' excludes columns but keeps no corrections.");
            }

            var strayReferences = rule.CatalogueReferences
                .Where(reference => rule.CorrectionWhere is null && !rule.ChoiceColumns.Contains(reference.Column, StringComparer.OrdinalIgnoreCase))
                .Select(reference => reference.Column);
            foreach (var column in strayReferences)
            {
                problems.Add($"'{rule.Table}' names reference '{column}' but keeps no corrections and it is not a choice column.");
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void ChoiceColumns_AreOnlyAllowedOnCatalogueTables_OrConfigurationTablesWithAKeepCondition()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.ChoiceColumns.Count > 0)
            .Where(rule => !(rule.Kind == BackupDataKind.Catalogue || (rule.Kind == BackupDataKind.Configuration && rule.KeepWhere is not null)))
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(
            offenders.Count == 0,
            "Choice columns (a person's own choices on a catalogue row) belong to Catalogue tables, or Configuration tables that keep only some rows: " + string.Join(", ", offenders));
        Assert.Contains(BackupTableClassification.Rules.Values, rule => rule.Table == "local_items" && rule.ChoiceColumns.Contains("favorite"));
        Assert.Contains(BackupTableClassification.Rules.Values, rule => rule.Table == "collections" && rule.ChoiceColumns.Contains("is_enabled"));
    }

    [Fact]
    public void ChoiceRowReference_IsOnlyAllowedOnRulesWithChoiceColumns()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.ChoiceRowReference is not null && rule.ChoiceColumns.Count == 0)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(offenders.Count == 0, "A choice row reference needs choice columns: " + string.Join(", ", offenders));
        Assert.Contains(
            BackupTableClassification.Rules.Values,
            rule => rule.Table == "local_item_metadata" && rule.ChoiceRowReference?.Column == "item_id");
    }

    [Fact]
    public void CorrectionWhere_IsOnlyAllowedOnCatalogueTables()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.Kind != BackupDataKind.Catalogue && rule.CorrectionWhere is not null)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(offenders.Count == 0, "A correction condition belongs to Catalogue tables only: " + string.Join(", ", offenders));
        Assert.Contains(BackupTableClassification.Rules.Values, rule => rule.Table == "metadata_claims" && rule.CorrectionWhere is not null);
    }

    [Fact]
    public void PortableColumns_AreOnlyAllowedOnConfigurationTables()
    {
        var offenders = BackupTableClassification.Rules.Values
            .Where(rule => rule.Kind != BackupDataKind.Configuration && rule.PortableColumns.Count > 0)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(offenders.Count == 0, "Portable columns belong to Configuration tables only: " + string.Join(", ", offenders));
        Assert.Contains(
            BackupTableClassification.Rules.Values,
            rule => rule.Table == "account_two_step" && rule.PortableColumns.Contains("secret_protected"));
        Assert.All(
            BackupTableClassification.Rules.Values,
            rule => Assert.DoesNotContain(rule.PortableColumns, column => rule.ExcludedColumns.Contains(column, StringComparer.OrdinalIgnoreCase)));
    }

    [Fact]
    public void ChoiceGatedBy_NamesChoiceColumnsOnly()
    {
        var problems = new List<string>();
        foreach (var rule in BackupTableClassification.Rules.Values)
        {
            foreach (var (column, flag) in rule.ChoiceGatedBy)
            {
                if (!rule.ChoiceColumns.Contains(column, StringComparer.OrdinalIgnoreCase) || !rule.ChoiceColumns.Contains(flag, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add($"'{rule.Table}' gates '{column}' by '{flag}', but both must be listed in its choice columns.");
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Contains(
            BackupTableClassification.Rules.Values,
            rule => rule.Table == "local_items" && rule.ChoiceGatedBy.Contains(("captured_at", "captured_at_user_override")));
    }

    [Fact]
    public void KeepWhere_AndCorrectionWhere_AreValidSql()
    {
        WithFreshStore(connection =>
        {
            var tables = ReadTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var problems = new List<string>();

            foreach (var rule in BackupTableClassification.Rules.Values.Where(rule => tables.Contains(rule.Table)))
            {
                foreach (var (name, predicate) in new[] { ("KeepWhere", rule.KeepWhere), ("CorrectionWhere", rule.CorrectionWhere) })
                {
                    if (predicate is null)
                    {
                        continue;
                    }

                    try
                    {
                        using var command = connection.CreateCommand();
                        command.CommandText = $"SELECT COUNT(*) FROM {rule.Table} WHERE {predicate}";
                        command.ExecuteScalar();
                    }
                    catch (SqliteException ex)
                    {
                        problems.Add($"{name} for '{rule.Table}' is not valid SQL: {ex.Message}");
                    }
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

            // Guard against a store that was never built: an empty one would make every table check pass.
            var tables = ReadTables(connection);
            Assert.True(tables.Count > 100, $"The fresh data store has only {tables.Count} tables.");
            Assert.Contains("works", tables);
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
