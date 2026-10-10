using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class MediaAssetCoverageRepositoryTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaAssetCoverageRepository _repo;

    public MediaAssetCoverageRepositoryTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_coverage_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _repo = new MediaAssetCoverageRepository(_db);
    }

    [Fact]
    public async Task ReplaceAndList_RoundTripsInPositionOrder_WithBlobIds()
    {
        var (assetId, firstWork) = CreateEpisodeWithAsset();
        var secondWork = CreateEpisode();

        await _repo.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, secondWork, 2, 2700.5, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(assetId, firstWork, 1, 0, 2700.5, MediaAssetCoverage.SourceFilename),
        ]);

        var rows = await _repo.ListByAssetAsync(assetId);
        Assert.Equal([firstWork, secondWork], rows.Select(r => r.WorkId));
        Assert.Equal(2700.5, rows[1].StartSeconds);
        Assert.Null(rows[1].EndSeconds);

        using var conn = _db.CreateConnection();
        var storage = conn.QueryFirst<(string Asset, int AssetLength, string Work, int WorkLength)>(
            "SELECT typeof(asset_id), length(asset_id), typeof(work_id), length(work_id) FROM media_asset_coverage LIMIT 1;");
        Assert.Equal(("blob", 16, "blob", 16), storage);
    }

    [Fact]
    public async Task Replace_OverwritesPreviousCoverage_AndEmptyListClears()
    {
        var (assetId, firstWork) = CreateEpisodeWithAsset();
        var secondWork = CreateEpisode();

        await _repo.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, firstWork, 1, null, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(assetId, secondWork, 2, null, null, MediaAssetCoverage.SourceFilename),
        ]);
        await _repo.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, firstWork, 1, null, null, MediaAssetCoverage.SourceManual),
        ]);

        var rows = await _repo.ListByAssetAsync(assetId);
        Assert.Single(rows);
        Assert.Equal(MediaAssetCoverage.SourceManual, rows[0].Source);

        await _repo.ReplaceForAssetAsync(assetId, []);
        Assert.Empty(await _repo.ListByAssetAsync(assetId));
    }

    [Fact]
    public async Task ListByWorks_FindsCoveringAssets_InOneBatch()
    {
        var (assetA, workA1) = CreateEpisodeWithAsset();
        var workA2 = CreateEpisode();
        var (assetB, workB1) = CreateEpisodeWithAsset();
        var unrelated = CreateEpisode();

        await _repo.ReplaceForAssetAsync(assetA,
        [
            new MediaAssetCoverage(assetA, workA1, 1, null, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(assetA, workA2, 2, null, null, MediaAssetCoverage.SourceFilename),
        ]);
        await _repo.ReplaceForAssetAsync(assetB,
        [
            new MediaAssetCoverage(assetB, workB1, 1, null, null, MediaAssetCoverage.SourceChapters),
        ]);

        var single = await _repo.ListByWorkAsync(workA2);
        Assert.Equal(assetA, Assert.Single(single).AssetId);

        var batch = await _repo.ListByWorksAsync([workA2, workB1, unrelated, workA2]);
        Assert.Equal(2, batch.Count);
        Assert.Contains(batch, r => r.AssetId == assetA && r.WorkId == workA2);
        Assert.Contains(batch, r => r.AssetId == assetB && r.WorkId == workB1);

        Assert.Empty(await _repo.ListByWorksAsync([]));
        Assert.Empty(await _repo.ListByWorkAsync(unrelated));
    }

    [Fact]
    public async Task DeleteForAsset_RemovesOnlyThatAssetsRows()
    {
        var (assetA, workA) = CreateEpisodeWithAsset();
        var (assetB, workB) = CreateEpisodeWithAsset();
        await _repo.ReplaceForAssetAsync(assetA, [new MediaAssetCoverage(assetA, workA, 1, null, null, MediaAssetCoverage.SourceManual)]);
        await _repo.ReplaceForAssetAsync(assetB, [new MediaAssetCoverage(assetB, workB, 1, null, null, MediaAssetCoverage.SourceManual)]);

        await _repo.DeleteForAssetAsync(assetA);

        Assert.Empty(await _repo.ListByAssetAsync(assetA));
        Assert.Single(await _repo.ListByAssetAsync(assetB));
    }

    [Fact]
    public async Task DeletingTheAsset_CascadesCoverage()
    {
        var (assetId, workId) = CreateEpisodeWithAsset();
        await _repo.ReplaceForAssetAsync(assetId, [new MediaAssetCoverage(assetId, workId, 1, null, null, MediaAssetCoverage.SourceManual)]);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DELETE FROM media_assets WHERE id = @assetId;", new { assetId });
        }

        Assert.Empty(await _repo.ListByWorkAsync(workId));
    }

    [Fact]
    public async Task Replace_RejectsInvalidCoverage_AndKeepsExistingRows()
    {
        var (assetId, firstWork) = CreateEpisodeWithAsset();
        var secondWork = CreateEpisode();
        await _repo.ReplaceForAssetAsync(assetId, [new MediaAssetCoverage(assetId, firstWork, 1, null, null, MediaAssetCoverage.SourceManual)]);

        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, firstWork, 1, null, null, MediaAssetCoverage.SourceManual),
            new MediaAssetCoverage(assetId, firstWork, 2, null, null, MediaAssetCoverage.SourceManual),
        ]));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, firstWork, 1, null, null, MediaAssetCoverage.SourceManual),
            new MediaAssetCoverage(assetId, secondWork, 1, null, null, MediaAssetCoverage.SourceManual),
        ]));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
            [new MediaAssetCoverage(assetId, firstWork, 0, null, null, MediaAssetCoverage.SourceManual)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
            [new MediaAssetCoverage(assetId, firstWork, 1, null, null, "guess")]));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
            [new MediaAssetCoverage(assetId, firstWork, 1, 100, 50, MediaAssetCoverage.SourceManual)]));
        await Assert.ThrowsAsync<ArgumentException>(() => _repo.ReplaceForAssetAsync(assetId,
            [new MediaAssetCoverage(Guid.NewGuid(), firstWork, 1, null, null, MediaAssetCoverage.SourceManual)]));

        Assert.Single(await _repo.ListByAssetAsync(assetId));
    }

    [Fact]
    public void Schema_AppliesToADatabaseThatLacksTheTable()
    {
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("DROP TABLE media_asset_coverage;");
        }

        _db.InitializeSchema();
        _db.RunStartupChecks();

        using var check = _db.CreateConnection();
        var count = check.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'media_asset_coverage';");
        Assert.Equal(1, count);
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM media_asset_coverage;"));
    }

    public void Dispose()
    {
        _db.Dispose();
        TestTemp.DeleteDatabase(_dbPath);
    }

    private Guid CreateEpisode()
    {
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@collectionId, @createdAt);",
            new { collectionId, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("""
            INSERT INTO works (id, collection_id, media_type, work_kind, is_catalog_only)
            VALUES (@workId, @collectionId, 'TV', 'standalone', 0);
            """, new { workId, collectionId });
        return workId;
    }

    private (Guid AssetId, Guid WorkId) CreateEpisodeWithAsset()
    {
        var workId = CreateEpisode();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _db.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, @path, 'Normal');
            """, new { assetId, editionId, contentHash = $"hash_{assetId:N}", path = $"/library/{assetId:N}.mkv" });
        return (assetId, workId);
    }
}
