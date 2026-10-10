using Dapper;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
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

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [third.WorkId, host.WorkId, second.WorkId]);

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

        await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second.WorkId]);
        var again = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [second.WorkId, host.WorkId]);

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
        await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second.WorkId]);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId]);

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

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, stranger.WorkId]);

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

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), works);

        Assert.Equal(FileCoverageOutcome.Invalid, result.Outcome);
        Assert.Empty(await _coverage.ListByAssetAsync(host.AssetId!.Value));
    }

    [Fact]
    public async Task Replace_RejectsAListWithoutTheHost_AndDuplicates()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);

        var withoutHost = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [second.WorkId]);
        var duplicates = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second.WorkId, second.WorkId]);

        Assert.Equal(FileCoverageOutcome.Invalid, withoutHost.Outcome);
        Assert.Equal(FileCoverageOutcome.Invalid, duplicates.Outcome);
    }

    [Fact]
    public async Task Replace_RefusesAnEpisodeThatAlreadyHasItsOwnFile()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var owned = CreateEpisode(season, 2, withAsset: true);

        var result = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, owned.WorkId]);

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
        await _repository.ReplaceAsync(third.AssetId!.Value, Guid.NewGuid(), [third.WorkId, second.WorkId]);

        var result = await _repository.ReplaceAsync(first.AssetId!.Value, Guid.NewGuid(), [first.WorkId, second.WorkId]);

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
                VALUES (@reviewId, @assetId, 'MediaAsset', 'RetailMatchAmbiguous', 'Pending');
                """, new { reviewId, assetId = host.AssetId });
        }

        await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second.WorkId]);

        using var check = _database.CreateConnection();
        Assert.Equal("Resolved", check.ExecuteScalar<string>("SELECT status FROM review_queue WHERE id=@reviewId;", new { reviewId }));
    }

    [Fact]
    public async Task Replace_LeavesOtherPendingReviewsAlone()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        var reviewId = Guid.NewGuid();
        using (var conn = _database.CreateConnection())
        {
            conn.Execute("""
                INSERT INTO review_queue (id, entity_id, entity_type, trigger, status)
                VALUES (@reviewId, @assetId, 'MediaAsset', 'WritebackFailed', 'Pending');
                """, new { reviewId, assetId = host.AssetId });
        }

        await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second.WorkId]);

        using var check = _database.CreateConnection();
        Assert.Equal("Pending", check.ExecuteScalar<string>("SELECT status FROM review_queue WHERE id=@reviewId;", new { reviewId }));
    }

    [Fact]
    public async Task Replace_WithTheSameOperationId_ReturnsTheSavedResultOnce_AndRefusesADifferentList()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateEpisode(season, 2);
        var third = CreateEpisode(season, 3);
        var operation = Guid.NewGuid();

        await _repository.ReplaceAsync(host.AssetId!.Value, operation, [host.WorkId, second.WorkId]);
        var replay = await _repository.ReplaceAsync(host.AssetId!.Value, operation, [second.WorkId, host.WorkId]);
        var reused = await _repository.ReplaceAsync(host.AssetId!.Value, operation, [host.WorkId, third.WorkId]);

        Assert.Equal(FileCoverageOutcome.Ok, replay.Outcome);
        Assert.Equal(FileCoverageOutcome.Conflict, reused.Outcome);
        Assert.Equal([host.WorkId, second.WorkId], (await _coverage.ListByAssetAsync(host.AssetId!.Value)).Select(row => row.WorkId));
    }

    [Fact]
    public async Task Get_ListsCatalogEpisodesSoThePickerCanChooseThem()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var catalog = CreateCatalogEpisode(season, 2);

        var view = (await _repository.GetAsync(host.AssetId!.Value)).View!;

        Assert.Equal([host.WorkId, catalog], view.Episodes.Select(episode => episode.WorkId));
        Assert.False(view.Episodes[1].IsCovered);
    }

    [Fact]
    public async Task Replace_PromotesNewlyCoveredCatalogEpisodes_AndDemotesUntickedOnes()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateCatalogEpisode(season, 2);
        var third = CreateCatalogEpisode(season, 3);

        var saved = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second, third]);

        Assert.Equal(FileCoverageOutcome.Ok, saved.Outcome);
        Assert.Equal(("child", "Owned", 0), WorkState(second));
        Assert.Equal(("child", "Owned", 0), WorkState(third));

        var trimmed = await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId, second]);

        Assert.Equal(FileCoverageOutcome.Ok, trimmed.Outcome);
        Assert.Equal(("child", "Owned", 0), WorkState(second));
        Assert.Equal(("catalog", "Unowned", 1), WorkState(third));

        await _repository.ReplaceAsync(host.AssetId!.Value, Guid.NewGuid(), [host.WorkId]);

        Assert.Equal(("catalog", "Unowned", 1), WorkState(second));
        Assert.Equal(("child", "Owned", 0), WorkState(host.WorkId));
    }

    [Fact]
    public async Task ClearingFilenameCoverage_ReturnsCoveredEpisodesToTheCatalogue()
    {
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateCatalogEpisode(season, 2);
        await _coverage.ReplaceForAssetAsync(host.AssetId!.Value,
        [
            new MediaAssetCoverage(host.AssetId!.Value, host.WorkId, 1, null, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(host.AssetId!.Value, second, 2, null, null, MediaAssetCoverage.SourceFilename),
        ]);
        Assert.Equal(("child", "Owned", 0), WorkState(second));

        await _coverage.ReplaceForAssetAsync(host.AssetId!.Value, []);

        Assert.Equal(("catalog", "Unowned", 1), WorkState(second));
        Assert.Equal(("child", "Owned", 0), WorkState(host.WorkId));
    }

    [Fact]
    public async Task CombinedFileLifecycle_FilenameToPickerToDelete()
    {
        // Filename: an unbroken pair is a range; a typo and a gap are not linked.
        var pair = EpisodeRangeParser.Parse("Show S01E01E02");
        Assert.True(pair!.IsRange);
        Assert.Equal((1, 2), (pair.FirstEpisode, pair.LastEpisode));
        Assert.True(EpisodeRangeParser.Parse("Show S01E01E200")!.IsUnresolvedRange);
        Assert.True(EpisodeRangeParser.Parse("Show S01E01E03")!.IsUnresolvedRange);

        // Ingest links the pair; counts treat both as owned from one file, plus one normal file.
        var season = CreateSeason();
        var host = CreateEpisode(season, 1, withAsset: true);
        var second = CreateCatalogEpisode(season, 2);
        var normal = CreateEpisode(season, 3, withAsset: true);
        var assetId = host.AssetId!.Value;
        await _coverage.ReplaceForAssetAsync(assetId,
        [
            new MediaAssetCoverage(assetId, host.WorkId, 1, null, null, MediaAssetCoverage.SourceFilename),
            new MediaAssetCoverage(assetId, second, 2, null, null, MediaAssetCoverage.SourceFilename),
        ]);
        using (var conn = _database.CreateConnection())
        {
            Assert.Equal(3, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT work_id) FROM work_owned_assets;"));
            Assert.Equal(2, conn.ExecuteScalar<long>("SELECT COUNT(DISTINCT asset_id) FROM work_owned_assets;"));
        }

        // Chip lookup: each episode of the file finds the other through one batched query.
        var siblings = await _coverage.ListByWorksAsync([host.WorkId, second, normal.WorkId]);
        Assert.Equal(2, siblings.Count(row => row.AssetId == assetId));
        Assert.DoesNotContain(siblings, row => row.WorkId == normal.WorkId && row.AssetId != normal.AssetId);

        // Recorded episodes, not the filename, decide the organiser's range.
        var numbers = await _coverage.ListCoveredEpisodeNumbersAsync(assetId);
        Assert.Equal([1, 2], numbers);
        Assert.True(EpisodeRangeParser.TryGetUnbrokenRun(numbers, out var first, out var last));
        Assert.Equal((1, 2), (first, last));
        Assert.False(EpisodeRangeParser.TryGetUnbrokenRun([1, 3], out _, out _));
        Assert.False(EpisodeRangeParser.TryGetUnbrokenRun([2], out _, out _));

        // Editor fix: saving the same list again changes nothing.
        var again = await _repository.ReplaceAsync(assetId, Guid.NewGuid(), [host.WorkId, second]);
        Assert.Equal(FileCoverageOutcome.Ok, again.Outcome);
        Assert.Equal(2, (await _coverage.ListByAssetAsync(assetId)).Count);

        // Delete: removing the file takes all its coverage with it.
        using (var conn = _database.CreateConnection())
        {
            conn.Execute("DELETE FROM media_assets WHERE id = @assetId;", new { assetId });
            Assert.Equal(0, conn.ExecuteScalar<long>("SELECT COUNT(*) FROM media_asset_coverage;"));
        }
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

    private Guid CreateCatalogEpisode(Guid seasonId, int number)
    {
        var workId = Guid.NewGuid();
        InsertWork(workId, "catalog", seasonId, number, catalog: true);
        return workId;
    }

    private (string WorkKind, string Ownership, int IsCatalogOnly) WorkState(Guid workId)
    {
        using var conn = _database.CreateConnection();
        return conn.QuerySingle<(string WorkKind, string Ownership, int IsCatalogOnly)>(
            "SELECT work_kind AS WorkKind, ownership AS Ownership, is_catalog_only AS IsCatalogOnly FROM works WHERE id = @workId;",
            new { workId });
    }

    private Guid CreateStandaloneWithAsset()
    {
        var workId = Guid.NewGuid();
        InsertWork(workId, "standalone", null, null, mediaType: "Movies");
        return AddAsset(workId);
    }

    private void InsertWork(Guid workId, string kind, Guid? parentId, int? ordinal, string mediaType = "TV", bool catalog = false)
    {
        var collectionId = Guid.NewGuid();
        using var conn = _database.CreateConnection();
        conn.Execute("INSERT INTO collections (id, created_at) VALUES (@collectionId, @createdAt);",
            new { collectionId, createdAt = DateTimeOffset.UtcNow.ToString("O") });
        conn.Execute("""
            INSERT INTO works (id, collection_id, media_type, work_kind, parent_work_id, ordinal, ordinal_sort, is_catalog_only, ownership)
            VALUES (@workId, @collectionId, @mediaType, @kind, @parentId, @ordinal, @ordinal, @isCatalogOnly, @ownership);
            """, new
        {
            workId, collectionId, mediaType, kind, parentId, ordinal,
            isCatalogOnly = catalog ? 1 : 0,
            ownership = catalog ? "Unowned" : "Owned",
        });
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
