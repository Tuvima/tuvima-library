using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage;

/// <summary>
/// Test-only speed-up. Upgrading a brand-new data store runs ~160 KB of schema SQL (about half a second), and the
/// test suites create thousands of them. When <c>TUVIMA_SCHEMA_TEMPLATE=1</c> (set by the shared test setup, never
/// by the Engine), the first new store in the process is upgraded the normal way and kept as a template; every
/// later new store starts as a copy of that file. Anything unusual (a backups folder, custom steps, an existing
/// file) skips this and takes the normal path, and the normal startup checks still run on the copy.
/// </summary>
internal static class SchemaTemplate
{
    internal const string EnvironmentVariable = "TUVIMA_SCHEMA_TEMPLATE";

    private static readonly object Gate = new();
    private static string? _templatePath;
    private static bool _unavailable;

    internal static bool Enabled
        => Environment.GetEnvironmentVariable(EnvironmentVariable) == "1";

    /// <summary>Copies the template to <paramref name="target"/>; leaves nothing behind when it cannot.</summary>
    internal static void TryCopyTo(string target)
    {
        var template = GetOrCreateTemplate();
        if (template is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.Copy(template, target, overwrite: false);
    }

    private static string? GetOrCreateTemplate()
    {
        lock (Gate)
        {
            if (_templatePath is not null || _unavailable)
            {
                return _templatePath;
            }

            var path = Path.Combine(Path.GetTempPath(), $"schema-template-{Guid.NewGuid():N}.db");
            try
            {
                using (var template = new DatabaseConnection(path))
                {
                    template.InitializeSchemaCore();
                }

                // Fold the write-ahead log into the main file, so copying that one file is a complete data store.
                using (var checkpoint = new SqliteConnection($"Data Source={path};Pooling=False"))
                {
                    checkpoint.Open();
                    using var command = checkpoint.CreateCommand();
                    command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                    command.ExecuteNonQuery();
                }

                if (File.Exists(path + "-wal") && new FileInfo(path + "-wal").Length > 0)
                {
                    _unavailable = true;
                    return null;
                }

                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Cleanup();
                _templatePath = path;
                return path;
            }
            catch (Exception exception) when (exception is IOException or SqliteException or UnauthorizedAccessException)
            {
                // Fall back to the normal path for every store; say why once so a slow run is explainable.
                Console.Error.WriteLine($"[SchemaTemplate] Not used: {exception.Message}");
                _unavailable = true;
                return null;
            }
        }
    }

    private static void Cleanup()
    {
        if (_templatePath is null)
        {
            return;
        }

        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try
            {
                File.Delete(_templatePath + suffix);
            }
            catch (IOException)
            {
                // Best effort: the private test temp folder is swept on the next run anyway.
            }
        }
    }
}
