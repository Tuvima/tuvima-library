using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class LargeLibraryReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;

    public LargeLibraryReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_large_library_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public void LargeLibraryIndexes_ExistAfterStartupMigrations()
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'index';";
        using var reader = cmd.ExecuteReader();
        var indexes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
        {
            indexes.Add(reader.GetString(0));
        }

        Assert.Contains("idx_canonical_values_key_value_entity", indexes);
        Assert.Contains("idx_person_media_links_person", indexes);
        Assert.Contains("idx_identity_jobs_activity_latest", indexes);

        // The live page and Recently Added read by file and entity, with no run record.
        Assert.Contains("idx_ingestion_log_media_asset", indexes);
        Assert.Contains("idx_media_operations_entity", indexes);
        Assert.Contains("idx_identity_jobs_entity_id", indexes);
    }
}
