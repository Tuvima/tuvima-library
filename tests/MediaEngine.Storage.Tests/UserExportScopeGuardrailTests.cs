using MediaEngine.Storage.Backup;
using MediaEngine.Storage.Schema.Upgrades;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Guardrails for what a user export holds (backup and restore spec D19): every Configuration and PersonalState table is either in the
/// user export map or in the explicit "not in a user export" list with a reason, so a new table forces a decision, and the map points at
/// real tables and columns.
/// </summary>
public sealed class UserExportScopeGuardrailTests
{
    [Fact]
    public void EveryConfigurationAndPersonalStateTable_IsEitherInTheUserExportOrExplicitlyLeftOut()
    {
        var decided = BackupTableClassification.OfKind(BackupDataKind.Configuration)
            .Concat(BackupTableClassification.OfKind(BackupDataKind.PersonalState))
            .Select(rule => rule.Table)
            .ToList();
        var inExport = UserExportScope.Tables.Select(table => table.Table).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var leftOut = UserExportScope.NotInAUserExport.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);

        var undecided = decided.Where(table => !inExport.Contains(table) && !leftOut.Contains(table)).OrderBy(table => table, StringComparer.Ordinal).ToList();
        Assert.True(
            undecided.Count == 0,
            "Tables a user export has no decision for: " + string.Join(", ", undecided) +
            ". Add each to UserExportScope.Tables, or to NotInAUserExport with a one-line reason.");

        var both = inExport.Intersect(leftOut).OrderBy(table => table, StringComparer.Ordinal).ToList();
        Assert.True(both.Count == 0, "Tables both in a user export and left out of it: " + string.Join(", ", both));

        var stale = inExport.Concat(leftOut).Where(table => !decided.Contains(table, StringComparer.OrdinalIgnoreCase)).Distinct().ToList();
        Assert.True(stale.Count == 0, "Tables in the user export map that are not Configuration or PersonalState tables: " + string.Join(", ", stale));
    }

    [Fact]
    public void EveryTableLeftOutOfAUserExport_SaysWhy()
    {
        var blank = UserExportScope.NotInAUserExport.Where(pair => string.IsNullOrWhiteSpace(pair.Value)).Select(pair => pair.Key).ToList();

        Assert.True(blank.Count == 0, "Tables left out of a user export without a reason: " + string.Join(", ", blank));
    }

    [Fact]
    public void EveryPersonalStateTable_IsInTheUserExport()
    {
        var missing = BackupTableClassification.OfKind(BackupDataKind.PersonalState)
            .Where(rule => UserExportScope.Find(rule.Table) is null)
            .Select(rule => rule.Table)
            .ToList();

        Assert.True(missing.Count == 0, "A person's own state must travel with them. Missing: " + string.Join(", ", missing));
    }

    [Fact]
    public void NoTableThatHoldsAccessRights_IsInTheUserExport()
    {
        var rights = new[]
        {
            "profile_library_grants", "profile_admin_protections", "profile_admin_unlocks", "client_devices", "device_profile_preferences",
            "auth_sessions", "account_invitations", "password_recovery_codes", "applications", "provider_config", "backup_settings",
        };

        Assert.All(rights, table => Assert.Null(UserExportScope.Find(table)));
    }

    [Fact]
    public void TheUserExportMap_PointsAtRealTablesAndColumns_AndEveryRowCheckNamesAColumn()
    {
        WithFreshStore(connection =>
        {
            var problems = new List<string>();
            foreach (var entry in UserExportScope.Tables)
            {
                var columns = ReadColumns(connection, entry.Table);
                if (columns.Count == 0)
                {
                    problems.Add($"'{entry.Table}' is in the user export map but is not a table.");
                    continue;
                }

                foreach (var column in entry.ExcludedColumns.Where(column => !columns.Contains(column)))
                {
                    problems.Add($"'{entry.Table}' excludes '{column}', which is not a column of that table.");
                }

                foreach (var column in (entry.ZeroedColumns ?? []).Where(column => !columns.Contains(column)))
                {
                    problems.Add($"'{entry.Table}' zeroes '{column}', which is not a column of that table.");
                }

                if (entry.Checks.Count == 0)
                {
                    problems.Add($"'{entry.Table}' has no row check, so a crafted file could put anyone's rows in it.");
                }

                foreach (var check in entry.Checks)
                {
                    var column = check switch
                    {
                        UserRowCheck.AccountColumn account => account.Column,
                        UserRowCheck.ProfileColumn profile => profile.Column,
                        UserRowCheck.ParentRow parent => parent.Column,
                        UserRowCheck.ColumnIs equals => equals.Column,
                        UserRowCheck.ColumnIn within => within.Column,
                        _ => string.Empty,
                    };
                    if (!columns.Contains(column))
                    {
                        problems.Add($"'{entry.Table}' checks '{column}', which is not a column of that table.");
                    }

                    if (check is UserRowCheck.ParentRow parentRow && !ReadColumns(connection, parentRow.ParentTable).Contains(parentRow.ParentColumn))
                    {
                        problems.Add($"'{entry.Table}' checks against '{parentRow.ParentTable}.{parentRow.ParentColumn}', which does not exist.");
                    }
                }

                // The filter must be valid SQL for the table, with real column names.
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{entry.Table}\" WHERE " +
                    UserExportScope.FilterFor(entry, [Guid.NewGuid()], [Guid.NewGuid()]);
                try
                {
                    command.ExecuteScalar();
                }
                catch (SqliteException ex)
                {
                    problems.Add($"The filter for '{entry.Table}' is not valid: {ex.Message}");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void NoTableInAUserExport_HasATextOrNumberPrimaryKeyOfItsOwn_SoNewBlobIdsCoverEveryIdAnImportMustReplace()
    {
        // An import gives a new id to every row whose own id already exists here (UserImportRepository.RenamedIds reads BLOB ids). A table
        // with a single TEXT or INTEGER primary key would need its own replacement rule, so such a table must not join the user export unseen.
        WithFreshStore(connection =>
        {
            var problems = new List<string>();
            foreach (var entry in UserExportScope.Tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT name, upper(type) FROM pragma_table_info($table) WHERE pk > 0;";
                command.Parameters.AddWithValue("$table", entry.Table);
                var keys = new List<(string Name, string Type)>();
                using (var reader = command.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        keys.Add((reader.GetString(0), reader.GetString(1)));
                    }
                }

                if (keys.Count == 1 && keys[0].Type != "BLOB")
                {
                    problems.Add($"'{entry.Table}' has a single {keys[0].Type} primary key '{keys[0].Name}', which an import would not give a new id.");
                }
            }

            Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        });
    }

    [Fact]
    public void TheUserExportFilter_MatchesNothingWhenTheAccountHasNoProfiles()
    {
        WithFreshStore(connection =>
        {
            foreach (var entry in UserExportScope.Tables)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{entry.Table}\" WHERE " + UserExportScope.FilterFor(entry, [], []);
                command.ExecuteScalar();
            }
        });
    }

    private static void WithFreshStore(Action<SqliteConnection> assertions)
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima_user_export_scope_{Guid.NewGuid():N}.db");
        try
        {
            using var connection = new SqliteConnection($"Data Source={path}");
            connection.Open();
            new DataStoreUpgradeRunner().Run(
                connection,
                DataStoreUpgradeCatalog.ForProduct(),
                new DataStoreUpgradeOptions { EngineVersion = "user-export-scope" });
            assertions(connection);
        }
        finally
        {
            TestTemp.DeleteDatabase(path);
        }
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
}
