using Dapper;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

/// <summary>The development reset follows each seeded fixture through the data store to wherever ingestion filed it.</summary>
public sealed class DevelopmentFixtureLocationRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima_fixture_locations_{Guid.NewGuid():N}.db");
    private readonly string _books = Path.Combine(Path.GetTempPath(), "tuvima_fixture_locations", "library", "Books");
    private readonly DatabaseConnection _database;

    public DevelopmentFixtureLocationRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _database.RunStartupChecks();
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.ReleaseSqlitePool(_path);
        foreach (var file in new[] { _path, $"{_path}-wal", $"{_path}-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void AFiledFixture_IsFoundAtItsCurrentPath_WithOnlyTheTracksTheEngineWrote()
    {
        var seeded = Path.Combine(_books, "Dune.epub");
        var filed = Path.Combine(_books, "Books", "Dune (1965).epub");
        var generated = Path.ChangeExtension(filed, ".en.vtt");
        var supplied = Path.ChangeExtension(filed, ".fr.vtt");
        Track(seeded, filed, "complete", generated, supplied);

        var locations = new DevelopmentFixtureLocationRepository(_database)
            .FindCurrentLocations([seeded.ToUpperInvariant(), Path.Combine(_books, "Never Ingested.epub")]);

        var location = Assert.Single(locations);
        Assert.Equal(Path.GetFullPath(seeded), location.DetectedPath, ignoreCase: true);
        Assert.Equal(Path.GetFullPath(filed), location.CurrentPath);
        Assert.Equal([Path.GetFullPath(generated)], location.GeneratedFiles);
    }

    [Fact]
    public void AFixtureSkippedAsACopyOfAnotherFile_DoesNotLeadToThatOtherFile()
    {
        var seeded = Path.Combine(_books, "Dune.epub");
        var someonesCopy = Path.Combine(_books, "My Dune.epub");
        Track(seeded, someonesCopy, "duplicate", generatedTrack: null, userTrack: null);

        var locations = new DevelopmentFixtureLocationRepository(_database).FindCurrentLocations([seeded]);

        Assert.Empty(locations);
    }

    [Fact]
    public void AStoredPathThatIsNotAPath_IsLeftOutRatherThanStoppingTheLookup()
    {
        var seeded = Path.Combine(_books, "Dune.epub");
        var filed = Path.Combine(_books, "Books", "Dune (1965).epub");
        Track(seeded, filed, "complete", generatedTrack: null, userTrack: null);
        Track("bad\0path", filed + ".other", "complete", generatedTrack: null, userTrack: null);

        var locations = new DevelopmentFixtureLocationRepository(_database).FindCurrentLocations([seeded, "also\0bad"]);

        Assert.Equal(Path.GetFullPath(filed), Assert.Single(locations).CurrentPath);
    }

    private void Track(string detected, string current, string status, string? generatedTrack, string? userTrack)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("INSERT INTO works (id, media_type) VALUES (@workId, 'Books');", new { workId });
        connection.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        connection.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @hash, @current, 'Normal');
            """, new { assetId, editionId, hash = Guid.NewGuid().ToString("N"), current });
        connection.Execute(
            "INSERT INTO ingestion_log (id, file_path, media_asset_id, status) VALUES (@id, @detected, @assetId, @status);",
            new { id = Guid.NewGuid(), detected, assetId, status });
        foreach (var (path, userOwned) in new[] { (generatedTrack, 0), (userTrack, 1) })
        {
            if (path is null)
            {
                continue;
            }

            connection.Execute("""
                INSERT INTO text_tracks (id, asset_id, kind, provider, source_format, normalized_format, local_path, sidecar_path, is_user_owned)
                VALUES (@id, @assetId, 'Subtitles', 'test', 'vtt', 'vtt', @path, @path, @userOwned);
                """, new { id = Guid.NewGuid(), assetId, path, userOwned });
        }
    }
}
