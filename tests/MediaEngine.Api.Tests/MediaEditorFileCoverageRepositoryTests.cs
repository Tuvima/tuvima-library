using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// The editor's "This file covers" picker sets which episodes of one season a TV file holds.
/// It must stay inside the file's season, respect the 6-episode cap, never take an episode
/// that already has its own file, and answer the file's "combined media" review.
/// </summary>
public sealed class MediaEditorFileCoverageRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly MediaEditorFileCoverageRepository _repository;
    private readonly MediaAssetCoverageRepository _coverage;

    public MediaEditorFileCoverageRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_file_coverage_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new MediaEditorFileCoverageRepository(_database);
        _coverage = new MediaAssetCoverageRepository(_database);
    }

    [Fact]
    public async Task Get_ListsTheSeasonsEpisodes_WithTheHostMarked()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);

        var result = await _repository.GetAsync(host.AssetId!.Value);

        Assert.Equal(FileCoverageOutcome.Ok, result.Outcome);
        var view = result.View!;
        Assert.Equal(host.WorkId, view.HostWorkId);
        Assert.Equal(2, view.Episodes.Count);
        Assert.True(view.Episodes[0].IsHost);
        Assert.True(view.Episodes[0].IsCovered);
        Assert.False(view.Episodes[1].IsCovered);
        Assert.Equal(second.WorkId, view.Episodes[1].WorkId);
    }

    [Fact]
    public async Task Get_OnAFileThatIsNotATvEpisode_IsNotFound()
    {
        var movie = CreateStandaloneWithAsset();

        var result = await _repository.GetAsync(movie);

        Assert.Equal(FileCoverageOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public async Task Replace_WritesManualCoverageInEpisodeOrder_AndRecordsHistory()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        var third = CreateEpisode(season, 3);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, [third.WorkId, host.WorkId, second.WorkId]);

        Assert.Equal(FileCoverageOutcome.Ok, result.Outcome);
        var rows = await _coverage.ListByAssetAsync(host.AssetId!.Value);
        Assert.Equal([host.WorkId, second.WorkId, third.WorkId], rows.Select(row => row.WorkId));
        Assert.Equal([1, 2, 3], rows.Select(row => row.Position));
        Assert.All(rows, row => Assert.Equal(MediaAssetCoverage.SourceManual, row.Source));
        using var conn = _database.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM system_activity WHERE action_type='MetadataUpdated' AND entity_type='MediaAsset';"));
        Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT work_id) FROM work_owned_assets;"));
    }

    [Fact]
    public async Task Replace_WithTheSameList_IsIdempotent()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);

        await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, second.WorkId]);
        var again = await _repository.ReplaceAsync(host.AssetId!.Value, [second.WorkId, host.WorkId]);

        Assert.Equal(FileCoverageOutcome.Ok, again.Outcome);
        Assert.Equal(2, (await _coverage.ListByAssetAsync(host.AssetId!.Value)).Count);
        using var conn = _database.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM system_activity WHERE action_type='MetadataUpdated';"));
    }

    [Fact]
    public async Task Replace_WithOnlyTheHost_ClearsCoverage()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, second.WorkId]);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId]);

        Assert.Equal(FileCoverageOutcome.Ok, result.Outcome);
        Assert.Empty(await _coverage.ListByAssetAsync(host.AssetId!.Value));
        Assert.False(result.View!.Episodes.Single(episode => episode.WorkId == second.WorkId).IsCovered);
    }

    [Fact]
    public async Task Replace_RejectsEpisodesFromAnotherSeason()
    {
        var season = CreateSeason();
        var otherSeason = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var stranger = CreateEpisode(otherSeason, 2);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, stranger.WorkId]);

        Assert.Equal(FileCoverageOutcome.Invalid, result.Outcome);
        Assert.Empty(await _coverage.ListByAssetAsync(host.AssetId!.Value));
    }

    [Fact]
    public async Task Replace_RejectsMoreThanSixEpisodes()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var works = new List<Guid> { host.WorkId };
        for (var number = 2; number <= 7; number++)
        {
            works.Add(CreateEpisode(season, number).WorkId);
        }

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, works);

        Assert.Equal(FileCoverageOutcome.Invalid, result.Outcome);
        Assert.Empty(await _coverage.ListByAssetAsync(host.AssetId!.Value));
    }

    [Fact]
    public async Task Replace_RejectsAListWithoutTheHost_AndDuplicates()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);

        var withoutHost = await _repository.ReplaceAsync(host.AssetId!.Value, [second.WorkId]);
        var duplicates = await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, second.WorkId, second.WorkId]);

        Assert.Equal(FileCoverageOutcome.Invalid, withoutHost.Outcome);
        Assert.Equal(FileCoverageOutcome.Invalid, duplicates.Outcome);
    }

    [Fact]
    public async Task Replace_RefusesAnEpisodeThatAlreadyHasItsOwnFile()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var owned = CreateEpisode(season, 2, withAsset: true);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, owned.WorkId]);

        Assert.Equal(FileCoverageOutcome.Conflict, result.Outcome);
        Assert.Empty(await _coverage.ListByAssetAsync(host.AssetId!.Value));
        var view = (await _repository.GetAsync(host.AssetId!.Value)).View!;
        Assert.True(view.Episodes.Single(episode => episode.WorkId == owned.WorkId).OwnedByOtherFile);
    }

    [Fact]
    public async Task Replace_RefusesAnEpisodeCoveredByAnotherFile()
    {
        var season = CreateSeason();
        var first = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        var third = CreateEpisode(season, 3, withAsset: true);
        await _repository.ReplaceAsync(third.AssetId!.Value, [third.WorkId, second.WorkId]);

        var result = await _repository.ReplaceAsync(first.AssetId!.Value, [first.WorkId, second.WorkId]);

        Assert.Equal(FileCoverageOutcome.Conflict, result.Outcome);
    }

    [Fact]
    public async Task Replace_ResolvesThePendingReviewForTheFile()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        var reviewId = Guid.NewGuid();
        using (var conn = _database.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO review_queue (id, entity_id, entity_type, trigger, status)
                VALUES (@reviewId, @assetId, 'MediaAsset', 'RetailMatchFailed', 'Pending');
                """, new { reviewId, assetId = host.AssetId });
        }

        await _repository.ReplaceAsync(host.AssetId!.Value, [host.WorkId, second.WorkId]);

        using var check = _database.CreateConnection();
        Assert.Equal("Resolved", check.ExecuteScalar<string>("SELECT status FROM review_queue WHERE id=@reviewId;", new { reviewId }));
    }

    public void Dispose()
    {
        try { _database.Dispose(); } catch { /* Test cleanup is best effort. */ }
        try { File.Delete(_databasePath); } catch { /* Test cleanup is best effort. */ }
    }

    private Guid CreateSeason()
    {
        var seasonId = Guid.NewGuid();
        InsertWork(seasonId, "parent", null, null);
        return seasonId;
    }

    private (Guid WorkId, Guid? AssetId) CreateEpisode(Guid seasonId, int number, bool withAsset = false)
    {
        var workId = Guid.NewGuid();
        InsertWork(workId, "child", seasonId, number);
        if (!withAsset)
        {
            return (workId, null);
        }

        return (workId, AddAsset(workId));
    }

    private Guid CreateStandaloneWithAsset()
    {
        var workId = Guid.NewGuid();
        InsertWork(workId, "standalone", null, null, mediaType: "Movies");
        return AddAsset(workId);
    }

    private void InsertWork(Guid workId, string kind, Guid? parentId, int? ordinal, string mediaType = "TV")
    {
        var collectionId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@collectionId, @createdAt);",
            new { collectionId, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("""
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only)
            VALUES (@workId, @collectionId, @mediaType, @kind, @parentId, @ordinal, @ordinal, 0);
            """, new { workId, collectionId, mediaType, kind, parentId, ordinal });
    }

    private Guid AddAsset(Guid workId)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute("INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);", new { editionId, workId });
        conn.Execute("""
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status)
            VALUES (@assetId, @editionId, @contentHash, @path, 'Normal');
            """, new { assetId, editionId, contentHash = $"hash_{assetId:N}", path = $"/library/{assetId:N}.mkv" });
        return assetId;
    }
}
