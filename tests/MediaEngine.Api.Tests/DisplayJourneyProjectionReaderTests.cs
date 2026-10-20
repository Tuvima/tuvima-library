using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

public sealed class DisplayJourneyProjectionReaderTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly Guid _profileId = Guid.NewGuid();

    public DisplayJourneyProjectionReaderTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_journey_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_db);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task LoadAsync_ACoverTakesTheEnginesOwnCopyBeforeTheProvidersLink()
    {
        var managedCover = $"/stream/artwork/{Guid.NewGuid():D}";
        var (workId, assetId) = SeedStartedMovie("Both Covers.mkv");
        // Alphabetically 'cover' comes first; the read must still take the Engine's copy, written first.
        SetValue(workId, "cover", "https://image.tmdb.org/t/p/original/both.jpg");
        SetValue(workId, "cover_url", managedCover);
        var (providerWorkId, providerAssetId) = SeedStartedMovie("Provider Only.mkv");
        SetValue(providerWorkId, "cover", "https://image.tmdb.org/t/p/original/only.jpg");

        var rows = await new DisplayJourneyProjectionReader(_db).LoadAsync(_profileId, lane: null, CancellationToken.None);

        Assert.Equal(managedCover, Assert.Single(rows, row => row.AssetId == assetId).CoverUrl);
        Assert.Equal($"/stream/{providerAssetId:D}/cover", Assert.Single(rows, row => row.AssetId == providerAssetId).CoverUrl);
    }

    private (Guid WorkId, Guid AssetId) SeedStartedMovie(string fileName)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type, work_kind, ownership, is_catalog_only) VALUES (@workId, 'Movies', 'standalone', 'Owned', 0);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root) VALUES (@assetId, @editionId, @hash, @fileName);
            INSERT INTO user_states (user_id, asset_id, progress_pct, last_accessed, extended_properties)
            VALUES (@profileId, @assetId, 10, @now, '{}');
            """,
            new
            {
                workId,
                editionId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                fileName,
                profileId = _profileId,
                now = DateTimeOffset.UtcNow.ToString("O"),
            });
        return (workId, assetId);
    }

    private void SetValue(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, @now);",
            new { entityId, key, value, now = DateTimeOffset.UtcNow.ToString("O") });
    }
}
