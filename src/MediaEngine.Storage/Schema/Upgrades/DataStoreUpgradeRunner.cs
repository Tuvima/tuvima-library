using System.Globalization;
using System.Reflection;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Schema.Upgrades;

/// <summary>What the runner is about to apply, handed to <see cref="DataStoreUpgradeOptions.BeforeFirstPendingStep"/>.</summary>
/// <param name="FromNumber">Highest step already applied (always at least 1 when the hook runs).</param>
/// <param name="PendingSteps">Steps that will run, in order.</param>
internal sealed record DataStoreUpgradePlan(int FromNumber, IReadOnlyList<DataStoreUpgradeStep> PendingSteps);

/// <summary>Settings for one <see cref="DataStoreUpgradeRunner.Run"/>.</summary>
internal sealed class DataStoreUpgradeOptions
{
    /// <summary>Engine version recorded against each applied step.</summary>
    public string EngineVersion { get; init; } = DefaultEngineVersion();

    /// <summary>Source of the applied-at time and step durations.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Where the "newer data store" refusal tells the person to look for an older backup.</summary>
    public string BackupLocationHint { get; init; } = "your backups folder";

    /// <summary>
    /// Called once, only when at least one step is pending and the data store already has an applied record
    /// (so not for a brand-new data store). May return the path of a safety copy, which the runner reports
    /// in the result and in failure messages.
    /// </summary>
    public Func<DataStoreUpgradePlan, string?>? BeforeFirstPendingStep { get; init; }

    private static string DefaultEngineVersion()
    {
        var informational = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        var version = informational?.Split('+')[0];
        return string.IsNullOrWhiteSpace(version) ? "unknown" : version;
    }
}

/// <summary>What one run did.</summary>
/// <param name="FromNumber">Highest step applied before the run (0 for a new data store).</param>
/// <param name="ToNumber">Highest step applied after the run.</param>
/// <param name="Applied">The steps this run applied, in order, with how long each took.</param>
/// <param name="SafetyCopyPath">Path returned by the safety-copy hook, if any.</param>
internal sealed record DataStoreUpgradeResult(
    int FromNumber,
    int ToNumber,
    IReadOnlyList<(int Number, string Name, TimeSpan Duration)> Applied,
    string? SafetyCopyPath)
{
    /// <summary>Lines for the startup log, one per applied step plus the safety copy location when there is one.</summary>
    public IReadOnlyList<string> DescribeForLog()
    {
        var lines = new List<string>();
        if (SafetyCopyPath is not null)
        {
            lines.Add($"Safety copy of the data store before the upgrade: {SafetyCopyPath}");
        }

        foreach (var (number, name, duration) in Applied)
        {
            var milliseconds = (long)Math.Round(duration.TotalMilliseconds);
            lines.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"Applied data store upgrade step {DataStoreUpgradeCatalog.Format(number)} ({name}) in {milliseconds} ms"));
        }

        return lines;
    }
}

/// <summary>
/// Applies pending upgrade steps to a data store, one transaction per step with its record written in the
/// same transaction, and refuses to touch a data store whose record does not match the known steps.
/// Synchronous on purpose: this is startup code.
/// </summary>
internal sealed class DataStoreUpgradeRunner
{
    private const string CreateRecordTableSql = """
        CREATE TABLE IF NOT EXISTS data_store_upgrades (
            number         INTEGER NOT NULL PRIMARY KEY,
            name           TEXT    NOT NULL,
            fingerprint    TEXT    NOT NULL,
            applied_at     TEXT    NOT NULL,
            engine_version TEXT    NOT NULL
        );
        """;

    /// <summary>
    /// Applies every pending step in number order.
    /// </summary>
    /// <param name="connection">An open connection with no transaction in progress.</param>
    /// <param name="steps">All known steps; validated and sorted here.</param>
    /// <param name="options">Version, clock, backup hint and safety-copy hook.</param>
    /// <exception cref="DataStoreUpgradeCatalogException">The known steps are malformed.</exception>
    /// <exception cref="DataStoreUpgradeRefusedException">The recorded steps do not match the known steps.</exception>
    /// <exception cref="DataStoreUpgradeFailedException">A step failed; it was rolled back.</exception>
    public DataStoreUpgradeResult Run(
        SqliteConnection connection,
        IReadOnlyList<DataStoreUpgradeStep> steps,
        DataStoreUpgradeOptions options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(steps);
        ArgumentNullException.ThrowIfNull(options);

        var known = DataStoreUpgradeCatalog.Validate(steps);
        Execute(connection, CreateRecordTableSql);
        var applied = ReadApplied(connection);

        CheckRecord(applied, known, options);

        var appliedCount = applied.Count;
        var pending = known.Where(step => step.Number > appliedCount).ToList();
        if (pending.Count == 0)
        {
            return new DataStoreUpgradeResult(appliedCount, appliedCount, [], null);
        }

        string? safetyCopyPath = null;
        if (appliedCount > 0 && options.BeforeFirstPendingStep is not null)
        {
            safetyCopyPath = options.BeforeFirstPendingStep(new DataStoreUpgradePlan(appliedCount, pending));
        }

        var done = new List<(int Number, string Name, TimeSpan Duration)>();
        foreach (var step in pending)
        {
            var started = options.Clock.GetTimestamp();
            ApplyStep(connection, step, options, safetyCopyPath);
            done.Add((step.Number, step.Name, options.Clock.GetElapsedTime(started)));
        }

        return new DataStoreUpgradeResult(appliedCount, pending[^1].Number, done, safetyCopyPath);
    }

    private static void CheckRecord(
        IReadOnlyList<AppliedRow> applied,
        IReadOnlyList<DataStoreUpgradeStep> known,
        DataStoreUpgradeOptions options)
    {
        for (var index = 0; index < applied.Count; index++)
        {
            if (applied[index].Number != index + 1)
            {
                throw new DataStoreUpgradeRefusedException(
                    $"The data store's record of applied upgrade steps is damaged: expected step {DataStoreUpgradeCatalog.Format(index + 1)} but found {DataStoreUpgradeCatalog.Format(applied[index].Number)}. "
                    + $"Restore a backup from {options.BackupLocationHint}.");
            }
        }

        var highestKnown = known.Count == 0 ? 0 : known[^1].Number;
        if (applied.Count > 0 && applied[^1].Number > highestKnown)
        {
            throw new DataStoreUpgradeRefusedException(
                $"This data store was upgraded by a newer version of Tuvima (step {DataStoreUpgradeCatalog.Format(applied[^1].Number)}). "
                + $"Install that version, or restore an older backup from {options.BackupLocationHint}.");
        }

        foreach (var row in applied)
        {
            var step = known[row.Number - 1];
            if (!string.Equals(step.Fingerprint, row.Fingerprint, StringComparison.Ordinal)
                || !string.Equals(step.Name, row.Name, StringComparison.Ordinal))
            {
                throw new DataStoreUpgradeRefusedException(
                    $"Upgrade step {DataStoreUpgradeCatalog.Format(row.Number)} ({row.Name}) has been changed after it was applied. "
                    + "Applied steps must never be edited - add a new step instead.");
            }
        }
    }

    private static void ApplyStep(
        SqliteConnection connection,
        DataStoreUpgradeStep step,
        DataStoreUpgradeOptions options,
        string? safetyCopyPath)
    {
        var foreignKeysOff = step.RequiresForeignKeysOff;
        try
        {
            if (foreignKeysOff)
            {
                // SQLite ignores this inside a transaction, so it is set before the step's one begins.
                Execute(connection, "PRAGMA foreign_keys = OFF;");
            }

            DatabaseConnection.ExecuteStartupTransaction(connection, transaction =>
            {
                step.Apply(connection, transaction);
                if (foreignKeysOff)
                {
                    FailOnOrphanedRows(connection, transaction);
                }

                Record(connection, transaction, step, options);
            });
        }
        catch (Exception exception)
        {
            throw new DataStoreUpgradeFailedException(
                step.Number,
                step.Name,
                safetyCopyPath,
                FailureMessage(step, exception, safetyCopyPath),
                exception);
        }
        finally
        {
            if (foreignKeysOff)
            {
                Execute(connection, "PRAGMA foreign_keys = ON;");
            }
        }
    }

    private static string FailureMessage(DataStoreUpgradeStep step, Exception exception, string? safetyCopyPath)
    {
        var message = $"Data store upgrade step {DataStoreUpgradeCatalog.Format(step.Number)} ({step.Name}) failed: {exception.Message} "
                      + "The data store was left as it was before this step.";
        return safetyCopyPath is null
            ? message
            : $"{message} A safety copy of the data store from before the upgrade is at {safetyCopyPath}.";
    }

    private static void FailOnOrphanedRows(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "PRAGMA foreign_key_check;";
        using var reader = command.ExecuteReader();
        if (reader.Read())
        {
            throw new InvalidOperationException(
                $"The step left rows in table '{reader.GetString(0)}' that point at rows which no longer exist.");
        }
    }

    private static void Record(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DataStoreUpgradeStep step,
        DataStoreUpgradeOptions options)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO data_store_upgrades (number, name, fingerprint, applied_at, engine_version)
            VALUES ($number, $name, $fingerprint, $appliedAt, $engineVersion);
            """;
        command.Parameters.AddWithValue("$number", step.Number);
        command.Parameters.AddWithValue("$name", step.Name);
        command.Parameters.AddWithValue("$fingerprint", step.Fingerprint);
        command.Parameters.AddWithValue("$appliedAt", options.Clock.GetUtcNow().UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$engineVersion", options.EngineVersion);
        command.ExecuteNonQuery();
    }

    private static List<AppliedRow> ReadApplied(SqliteConnection connection)
    {
        var rows = new List<AppliedRow>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT number, name, fingerprint FROM data_store_upgrades ORDER BY number;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new AppliedRow(reader.GetInt32(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private sealed record AppliedRow(int Number, string Name, string Fingerprint);
}
