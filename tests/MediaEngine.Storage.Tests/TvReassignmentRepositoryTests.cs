using Dapper;
using MediaEngine.Domain;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage.Services;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// Move to TV re-files a Movies asset as Season 0, Episode 1 of a TV show. Real temp SQLite, the same
/// chain factory the pipeline uses; the media asset row (and so the file path) must be untouched.
/// </summary>
public sealed class TvReassignmentRepositoryTests : IDisposable
{
    private const string FilePath = @"C:\Temp\real_import\movies\Dr. Horrible's Sing-Along Blog (2008)\Dr. Horrible's Sing-Along Blog (2008) - x264 DTS.mkv";
    private const string ShowName = "Dr. Horrible's Sing-Along Blog";

    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly MediaEntityChainFactory _factory;
    private readonly TvReassignmentRepository _repository;

    public TvReassignmentRepositoryTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_tvreassign_test_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        var resolver = new HierarchyResolver(new WorkRepository(_db));
        _factory = new MediaEntityChainFactory(_db, resolver);
        _repository = new TvReassignmentRepository(_db, resolver);
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { /* temp database cleanup is best effort */ }
        try { File.Delete(_dbPath); } catch { /* temp database cleanup is best effort */ }
    }

    [Fact]
    public async Task Movie_IsRefiledAsSeasonZeroEpisodeOneOfTheShow_AndOldMovieWorkIsRemoved()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        SeedWorkLevelData(movieWorkId);

        var result = await _repository.ReassignToTvShowSpecialAsync(assetId, ShowName, "Dr. Horrible's Sing-Along Blog");

        // Show -> Season 0 -> Episode 1.
        using var conn = _db.CreateConnection();
        var episode = conn.QuerySingle<(string MediaType, string WorkKind, int? Ordinal, Guid? Parent)>(
            "SELECT media_type AS MediaType, work_kind AS WorkKind, ordinal AS Ordinal, parent_work_id AS Parent FROM works WHERE id = @id;",
            new { id = result.EpisodeWorkId });
        Assert.Equal(("TV", "child", 1, result.SeasonWorkId), episode);

        var season = conn.QuerySingle<(string WorkKind, int? Ordinal, Guid? Parent, string? Key)>(
            "SELECT work_kind AS WorkKind, ordinal AS Ordinal, parent_work_id AS Parent, parent_key AS Key FROM works WHERE id = @id;",
            new { id = result.SeasonWorkId });
        Assert.Equal(("parent", TvSpecialPlacement.SeasonNumber, result.ShowWorkId), (season.WorkKind, season.Ordinal, season.Parent));
        Assert.EndsWith("|s00", season.Key);

        var show = conn.QuerySingle<(string MediaType, string WorkKind, string? Key)>(
            "SELECT media_type AS MediaType, work_kind AS WorkKind, parent_key AS Key FROM works WHERE id = @id;",
            new { id = result.ShowWorkId });
        Assert.Equal(("TV", "parent", "dr. horrible's sing-along blog"), show);

        // The edition (and so the asset) moved under the episode; the asset row is unchanged.
        Assert.Equal(result.EpisodeWorkId, conn.ExecuteScalar<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = result.EditionId }));
        var asset = conn.QuerySingle<(string Path, string Status, Guid Edition)>(
            "SELECT file_path_root AS Path, status AS Status, edition_id AS Edition FROM media_assets WHERE id = @id;",
            new { id = assetId });
        Assert.Equal((FilePath, "Normal", result.EditionId), asset);

        // The standalone movie Work and everything that described only it are gone.
        Assert.True(result.PreviousWorkRemoved);
        Assert.Equal(movieWorkId, result.PreviousWorkId);
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWorkId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'Movies';"));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM canonical_values WHERE entity_id = @id;", new { id = movieWorkId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id;", new { id = movieWorkId }));
    }

    [Fact]
    public async Task MovingAMovieToTv_KeepsItsCuratedCollectionAndPointsItAtTheEpisode()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        var collectionId = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
                VALUES (@collectionId, 'Musicals', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@itemId, @collectionId, @movieWorkId, 1);
                """, new { collectionId, itemId = Guid.NewGuid(), movieWorkId });
        }

        var result = await _repository.ReassignToTvShowSpecialAsync(assetId, ShowName, null);

        Assert.True(result.PreviousWorkRemoved);
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM collections WHERE id = @collectionId;", new { collectionId }));
        Assert.Equal(
            [result.EpisodeWorkId],
            conn.Query<Guid>("SELECT work_id FROM collection_items WHERE collection_id = @collectionId;", new { collectionId }).ToList());
    }

    [Fact]
    public async Task MovingAMovieToTv_WhenTheCollectionAlreadyHoldsTheEpisode_LeavesOneMembership()
    {
        var (first, _) = await IngestMovieAsync(@"C:\Temp\real_import\movies\a.mkv");
        var (second, secondMovieWorkId) = await IngestMovieAsync(@"C:\Temp\real_import\movies\b.mkv");
        var firstMove = await _repository.ReassignToTvShowSpecialAsync(first, ShowName, null);
        var collectionId = Guid.NewGuid();
        using (var seed = _db.CreateConnection())
        {
            seed.Execute("""
                INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
                VALUES (@collectionId, 'Specials', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
                INSERT INTO collection_items (id, collection_id, work_id, sort_order)
                VALUES (@itemA, @collectionId, @episodeWorkId, 1), (@itemB, @collectionId, @secondMovieWorkId, 2);
                """, new { collectionId, itemA = Guid.NewGuid(), itemB = Guid.NewGuid(), episodeWorkId = firstMove.EpisodeWorkId, secondMovieWorkId });
        }

        await _repository.ReassignToTvShowSpecialAsync(second, ShowName, null);

        using var conn = _db.CreateConnection();
        Assert.Equal(
            [firstMove.EpisodeWorkId],
            conn.Query<Guid>("SELECT work_id FROM collection_items WHERE collection_id = @collectionId;", new { collectionId }).ToList());
    }

    [Fact]
    public async Task Reassigning_IsIdempotent()
    {
        var (assetId, _) = await IngestMovieAsync();

        var first = await _repository.ReassignToTvShowSpecialAsync(assetId, ShowName, null);
        var second = await _repository.ReassignToTvShowSpecialAsync(assetId, ShowName, null);

        Assert.Equal(first.EpisodeWorkId, second.EpisodeWorkId);
        Assert.False(second.PreviousWorkRemoved);
        using var conn = _db.CreateConnection();
        Assert.Equal(3, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works;"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM editions;"));
    }

    [Fact]
    public async Task SecondFileForTheSameShow_ReusesTheShowAndSeason()
    {
        var (first, _) = await IngestMovieAsync(@"C:\Temp\real_import\movies\a.mkv");
        var (second, _) = await IngestMovieAsync(@"C:\Temp\real_import\movies\b.mkv");

        var one = await _repository.ReassignToTvShowSpecialAsync(first, ShowName, null);
        var two = await _repository.ReassignToTvShowSpecialAsync(second, ShowName, null);

        Assert.Equal(one.ShowWorkId, two.ShowWorkId);
        Assert.Equal(one.SeasonWorkId, two.SeasonWorkId);
        Assert.Equal(one.EpisodeWorkId, two.EpisodeWorkId); // same special; two editions
        using var conn = _db.CreateConnection();
        Assert.Equal(2, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM editions WHERE work_id = @id;", new { id = one.EpisodeWorkId }));
    }

    [Fact]
    public async Task MoveToTv_FailingAfterTheHierarchyIsCreated_RollsBackWithNoOrphanWorks_AndReviewStaysPending()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);

        var request = new TvMoveRequest(
            assetId, ShowName, null, 0, [1], "tv-library", reviewId, "user",
            _ => throw new IOException("injected failure after the hierarchy was created"));

        await Assert.ThrowsAsync<IOException>(() => _repository.MoveToTvShowAsync(request));

        using var conn = _db.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works;"));
        Assert.Equal(movieWorkId, conn.ExecuteScalar<Guid>(
            "SELECT work_id FROM editions WHERE id = (SELECT edition_id FROM media_assets WHERE id = @id);", new { id = assetId }));
        Assert.Null(conn.ExecuteScalar<string?>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = assetId }));
        Assert.Equal(ReviewStatus.Pending, conn.ExecuteScalar<string>(
            "SELECT status FROM review_queue WHERE id = @id;", new { id = reviewId }));
    }

    [Fact]
    public async Task MoveToTv_WhenTheReviewItemIsNoLongerPending_RollsBackTheWholeMove()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE review_queue SET status = 'Dismissed' WHERE id = @id;", new { id = reviewId });
        }

        var request = new TvMoveRequest(
            assetId, ShowName, null, 0, [1], "tv-library", reviewId, "user",
            _ => new TvMoveDecision([], [], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.MoveToTvShowAsync(request));

        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWorkId }));
    }

    [Fact]
    public async Task MoveToTv_CommitsHierarchyLibraryDecisionAndReviewResolutionTogether()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);
        var request = new TvMoveRequest(
            assetId, ShowName, null, 0, [1], "tv-library", reviewId, "user",
            placement => new TvMoveDecision(
                [],
                [new MediaEngine.Domain.Entities.CanonicalValue
                {
                    EntityId = placement.ShowWorkId, Key = "title", Value = ShowName, LastScoredAt = DateTimeOffset.UtcNow,
                }],
                []));

        var result = await _repository.MoveToTvShowAsync(request);

        Assert.True(result.PreviousWorkRemoved);
        using var conn = _db.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWorkId }));
        Assert.Equal("tv-library", conn.ExecuteScalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = assetId }));
        Assert.Equal(ShowName, conn.ExecuteScalar<string>(
            "SELECT value FROM canonical_values WHERE entity_id = @id AND key = 'title';", new { id = result.ShowWorkId }));
        Assert.Equal(ReviewStatus.Resolved, conn.ExecuteScalar<string>(
            "SELECT status FROM review_queue WHERE id = @id;", new { id = reviewId }));
        Assert.Equal(3, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM works WHERE media_type = 'TV' AND ownership = 'Owned' AND is_catalog_only = 0;"));
    }

    [Fact]
    public async Task MoveToTv_CoveringThreeEpisodes_WritesManualCoverageAndOwnsEveryEpisodeInOneTransaction()
    {
        var (assetId, _) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);
        var request = new TvMoveRequest(
            assetId, ShowName, null, 1, [1, 2, 3], "tv-library", reviewId, "user",
            _ => new TvMoveDecision([], [], []));

        var result = await _repository.MoveToTvShowAsync(request);

        Assert.Equal(3, result.CoveredEpisodeWorkIds.Count);
        Assert.Equal(result.EpisodeWorkId, result.CoveredEpisodeWorkIds[0]);
        using var conn = _db.CreateConnection();
        var coverage = conn.Query<(Guid Work, int Position, string Source, int Ordinal)>(
            """
            SELECT c.work_id AS Work, c.position AS Position, c.source AS Source, w.ordinal AS Ordinal
            FROM media_asset_coverage c JOIN works w ON w.id = c.work_id
            WHERE c.asset_id = @id ORDER BY c.position;
            """, new { id = assetId }).ToList();
        Assert.Equal(
            [(result.CoveredEpisodeWorkIds[0], 1, "manual", 1), (result.CoveredEpisodeWorkIds[1], 2, "manual", 2), (result.CoveredEpisodeWorkIds[2], 3, "manual", 3)],
            coverage);
        Assert.Equal(result.EpisodeWorkId, conn.ExecuteScalar<Guid>(
            "SELECT work_id FROM editions WHERE id = @id;", new { id = result.EditionId }));
        Assert.Equal(3, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM works WHERE parent_work_id = @season AND ownership = 'Owned' AND is_catalog_only = 0;",
            new { season = result.SeasonWorkId }));
        Assert.Equal(ReviewStatus.Resolved, conn.ExecuteScalar<string>(
            "SELECT status FROM review_queue WHERE id = @id;", new { id = reviewId }));
    }

    [Fact]
    public async Task MoveToTv_CoveringOneEpisode_LeavesNoCoverageRows()
    {
        var (assetId, _) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);
        var request = new TvMoveRequest(
            assetId, ShowName, null, 1, [1], "tv-library", reviewId, "user",
            _ => new TvMoveDecision([], [], []));

        var result = await _repository.MoveToTvShowAsync(request);

        Assert.Single(result.CoveredEpisodeWorkIds);
        using var conn = _db.CreateConnection();
        Assert.Equal(0, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM media_asset_coverage WHERE asset_id = @id;", new { id = assetId }));
    }

    [Fact]
    public async Task MoveToTv_CoveringThreeEpisodes_WhenTheReviewItemIsNoLongerPending_LeavesNoCoverageOrEpisodeWorks()
    {
        var (assetId, movieWorkId) = await IngestMovieAsync();
        var reviewId = await InsertReviewAsync(assetId);
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("UPDATE review_queue SET status = 'Dismissed' WHERE id = @id;", new { id = reviewId });
        }

        var request = new TvMoveRequest(
            assetId, ShowName, null, 1, [1, 2, 3], "tv-library", reviewId, "user",
            _ => new TvMoveDecision([], [], []));

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.MoveToTvShowAsync(request));

        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM media_asset_coverage;"));
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(1, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWorkId }));
    }

    [Fact]
    public async Task NonMovieAsset_IsRefused()
    {
        var assetId = Guid.NewGuid();
        var editionId = await _factory.EnsureEntityChainAsync(MediaType.Books, new Dictionary<string, string> { ["title"] = "A Book" });
        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a, @e, @h, @p, 'Normal');",
                new { a = assetId, e = editionId, h = assetId.ToString("N"), p = @"C:\books\a.epub" });
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignToTvShowSpecialAsync(assetId, ShowName, null));
    }

    [Fact]
    public async Task UnknownAsset_IsRefused()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _repository.ReassignToTvShowSpecialAsync(Guid.NewGuid(), ShowName, null));
    }

    private async Task<(Guid AssetId, Guid WorkId)> IngestMovieAsync(string path = FilePath)
    {
        var assetId = Guid.NewGuid();
        var editionId = await _factory.EnsureEntityChainAsync(
            MediaType.Movies, new Dictionary<string, string> { ["title"] = "Dr. Horrible's Sing-Along Blog" }, path);

        using var conn = _db.CreateConnection();
        conn.Execute(
            "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status) VALUES (@a, @e, @h, @p, 'Normal');",
            new { a = assetId, e = editionId, h = assetId.ToString("N"), p = path });
        var workId = conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = @e;", new { e = editionId });
        return (assetId, workId);
    }

    private async Task<Guid> InsertReviewAsync(Guid assetId)
    {
        var entry = new MediaEngine.Domain.Entities.ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = ReviewTrigger.MovieMatchedAsTv,
            Detail = "test",
            ReviewReadyAt = DateTimeOffset.UtcNow,
        };
        await new ReviewQueueRepository(_db).InsertAsync(entry);
        return entry.Id;
    }

    private void SeedWorkLevelData(Guid workId)
    {
        using var conn = _db.CreateConnection();
        conn.Execute(
            """
            INSERT OR IGNORE INTO metadata_providers (id, name, version, is_enabled)
            VALUES (@provider, 'local_processor', '1', 1);
            INSERT INTO metadata_claims (id, entity_id, provider_id, claim_key, claim_value, confidence, claimed_at)
            VALUES (@claim, @work, @provider, 'year', '2008', 1, '2026-01-01');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'year', '2008', '2026-01-01');
            """,
            new { provider = WellKnownProviders.LocalProcessor, claim = Guid.NewGuid(), work = workId });
    }
}
