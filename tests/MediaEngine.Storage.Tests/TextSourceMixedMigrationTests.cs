using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Text recognition added the "mixed" text source. A data store created before that only accepts
/// embedded, ocr and none, so startup widens the stored table definition in place.
/// </summary>
public sealed class TextSourceMixedMigrationTests : IDisposable
{
    private const string NewCheck = "IN ('embedded', 'ocr', 'mixed', 'none')";
    private const string OldCheck = "IN ('embedded', 'ocr', 'none')";

    private readonly List<string> _paths = [];

    [Fact]
    public void LegacyDataStoreAcceptsMixedAfterStartup()
    {
        var path = CreateStoreWithOldDefinition();
        Assert.Throws<SqliteException>(() => InsertMetadata(path, "mixed"));

        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
            database.RunStartupChecks();
        }

        SqliteConnection.ClearAllPools();
        InsertMetadata(path, "mixed");
        Assert.Equal("mixed", Scalar(path, "SELECT text_source FROM local_item_metadata;"));
        Assert.Contains(NewCheck, Scalar(path, "SELECT sql FROM sqlite_master WHERE name = 'local_item_metadata';"), StringComparison.Ordinal);
        Assert.Equal("ok", Scalar(path, "PRAGMA integrity_check;"));
    }

    [Fact]
    public void RunningStartupTwiceChangesNothingFurther()
    {
        var path = CreateStoreWithOldDefinition();
        for (var run = 0; run < 2; run++)
        {
            using var database = new DatabaseConnection(path);
            database.InitializeSchema();
            database.RunStartupChecks();
        }

        SqliteConnection.ClearAllPools();
        var definition = Scalar(path, "SELECT sql FROM sqlite_master WHERE name = 'local_item_metadata';");
        Assert.Contains(NewCheck, definition, StringComparison.Ordinal);
        Assert.DoesNotContain(OldCheck, definition, StringComparison.Ordinal);
    }

    [Fact]
    public void NewDataStoreAlreadyAcceptsMixed()
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
        }

        SqliteConnection.ClearAllPools();
        InsertMetadata(path, "mixed");
        Assert.Equal("mixed", Scalar(path, "SELECT text_source FROM local_item_metadata;"));
    }

    private string CreateStoreWithOldDefinition()
    {
        var path = NewPath();
        using (var database = new DatabaseConnection(path))
        {
            database.InitializeSchema();
        }

        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        var version = Convert.ToInt64(Execute(raw, "PRAGMA schema_version;", scalar: true), System.Globalization.CultureInfo.InvariantCulture);
        Execute(raw, "PRAGMA writable_schema = ON;");
        using (var update = raw.CreateCommand())
        {
            update.CommandText = "UPDATE sqlite_master SET sql = replace(sql, @new, @old) WHERE name = 'local_item_metadata';";
            update.Parameters.AddWithValue("@new", NewCheck);
            update.Parameters.AddWithValue("@old", OldCheck);
            update.ExecuteNonQuery();
        }

        Execute(raw, $"PRAGMA schema_version = {version + 1};");
        Execute(raw, "PRAGMA writable_schema = OFF;");
        return path;
    }

    private string NewPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"tuvima-text-source-{Guid.NewGuid():N}.db");
        _paths.Add(path);
        return path;
    }

    private static void InsertMetadata(string path, string textSource)
    {
        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        var item = Guid.NewGuid().ToByteArray();
        var library = Guid.NewGuid().ToByteArray();
        using (var household = raw.CreateCommand())
        {
            household.CommandText = """
                INSERT INTO households(id, name, created_at) VALUES(@household, 'Test', '2026-10-13T00:00:00Z');
                INSERT INTO view_shared_library(household_id, library_id, created_at, updated_at)
                VALUES(@household, @library, '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z');
                """;
            household.Parameters.AddWithValue("@household", Guid.NewGuid().ToByteArray());
            household.Parameters.AddWithValue("@library", library);
            household.ExecuteNonQuery();
        }

        using var insertItem = raw.CreateCommand();
        insertItem.CommandText = """
            INSERT INTO local_items(id, scope_kind, library_id, media_kind, primary_file_name, primary_mime_type, created_at, updated_at)
            VALUES(@id, 'shared', @library, 'document', 'scan.pdf', 'application/pdf', '2026-10-13T00:00:00Z', '2026-10-13T00:00:00Z');
            """;
        insertItem.Parameters.AddWithValue("@id", item);
        insertItem.Parameters.AddWithValue("@library", library);
        insertItem.ExecuteNonQuery();

        using var insertMetadata = raw.CreateCommand();
        insertMetadata.CommandText = "INSERT INTO local_item_metadata(item_id, text_source, updated_at) VALUES(@id, @source, '2026-10-13T00:00:00Z');";
        insertMetadata.Parameters.AddWithValue("@id", item);
        insertMetadata.Parameters.AddWithValue("@source", textSource);
        insertMetadata.ExecuteNonQuery();
    }

    private static string Scalar(string path, string sql)
    {
        SqliteConnection.ClearAllPools();
        using var raw = new SqliteConnection($"Data Source={path};Pooling=False");
        raw.Open();
        return Convert.ToString(Execute(raw, sql, scalar: true), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static object? Execute(SqliteConnection connection, string sql, bool scalar = false)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return scalar ? command.ExecuteScalar() : command.ExecuteNonQuery();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in _paths)
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
            {
                if (File.Exists(path + suffix))
                {
                    File.Delete(path + suffix);
                }
            }
        }
    }
}
