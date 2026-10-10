using Dapper;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A combined file (for example S01E01E02.mkv) is attached to its first episode and lists the
/// other episodes it covers. Every covered episode is owned and resolves to the host file, but
/// the file itself is still a single file.
/// </summary>
public sealed class CombinedEpisodeOwnershipTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly MediaAssetCoverageRepository _coverage;
    private readonly CollectionBrowseReadService _browse;

    public CombinedEpisodeOwnershipTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_combined_ownership_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _coverage = new MediaAssetCoverageRepository(_database);
        _browse = new CollectionBrowseReadService(
            new CollectionRepository(_database),
            _database,
            NullLogger<CollectionBrowseReadService>.Instance);
    }

    [Fact]
    public async Task CombinedFile_OwnsEveryCoveredEpisode_ButCountsAsOneFile()
    {
        var combined = CreateEpisodeWithAsset();
        var coveredEpisode = CreateEpisode();
        var normal = CreateEpisodeWithAsset();
        await _coverage.ReplaceForAssetAsync(combined.AssetId,
        [
            new MediaAssetCoverage(combined.AssetId, combined.WorkId, 1, null, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(combined.AssetId, coveredEpisode, 2, null, null, MediaAssetCoverage.SourceFilename),
        ]);

        using var conn = _database.CreateConnection();
        Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT work_id) FROM work_owned_assets;"));
        Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT asset_id) FROM work_owned_assets;"));
        Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM work_owned_assets;"));

        var assets = await _browse.GetPrimaryAssetIdsAsync(
            [combined.WorkId, coveredEpisode, normal.WorkId], CancellationToken.None);
        Assert.Equal(combined.AssetId, assets[combined.WorkId]);
        Assert.Equal(combined.AssetId, assets[coveredEpisode]);
        Assert.Equal(normal.AssetId, assets[normal.WorkId]);
    }

    [Fact]
    public async Task FilesWithoutCoverage_BehaveExactlyAsBefore()
    {
        var first = CreateEpisodeWithAsset();
        var second = CreateEpisodeWithAsset();
        var unowned = CreateEpisode();

        using var conn = _database.CreateConnection();
        Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM work_owned_assets;"));
        Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM work_owned_assets WHERE is_covered = 1;"));

        var assets = await _browse.GetPrimaryAssetIdsAsync(
            [first.WorkId, second.WorkId, unowned], CancellationToken.None);
        Assert.Equal(first.AssetId, assets[first.WorkId]);
        Assert.Equal(second.AssetId, assets[second.WorkId]);
        Assert.False(assets.ContainsKey(unowned));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { /* Test cleanup is best effort. */ }
        try { File.Delete(_databasePath); } catch { /* Test cleanup is best effort. */ }
    }

    private Guid CreateEpisode()
    {
        var collectionId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
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
        using var conn = _database.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, @path, 'Normal');
            """, new { assetId, editionId, contentHash = $"hash_{assetId:N}", path = $"/library/{assetId:N}.mkv" });
        return (assetId, workId);
    }
}
