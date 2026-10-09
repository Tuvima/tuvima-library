using System.Text.Json;
using Microsoft.Data.Sqlite;

if (args.Length < 2)
{
    return Usage();
}

var command = args[0].ToLowerInvariant();
var options = ParseOptions(args.Skip(1).ToArray());
if (!options.TryGetValue("db", out var databasePath))
{
    return Usage();
}

databasePath = Path.GetFullPath(databasePath);
if (!File.Exists(databasePath))
{
    throw new FileNotFoundException("Launch-gate database was not found.", databasePath);
}

return command switch
{
    "snapshot" => Snapshot(databasePath, options),
    "prepare" => Prepare(databasePath, options),
    "validate" => Validate(databasePath),
    _ => Usage(),
};

static int Snapshot(string databasePath, IReadOnlyDictionary<string, string> options)
{
    if (!options.TryGetValue("to", out var targetPath))
    {
        return Usage();
    }

    targetPath = Path.GetFullPath(targetPath);
    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)
        ?? throw new InvalidOperationException("Snapshot target must have a parent directory."));
    using var source = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadOnly,
    }.ToString());
    using var target = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = targetPath,
        Mode = SqliteOpenMode.ReadWriteCreate,
    }.ToString());
    source.Open();
    target.Open();
    source.BackupDatabase(target);
    Console.WriteLine(JsonSerializer.Serialize(new { snapshot = true, target = targetPath }));
    return 0;
}

static int Prepare(string databasePath, IReadOnlyDictionary<string, string> options)
{
    if (!options.TryGetValue("from", out var sourceRoot)
        || !options.TryGetValue("to", out var targetRoot))
    {
        return Usage();
    }

    sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceRoot));
    targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetRoot));
    using var connection = Open(databasePath);
    using var transaction = connection.BeginTransaction();
    using var command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = """
        UPDATE media_assets
        SET file_path_root = @target || substr(file_path_root, length(@source) + 1)
        WHERE substr(file_path_root, 1, length(@source)) = @source COLLATE NOCASE;
        """;
    command.Parameters.AddWithValue("@source", sourceRoot);
    command.Parameters.AddWithValue("@target", targetRoot);
    var rebased = command.ExecuteNonQuery();
    transaction.Commit();
    Console.WriteLine(JsonSerializer.Serialize(new { prepared = true, rebased_assets = rebased }));
    return 0;
}

static int Validate(string databasePath)
{
    using var connection = Open(databasePath);
    var integrity = Scalar(connection, "PRAGMA integrity_check;") ?? "unknown";
    var foreignKeyErrors = CountRows(connection, "PRAGMA foreign_key_check;");
    var falseAiQuarantines = ScalarInt(connection, """
        SELECT COUNT(1)
        FROM ai_feature_artifacts
        WHERE status = 'Poisoned'
          AND last_outcome_category IN ('UnavailableCapability', 'TransientDependencyFailure', 'Cancellation');
        """);
    var falseIdentityFailures = ScalarInt(connection, """
        SELECT COUNT(1)
        FROM identity_jobs
        WHERE state = 'Failed'
          AND last_outcome_category IN ('UnavailableCapability', 'TransientDependencyFailure', 'Cancellation');
        """);
    var falseOperationFailures = ScalarInt(connection, """
        SELECT COUNT(1)
        FROM media_operations
        WHERE status IN ('failed_terminal', 'dead_lettered')
          AND last_outcome_category IN ('UnavailableCapability', 'TransientDependencyFailure', 'Cancellation');
        """);

    var valid = string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase)
                && foreignKeyErrors == 0
                && falseAiQuarantines == 0
                && falseIdentityFailures == 0
                && falseOperationFailures == 0;
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        valid,
        integrity,
        foreign_key_errors = foreignKeyErrors,
        false_ai_quarantines = falseAiQuarantines,
        false_identity_failures = falseIdentityFailures,
        false_operation_failures = falseOperationFailures,
    }));
    return valid ? 0 : 1;
}

static SqliteConnection Open(string databasePath)
{
    var connection = new SqliteConnection(new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
    }.ToString());
    connection.Open();
    return connection;
}

static string? Scalar(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    return command.ExecuteScalar()?.ToString();
}

static int ScalarInt(SqliteConnection connection, string sql) =>
    int.TryParse(Scalar(connection, sql), out var count) ? count : -1;

static int CountRows(SqliteConnection connection, string sql)
{
    using var command = connection.CreateCommand();
    command.CommandText = sql;
    using var reader = command.ExecuteReader();
    var count = 0;
    while (reader.Read())
    {
        count++;
    }
    return count;
}

static Dictionary<string, string> ParseOptions(string[] values)
{
    var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var index = 0; index < values.Length; index += 2)
    {
        if (index + 1 >= values.Length || !values[index].StartsWith("--", StringComparison.Ordinal))
        {
            throw new ArgumentException("Launch-gate options must use --name value pairs.");
        }
        result[values[index][2..]] = values[index + 1];
    }
    return result;
}

static int Usage()
{
    Console.Error.WriteLine("Usage: MediaEngine.LaunchGate snapshot --db <source-path> --to <copy-path>");
    Console.Error.WriteLine("   or: MediaEngine.LaunchGate prepare --db <path> --from <source-root> --to <copied-root>");
    Console.Error.WriteLine("   or: MediaEngine.LaunchGate validate --db <path>");
    return 2;
}
