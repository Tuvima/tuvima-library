using System.Text.Json;
using Dapper;
using MediaEngine.Api.Services.Review;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using MediaEngine.Storage.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// "Move to TV" on a "Found as a TV title" review item, against a real temp SQLite data store:
/// the film is re-filed as Season 0 Episode 1 of the TMDB show in the TV library, identity re-runs as
/// TV, the review item resolves, and the file on disk is never touched.
/// </summary>
public sealed class ReviewMoveToTvServiceTests : IDisposable
{
    private const string MoviesLibraryId = "33333333-3333-4333-8333-333333333333";
    private const string TvLibraryId = "22222222-2222-4222-8222-222222222222";
    private const string ShowName = "Dr. Horrible's Sing-Along Blog";

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"tuvima-move-to-tv-{Guid.NewGuid():N}");
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly ConfigurationDirectoryLoader _configuration;
    private readonly ReviewQueueRepository _reviews;
    private readonly MetadataClaimRepository _claims;
    private readonly CanonicalValueRepository _canonicals;
    private readonly RecordingPipeline _pipeline = new();
    private readonly RecordingPublisher _publisher = new();
    private readonly MediaEntityChainFactory _factory;
    private readonly ReviewMoveToTvService _service;
    private readonly string _filePath;

    public ReviewMoveToTvServiceTests()
    {
        DapperConfiguration.Configure();
        Directory.CreateDirectory(_root);
        _dbPath = Path.Combine(_root, "library.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();

        _configuration = new ConfigurationDirectoryLoader(Path.Combine(_root, "config"));
        _configuration.SaveLibraries(Libraries(
            new LibraryFolderConfig { Id = MoviesLibraryId, Name = "Movies", Category = "Movies", MediaTypes = ["Movies"] },
            new LibraryFolderConfig { Id = TvLibraryId, Name = "TV Shows", Category = "TV", MediaTypes = ["TV"] }));

        var resolver = new HierarchyResolver(new WorkRepository(_db));
        _factory = new MediaEntityChainFactory(_db, resolver);
        _reviews = new ReviewQueueRepository(_db);
        _claims = new MetadataClaimRepository(_db);
        _canonicals = new CanonicalValueRepository(_db);
        _service = new ReviewMoveToTvService(
            _reviews,
            new MediaAssetRepository(_db),
            new TvReassignmentRepository(_db, resolver),
            _canonicals,
            _configuration,
            _pipeline,
            new SystemActivityRepository(_db),
            _publisher,
            NullLogger<ReviewMoveToTvService>.Instance);

        // A real file on disk, so the test can prove nothing touches it.
        var folder = Path.Combine(_root, "movies", "Dr. Horrible's Sing-Along Blog (2008)");
        Directory.CreateDirectory(folder);
        _filePath = Path.Combine(folder, "Dr. Horrible's Sing-Along Blog (2008) - x264 DTS.mkv");
        File.WriteAllBytes(_filePath, [1, 2, 3, 4, 5, 6, 7, 8]);
        File.SetLastWriteTimeUtc(_filePath, new DateTime(2009, 1, 2, 3, 4, 5, DateTimeKind.Utc));
    }

    public void Dispose()
    {
        try { _configuration.Dispose(); } catch { /* temp cleanup is best effort */ }
        try { _db.Dispose(); } catch { /* temp cleanup is best effort */ }
        try { Directory.Delete(_root, recursive: true); } catch { /* temp cleanup is best effort */ }
    }

    [Fact]
    public async Task MoveToTv_RefilesTheFilmAsSeasonZeroEpisodeOne_AndLeavesTheFileUntouched()
    {
        var (assetId, movieWorkId, reviewId) = await SeedPendingSuggestionAsync();
        var bytesBefore = File.ReadAllBytes(_filePath);
        var writtenBefore = File.GetLastWriteTimeUtc(_filePath);

        var result = await _service.MoveAsync(reviewId, "user");

        Assert.Equal(ReviewMoveToTvOutcome.Moved, result.Outcome);
        var response = Assert.IsType<ReviewMoveToTvResponse>(result.Response);
        Assert.True(response.moved);
        Assert.Equal(ShowName, response.show_name);
        Assert.Equal(TvLibraryId, response.tv_library_id);

        using var conn = _db.CreateConnection();

        // Library reassigned; the file path is unchanged.
        var asset = conn.QuerySingle<(string? Library, string Path)>(
            "SELECT library_id AS Library, file_path_root AS Path FROM media_assets WHERE id = @id;", new { id = assetId });
        Assert.Equal((TvLibraryId, _filePath), asset);

        // Show -> Season 0 -> Episode 1 holds the asset's edition.
        var episode = conn.QuerySingle<(string MediaType, int? Ordinal, string SeasonKey, string ShowKey)>(
            """
            SELECT ep.media_type AS MediaType, ep.ordinal AS Ordinal, season.parent_key AS SeasonKey, show.parent_key AS ShowKey
            FROM media_assets a
            JOIN editions e ON e.id = a.edition_id
            JOIN works ep ON ep.id = e.work_id
            JOIN works season ON season.id = ep.parent_work_id
            JOIN works show ON show.id = season.parent_work_id
            WHERE a.id = @id;
            """, new { id = assetId });
        Assert.Equal(("TV", 1, "dr. horrible's sing-along blog|s00", "dr. horrible's sing-along blog"), episode);

        // The standalone movie Work is gone; nothing is left in Movies.
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id;", new { id = movieWorkId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'Movies';"));

        // User-locked decision on the asset.
        var assetClaims = await _claims.GetByEntityAsync(assetId);
        foreach (var (key, value) in new[]
                 {
                     ("media_type", "TV"), ("show_name", ShowName), ("series", ShowName),
                     ("season_number", "0"), ("episode_number", "1"), ("tmdb_id", "5739"),
                 })
        {
            var claim = Assert.Single(assetClaims, c => c.ClaimKey == key);
            Assert.Equal(value, claim.ClaimValue);
            Assert.True(claim.IsUserLocked);
        }

        // The file's own title is kept.
        var assetValues = (await _canonicals.GetByEntityAsync(assetId)).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal(ShowName, assetValues["title"]);
        Assert.Equal("TV", assetValues["media_type"]);
        Assert.Equal("0", assetValues["season_number"]);
        Assert.Equal("1", assetValues["episode_number"]);

        // The show Work carries the TMDB identity.
        var showWorkId = conn.ExecuteScalar<Guid>(
            "SELECT show.id FROM works show WHERE show.media_type = 'TV' AND show.parent_key = 'dr. horrible''s sing-along blog';");
        var showValues = (await _canonicals.GetByEntityAsync(showWorkId)).ToDictionary(v => v.Key, v => v.Value);
        Assert.Equal(ShowName, showValues["title"]);
        Assert.Equal("5739", showValues["tmdb_id"]);
        Assert.Equal("2008", showValues["year"]);

        // The review item is resolved and the decision announced.
        var item = await _reviews.GetByIdAsync(reviewId);
        Assert.Equal(ReviewStatus.Resolved, item!.Status);
        Assert.Equal("user", item.ResolvedBy);
        Assert.Contains(SignalREventsReviewResolved, _publisher.EventNames);

        // Identity re-runs as TV.
        var request = Assert.Single(_pipeline.Requests);
        Assert.Equal(assetId, request.EntityId);
        Assert.Equal(MediaType.TV, request.MediaType);
        Assert.Equal(ShowName, request.Hints["show_name"]);
        Assert.Equal("0", request.Hints["season_number"]);
        Assert.Equal("1", request.Hints["episode_number"]);

        // History records the decision.
        Assert.Equal(1, conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM system_activity WHERE entity_id = @id AND changes_json LIKE '%moved_to_tv%';", new { id = assetId }));

        // The source file is exactly as it was.
        Assert.Equal(bytesBefore, File.ReadAllBytes(_filePath));
        Assert.Equal(writtenBefore, File.GetLastWriteTimeUtc(_filePath));
    }

    [Fact]
    public async Task FailureAfterTheHierarchyIsCreated_RollsEverythingBack_AndTheReviewItemStaysPending()
    {
        var (assetId, movieWorkId, reviewId) = await SeedPendingSuggestionAsync();

        // The bridge id is written after the hierarchy, edition move, library change and claims,
        // so this fails the move late in the transaction.
        using (var conn = _db.CreateConnection())
        {
            conn.Execute("""
                CREATE TRIGGER fail_bridge_ids BEFORE INSERT ON bridge_ids
                BEGIN SELECT RAISE(ABORT, 'injected failure'); END;
                """);
        }

        await Assert.ThrowsAnyAsync<Exception>(() => _service.MoveAsync(reviewId, "user"));

        AssertStillAMovie(assetId, movieWorkId);
        using var verify = _db.CreateConnection();
        Assert.Equal(0, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(movieWorkId, verify.ExecuteScalar<Guid>(
            "SELECT e.work_id FROM media_assets a JOIN editions e ON e.id = a.edition_id WHERE a.id = @id;", new { id = assetId }));
        Assert.Equal(0, verify.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM metadata_claims WHERE entity_id = @id AND is_user_locked = 1;", new { id = assetId }));
        Assert.Equal(ReviewStatus.Pending, (await _reviews.GetByIdAsync(reviewId))!.Status);
        Assert.Empty(_pipeline.Requests);
        Assert.DoesNotContain(SignalREventsReviewResolved, _publisher.EventNames);

        // The retry after the cause is fixed succeeds cleanly.
        verify.Execute("DROP TRIGGER fail_bridge_ids;");
        var retry = await _service.MoveAsync(reviewId, "user");
        Assert.Equal(ReviewMoveToTvOutcome.Moved, retry.Outcome);
        Assert.Equal(3, verify.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
    }

    [Fact]
    public async Task WrongTrigger_IsRefused_AndNothingChanges()
    {
        var (assetId, movieWorkId, _) = await SeedPendingSuggestionAsync();
        var other = await InsertReviewAsync(assetId, ReviewTrigger.RetailMatchFailed, candidatesJson: null);

        var result = await _service.MoveAsync(other, "user");

        Assert.Equal(ReviewMoveToTvOutcome.WrongTrigger, result.Outcome);
        AssertStillAMovie(assetId, movieWorkId);
        Assert.Equal(ReviewStatus.Pending, (await _reviews.GetByIdAsync(other))!.Status);
        Assert.Empty(_pipeline.Requests);
    }

    [Fact]
    public async Task ResolvedItem_IsRefused()
    {
        var (assetId, movieWorkId, reviewId) = await SeedPendingSuggestionAsync();
        await _reviews.UpdateStatusAsync(reviewId, ReviewStatus.Dismissed, "user");

        var result = await _service.MoveAsync(reviewId, "user");

        Assert.Equal(ReviewMoveToTvOutcome.NotPending, result.Outcome);
        AssertStillAMovie(assetId, movieWorkId);
    }

    [Fact]
    public async Task UnknownReviewItem_IsNotFound()
    {
        var result = await _service.MoveAsync(Guid.NewGuid(), "user");

        Assert.Equal(ReviewMoveToTvOutcome.ReviewItemNotFound, result.Outcome);
    }

    [Fact]
    public async Task ItemWithoutAReadableSuggestion_IsRefused()
    {
        var (assetId, movieWorkId, _) = await SeedPendingSuggestionAsync();
        var broken = await InsertReviewAsync(assetId, ReviewTrigger.MovieMatchedAsTv, candidatesJson: "not json", resolveExisting: true);

        var result = await _service.MoveAsync(broken, "user");

        Assert.Equal(ReviewMoveToTvOutcome.SuggestionMissing, result.Outcome);
        AssertStillAMovie(assetId, movieWorkId);
    }

    [Fact]
    public async Task NoTvLibrary_IsRefused_AndNothingChanges()
    {
        var (assetId, movieWorkId, reviewId) = await SeedPendingSuggestionAsync();
        _configuration.SaveLibraries(Libraries(
            new LibraryFolderConfig { Id = MoviesLibraryId, Name = "Movies", Category = "Movies", MediaTypes = ["Movies"] }));

        var result = await _service.MoveAsync(reviewId, "user");

        Assert.Equal(ReviewMoveToTvOutcome.NoTvLibrary, result.Outcome);
        AssertStillAMovie(assetId, movieWorkId);
        Assert.Equal(ReviewStatus.Pending, (await _reviews.GetByIdAsync(reviewId))!.Status);
        Assert.Empty(_pipeline.Requests);
    }

    private LibrariesConfiguration Libraries(params LibraryFolderConfig[] libraries) => new()
    {
        StorageLocations = [new ServerStorageLocationConfig { Id = "media", Label = "Media storage", Path = _root, AllowWrite = true }],
        Libraries = [.. libraries],
    };

    private const string SignalREventsReviewResolved = SignalREvents.ReviewItemResolved;

    private void AssertStillAMovie(Guid assetId, Guid movieWorkId)
    {
        using var conn = _db.CreateConnection();
        Assert.Equal(1, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE id = @id AND media_type = 'Movies';", new { id = movieWorkId }));
        Assert.Equal(0, conn.ExecuteScalar<int>("SELECT COUNT(*) FROM works WHERE media_type = 'TV';"));
        Assert.Equal(MoviesLibraryId, conn.ExecuteScalar<string>("SELECT library_id FROM media_assets WHERE id = @id;", new { id = assetId }));
    }

    private async Task<(Guid AssetId, Guid MovieWorkId, Guid ReviewId)> SeedPendingSuggestionAsync()
    {
        var assetId = Guid.NewGuid();
        var editionId = await _factory.EnsureEntityChainAsync(
            MediaType.Movies, new Dictionary<string, string> { ["title"] = ShowName }, _filePath);

        using (var conn = _db.CreateConnection())
        {
            conn.Execute(
                "INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, library_id) VALUES (@a, @e, @h, @p, 'Normal', @lib);",
                new { a = assetId, e = editionId, h = assetId.ToString("N"), p = _filePath, lib = MoviesLibraryId });
        }

        await _canonicals.UpsertBatchAsync(
        [
            new CanonicalValue { EntityId = assetId, Key = "title", Value = ShowName, LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = assetId, Key = "year", Value = "2008", LastScoredAt = DateTimeOffset.UtcNow },
            new CanonicalValue { EntityId = assetId, Key = "media_type", Value = "Movies", LastScoredAt = DateTimeOffset.UtcNow },
        ]);

        Guid movieWorkId;
        using (var conn = _db.CreateConnection())
        {
            movieWorkId = conn.ExecuteScalar<Guid>("SELECT work_id FROM editions WHERE id = @e;", new { e = editionId });
        }

        var suggestion = new MovieTvSuggestionDto
        {
            TmdbTvId = "5739",
            Name = ShowName,
            FirstAirYear = 2008,
            Type = "Miniseries",
            Seasons = 1,
            Episodes = 3,
            PosterUrl = "https://image.tmdb.org/t/p/w500/dr.jpg",
            Overview = "A low-rent super-villain.",
        };
        var reviewId = await InsertReviewAsync(assetId, ReviewTrigger.MovieMatchedAsTv, JsonSerializer.Serialize(new[] { suggestion }));
        return (assetId, movieWorkId, reviewId);
    }

    private async Task<Guid> InsertReviewAsync(Guid assetId, string trigger, string? candidatesJson, bool resolveExisting = false)
    {
        if (resolveExisting)
        {
            foreach (var existing in await _reviews.GetByEntityAsync(assetId))
            {
                await _reviews.UpdateStatusAsync(existing.Id, ReviewStatus.Resolved, "test");
            }
        }

        var entry = new ReviewQueueEntry
        {
            Id = Guid.NewGuid(),
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = trigger,
            Detail = "test",
            CandidatesJson = candidatesJson,
            ReviewReadyAt = DateTimeOffset.UtcNow,
        };
        await _reviews.InsertAsync(entry);
        return entry.Id;
    }

    private sealed class RecordingPipeline : IHydrationPipelineService
    {
        public List<HarvestRequest> Requests { get; } = [];

        public ValueTask<Guid> EnqueueAsync(HarvestRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return ValueTask.FromResult(Guid.NewGuid());
        }

        public Task<HydrationResult> RunSynchronousAsync(HarvestRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingPublisher : IEventPublisher
    {
        public List<string> EventNames { get; } = [];

        public Task PublishAsync<TPayload>(string eventName, TPayload payload, CancellationToken ct = default)
            where TPayload : notnull
        {
            EventNames.Add(eventName);
            return Task.CompletedTask;
        }
    }
}
