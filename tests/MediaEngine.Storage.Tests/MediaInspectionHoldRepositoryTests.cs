using Dapper;

namespace MediaEngine.Storage.Tests;

public sealed class MediaInspectionHoldRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaInspectionHoldRepository _holds;

    public MediaInspectionHoldRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_inspection_holds_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _holds = new MediaInspectionHoldRepository(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        File.Delete(_dbPath);
    }

    [Fact]
    public async Task Upsert_HoldsTheAssetAndCountsItForThatTool()
    {
        var asset = SeedAsset();

        await _holds.UpsertAsync(asset, "ffmpeg", DateTimeOffset.UtcNow);

        Assert.True(await _holds.ExistsAsync(asset));
        Assert.Equal(1, await _holds.CountByToolAsync("ffmpeg"));
        Assert.Equal(0, await _holds.CountByToolAsync("tesseract"));
    }

    [Fact]
    public async Task Upsert_ForAnAssetAlreadyHeld_KeepsASingleHoldAndTheOriginalTime()
    {
        var asset = SeedAsset();
        var first = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);

        await _holds.UpsertAsync(asset, "ffmpeg", first);
        await _holds.UpsertAsync(asset, "ffmpeg", first.AddDays(3));

        Assert.Equal(1, await _holds.CountByToolAsync("ffmpeg"));
        using var conn = _db.CreateConnection();
        var heldAt = conn.ExecuteScalar<string>("SELECT held_at FROM media_inspection_holds;");
        Assert.StartsWith("2026-10-01T08:00:00", heldAt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Delete_ReleasesTheHold_AndIsHarmlessWhenThereIsNone()
    {
        var asset = SeedAsset();
        await _holds.UpsertAsync(asset, "ffmpeg", DateTimeOffset.UtcNow);

        await _holds.DeleteAsync(asset);
        await _holds.DeleteAsync(asset);

        Assert.False(await _holds.ExistsAsync(asset));
        Assert.Equal(0, await _holds.CountByToolAsync("ffmpeg"));
    }

    [Fact]
    public async Task ListAssetIdsByTool_ReturnsTheOldestFirstInPagesAndOnlyForThatTool()
    {
        var oldest = SeedAsset();
        var middle = SeedAsset();
        var newest = SeedAsset();
        var otherTool = SeedAsset();
        var start = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        await _holds.UpsertAsync(newest, "ffmpeg", start.AddHours(2));
        await _holds.UpsertAsync(oldest, "ffmpeg", start);
        await _holds.UpsertAsync(middle, "ffmpeg", start.AddHours(1));
        await _holds.UpsertAsync(otherTool, "tesseract", start);

        var firstPage = await _holds.ListAssetIdsByToolAsync("ffmpeg", limit: 2);
        var secondPage = await _holds.ListAssetIdsByToolAsync("ffmpeg", limit: 2, skip: 2);

        Assert.Equal([oldest, middle], firstPage);
        Assert.Equal([newest], secondPage);
    }

    [Fact]
    public async Task DeletingTheAsset_RemovesItsHold()
    {
        var asset = SeedAsset();
        await _holds.UpsertAsync(asset, "ffmpeg", DateTimeOffset.UtcNow);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM media_assets WHERE id = @asset;", new { asset });
        }

        Assert.Equal(0, await _holds.CountByToolAsync("ffmpeg"));
    }

    private Guid SeedAsset()
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT INTO works (id, media_type) VALUES (@workId, 'Movies');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @contentHash, @path);
            """,
            new
            {
                workId,
                editionId,
                assetId,
                contentHash = Guid.NewGuid().ToString("N"),
                path = $"C:/library/{assetId:N}.mkv",
            });
        return assetId;
    }
}
