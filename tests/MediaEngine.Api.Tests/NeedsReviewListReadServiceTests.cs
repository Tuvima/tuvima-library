using System.Text.Json;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Review;
using MediaEngine.Domain;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Constants;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Models;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using ProviderConfiguration = MediaEngine.Domain.Configuration.ProviderConfiguration;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Needs Review is one list (universal editor v3, WP7 and D9): review items and unmatched files read together. These tests use a real
/// temporary SQLite data store, and check the two promises the product makes: the library page's "N files not identified" opens a list that
/// holds exactly N, and the Settings notifier count is the number of rows the list holds.
/// </summary>
public sealed class NeedsReviewListReadServiceTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly UnmatchedAssetRepository _unmatched;
    private readonly ReviewQueueRepository _reviews;
    private readonly NeedsReviewListReadService _service;

    public NeedsReviewListReadServiceTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_needs_review_list_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _unmatched = new UnmatchedAssetRepository(_db);
        _reviews = new ReviewQueueRepository(_db);
        var loader = new ListLoader(
            new LibraryFolderConfig { Id = "movies", Name = "Movies" },
            new LibraryFolderConfig { Id = "tv", Name = "TV" });
        _service = new NeedsReviewListReadService(
            new ReviewQueueReadService(_db), _unmatched, loader, new VaultLibraryRepository(_db), new NeedsReviewRowContextReadService(_db));
    }

    public void Dispose()
    {
        try { _db.Dispose(); } catch { }
        try { File.Delete(_dbPath); } catch { }
    }

    [Fact]
    public async Task TheListHoldsReviewItemsAndUnmatchedFilesTogether_ByGroupThenNewestFirst_EachWithItsKind()
    {
        var oldFile = SeedFile("movies", "old.mkv", tried: At(1));
        var unsure = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(2));
        var newFile = SeedFile("tv", "new.mkv", tried: At(3));
        var failedRetail = SeedReview("movies", ReviewTrigger.RetailMatchFailed, created: At(4));

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        Assert.Equal(4, list.Total);
        // The choice first, then everything that found no match, newest first.
        Assert.Equal([$"review:{unsure:D}", $"review:{failedRetail:D}", $"file:{newFile:D}", $"file:{oldFile:D}"], list.Items.Select(row => row.Key));
        Assert.Equal(3, list.NotMatchedTotal);
        Assert.Equal(1, list.NotSureTotal);
        Assert.All(list.Items.Where(row => row.Unmatched is not null), row => Assert.Equal(NeedsReviewKinds.NotMatched, row.Kind));
        Assert.Equal(NeedsReviewKinds.NotSure, list.Items.Single(row => row.Key == $"review:{unsure:D}").Kind);
        Assert.Equal(NeedsReviewKinds.NotMatched, list.Items.Single(row => row.Key == $"review:{failedRetail:D}").Kind);
        Assert.Equal("TV", list.Items.Single(row => row.Key == $"file:{newFile:D}").LibraryName);
    }

    [Fact]
    public async Task TheKindFilterNarrowsTheRows_ButTheChipTotalsStayWhole()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedFile("movies", "b.mkv", tried: At(2));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(3));

        var notMatched = await _service.ListAsync("not_matched", null, true, 0, 100);
        var notSure = await _service.ListAsync("not_sure", null, true, 0, 100);

        Assert.Equal(2, notMatched.Total);
        Assert.All(notMatched.Items, row => Assert.Equal(NeedsReviewKinds.NotMatched, row.Kind));
        Assert.Equal(1, notSure.Total);
        Assert.Equal((2, 1), (notSure.NotMatchedTotal, notSure.NotSureTotal));
    }

    [Fact]
    public async Task ALibrarysNotIdentifiedCountOpensAListOfExactlyThatManyRows()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedFile("movies", $"m{i}.mkv", tried: At(i));
        }
        for (var i = 0; i < 2; i++)
        {
            SeedFile("tv", $"t{i}.mkv", tried: At(10 + i));
        }
        var kept = SeedFile("movies", "kept.mkv", tried: At(20));
        await _unmatched.KeepAsIsAsync(kept, DateTimeOffset.UtcNow);
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(30));

        var counts = await _unmatched.CountByLibraryAsync();
        var list = await _service.ListAsync("not_matched", "movies", true, 0, 100);

        Assert.Equal(5, counts["movies"]);
        Assert.Equal(counts["movies"], list.Total);
        Assert.Equal(counts["movies"], list.Items.Count(row => row.Unmatched is not null));
        Assert.DoesNotContain(list.Items, row => row.Unmatched?.AssetId == kept);
        Assert.All(list.Items, row => Assert.Equal("movies", row.LibraryId, ignoreCase: true));
    }

    [Fact]
    public async Task TheNotifierCountIsTheNumberOfRowsTheListHolds()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedFile("tv", "b.mkv", tried: At(2));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(3));
        SeedReview("tv", ReviewTrigger.RetailMatchFailed, created: At(4));
        var kept = SeedFile("tv", "kept.mkv", tried: At(5));
        await _unmatched.KeepAsIsAsync(kept, DateTimeOffset.UtcNow);

        var count = await _service.CountAsync(includeFiles: true);
        var list = await _service.ListAsync(null, null, true, 0, 100);

        Assert.Equal(list.Total, count.PendingCount);
        Assert.Equal(list.NotMatchedTotal, count.NotMatchedCount);
        Assert.Equal(list.NotSureTotal, count.NotSureCount);
        Assert.Equal(4, count.PendingCount);
    }

    [Fact]
    public async Task ACallerWhoMayNotSeeTheFilesGetsOnlyTheReviewItems_InTheListAndInTheCount()
    {
        SeedFile("movies", "a.mkv", tried: At(1));
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(2));

        var list = await _service.ListAsync(null, null, includeFiles: false, 0, 100);
        var count = await _service.CountAsync(includeFiles: false);

        Assert.Single(list.Items);
        Assert.Null(list.Items[0].Unmatched);
        Assert.Equal(1, count.PendingCount);
    }

    [Fact]
    public async Task AVaultLibrarysReviewItems_AreNeverListedOrCounted()
    {
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(1));
        SeedReview("hidden-vault", ReviewTrigger.RetailMatchFailed, created: At(2));
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES('hidden-vault','none',$now,$now,$now);";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);
        var count = await _service.CountAsync(includeFiles: true);

        Assert.Single(list.Items);
        Assert.Equal("movies", list.Items[0].LibraryId, ignoreCase: true);
        Assert.Equal(list.Total, count.PendingCount);
    }

    [Fact]
    public async Task AVaultItemTheSourceQueryLetThrough_IsStillDroppedFromTheListAndTheCount()
    {
        var ok = new ReviewItemDto { Id = Guid.NewGuid(), Trigger = ReviewTrigger.MetadataConflict, LibraryId = "movies", CreatedAt = At(1) };
        var vaulted = new ReviewItemDto { Id = Guid.NewGuid(), Trigger = ReviewTrigger.RetailMatchFailed, LibraryId = "Hidden-Vault", CreatedAt = At(2) };
        using (var conn = _db.CreateConnection())
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "INSERT INTO vault_libraries(library_id, lookup_source, adults_confirmed_at, created_at, updated_at) VALUES('hidden-vault','none',$now,$now,$now);";
            cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        }

        var service = new NeedsReviewListReadService(
            new FixedReviews([ok, vaulted]), _unmatched, new ListLoader(new LibraryFolderConfig { Id = "movies", Name = "Movies" }), new VaultLibraryRepository(_db),
            new NeedsReviewRowContextReadService(_db));

        var list = await service.ListAsync(null, null, includeFiles: false, 0, 100);
        var count = await service.CountAsync(includeFiles: false);

        Assert.Equal([$"review:{ok.Id:D}"], list.Items.Select(row => row.Key));
        Assert.Equal(1, count.PendingCount);
        Assert.Equal(0, count.NotMatchedCount);
    }

    private sealed class FixedReviews(IReadOnlyList<ReviewItemDto> items) : MediaEngine.Application.Services.IReviewQueueReadService
    {
        public Task<IReadOnlyList<ReviewItemDto>> GetPendingAsync(int limit, CancellationToken ct = default) => Task.FromResult(items);
        public Task<IReadOnlyList<ReviewItemDto>> GetPendingForLibraryAsync(int limit, string libraryId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReviewItemDto>>(items.Where(i => string.Equals(i.LibraryId, libraryId, StringComparison.OrdinalIgnoreCase)).ToList());
        public Task<IReadOnlyDictionary<string, int>> GetPendingCountsByLibraryAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        public Task<ReviewItemDto?> GetByIdAsync(Guid id, CancellationToken ct = default) => Task.FromResult<ReviewItemDto?>(items.FirstOrDefault(i => i.Id == id));
        public Task<int> GetPendingCountAsync(CancellationToken ct = default) => Task.FromResult(items.Count);
        public Task<IReadOnlyList<MediaEngine.Application.ReadModels.ReviewReasonCount>> GetPendingReasonCountsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<MediaEngine.Application.ReadModels.ReviewReasonCount>>([]);
        public Task<IReadOnlyList<ReviewItemDto>> GetKeptAsync(int limit, string? libraryId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<ReviewItemDto>>([]);
        public Task<int> GetKeptCountAsync(string? libraryId, CancellationToken ct = default) => Task.FromResult(0);
    }

    [Fact]
    public async Task ALibraryThatIsNotListedHoldsNothing()
    {
        SeedFile("movies", "a.mkv", tried: At(1));

        var list = await _service.ListAsync(null, "no-such-library", true, 0, 100);

        Assert.Empty(list.Items);
        Assert.Equal(0, list.Total);
    }

    [Fact]
    public async Task PagingKeepsTheTotalAndTheOrder()
    {
        for (var i = 0; i < 5; i++)
        {
            SeedFile("movies", $"m{i}.mkv", tried: At(i));
        }

        var first = await _service.ListAsync(null, null, true, 0, 2);
        var second = await _service.ListAsync(null, null, true, 2, 2);

        Assert.Equal(5, first.Total);
        Assert.Equal(2, first.Items.Count);
        Assert.Equal(2, second.Items.Count);
        Assert.Empty(first.Items.Select(row => row.Key).Intersect(second.Items.Select(row => row.Key)));
    }

    [Fact]
    public async Task AReviewWhereWikidataFoundSeveralEntities_OffersTheProviderMatchAsItsBestGuess()
    {
        var id = SeedReview("movies", ReviewTrigger.MultipleQidMatches, created: At(1), title: "Arrival");

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.NotNull(row.BestGuess);
        Assert.Equal("keep_provider_match", row.BestGuess!.Action);
        Assert.Equal("Arrival", row.BestGuess.Name);
        Assert.Equal(NeedsReviewKinds.NotSure, row.Kind);
    }

    [Fact]
    public async Task AFilmThatLooksLikeATvSeries_OffersTheSeriesAsItsBestGuess()
    {
        var suggestion = JsonSerializer.Serialize(new[] { new MovieTvSuggestionDto { TmdbTvId = "42", Name = "Chernobyl", FirstAirYear = 2019, Seasons = 1, Episodes = 5 } });
        var id = SeedReview("movies", ReviewTrigger.MovieMatchedAsTv, created: At(1), title: "Chernobyl", candidatesJson: suggestion);

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.Equal("move_to_tv", row.BestGuess!.Action);
        Assert.Equal("Chernobyl", row.BestGuess.Name);
        Assert.Equal("2019", row.BestGuess.Year);
    }

    [Fact]
    public async Task AReviewWithNoSingleAnswerHasNoBestGuess_AndAnUnmatchedFileNeverDoes()
    {
        SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(1));
        SeedFile("movies", "a.mkv", tried: At(2));

        var list = await _service.ListAsync(null, null, true, 0, 10);

        Assert.All(list.Items, row => Assert.Null(row.BestGuess));
    }

    [Fact]
    public async Task ThreeEpisodesAnAmbiguousFilmAndAnUnreadableFile_GetTheirGroups_AndTheEpisodesShareOneFamily()
    {
        var (show, season) = SeedShow("Chernobyl");
        var ep1 = SeedEpisodeFile("tv", "s01e01.mkv", season, At(1));
        var ep2 = SeedEpisodeFile("tv", "s01e02.mkv", season, At(2));
        var ep3 = SeedEpisodeFile("tv", "s01e03.mkv", season, At(3));
        var film = SeedReview("movies", ReviewTrigger.RetailMatchAmbiguous, created: At(4));
        var unreadable = SeedReview("movies", ReviewTrigger.MediaInspectionFailed, created: At(5));

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        Assert.Equal(5, list.Total);
        Assert.Equal(
            [$"review:{film:D}", $"file:{ep3:D}", $"file:{ep2:D}", $"file:{ep1:D}", $"review:{unreadable:D}"],
            list.Items.Select(row => row.Key));
        Assert.Equal(
            [NeedsReviewGroups.Choose, NeedsReviewGroups.NoMatch, NeedsReviewGroups.NoMatch, NeedsReviewGroups.NoMatch, NeedsReviewGroups.FileProblem],
            list.Items.Select(row => row.Group));
        Assert.Equal(
            new Dictionary<string, int>
            {
                [NeedsReviewGroups.Ready] = 0,
                [NeedsReviewGroups.Choose] = 1,
                [NeedsReviewGroups.NoMatch] = 3,
                [NeedsReviewGroups.FileProblem] = 1,
            },
            list.GroupTotals);
        var episodes = list.Items.Where(row => row.Unmatched is not null).ToList();
        Assert.All(episodes, row =>
        {
            Assert.Equal(show.ToString("D"), row.FamilyKey);
            Assert.Equal("Chernobyl", row.FamilyTitle);
            Assert.Equal(show, row.FamilyWorkId);
        });
        Assert.All(list.Items.Where(row => row.Review is not null), row =>
        {
            Assert.Null(row.FamilyKey);
            Assert.Null(row.FamilyTitle);
            Assert.Null(row.FamilyWorkId);
        });
    }

    [Fact]
    public async Task AFamilysRowsStayTogether_EvenWhenAnotherItemWasTriedBetweenThem()
    {
        var (_, season) = SeedShow("Dark");
        var early = SeedEpisodeFile("tv", "s01e01.mkv", season, At(1));
        var late = SeedEpisodeFile("tv", "s01e02.mkv", season, At(5));
        var lone = SeedFile("movies", "lone.mkv", tried: At(3));

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        // The family sorts by its newest row, so it comes first, and its two rows are neighbours.
        Assert.Equal([$"file:{late:D}", $"file:{early:D}", $"file:{lone:D}"], list.Items.Select(row => row.Key));
    }

    [Fact]
    public async Task AnEpisodeFileShowsItsShowsPicture_AndTheFamilyCarriesThePictureToo_ButAFileWithNoPictureGetsNone()
    {
        var (show, season) = SeedShow("Chernobyl");
        SeedPreferredCover(show, localPath: "C:/data/art/show.jpg", imageUrl: null);
        var episode = SeedEpisodeFile("tv", "s01e01.mkv", season, At(1));
        var bare = SeedFile("movies", "bare.mkv", tried: At(2));
        var withCover = SeedFile("movies", "with-cover.mkv", tried: At(3));
        SeedPreferredCover(WorkOf(withCover), localPath: "C:/data/art/film.jpg", imageUrl: null);
        var remoteOnly = SeedFile("movies", "remote-only.mkv", tried: At(4));
        SeedPreferredCover(WorkOf(remoteOnly), localPath: null, imageUrl: "https://example.com/poster.jpg");

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        Assert.Equal($"/stream/{episode:D}/cover", Row(list, episode).CoverUrl);
        Assert.Equal($"/stream/entity/work/{show:D}/cover", Row(list, episode).FamilyCoverUrl);
        Assert.Null(Row(list, bare).CoverUrl);
        Assert.Equal($"/stream/{withCover:D}/cover", Row(list, withCover).CoverUrl);
        // The asset cover route serves only a saved copy, so a picture that exists only as a web link is not offered as the file's own.
        Assert.Null(Row(list, remoteOnly).CoverUrl);
    }

    [Fact]
    public async Task WikidataOnlyItems_AreNeitherListedNorCounted_ButAnItemWithSeveralWikidataMatchesStays()
    {
        SeedReview("movies", ReviewTrigger.MissingQid, created: At(1), title: "Arrival");
        SeedReview("movies", ReviewTrigger.WikidataBridgeFailed, created: At(2), title: "Dune");
        var several = SeedReview("movies", ReviewTrigger.MultipleQidMatches, created: At(3), title: "Heat");
        var conflict = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(4));

        var list = await _service.ListAsync(null, null, includeFiles: true, 0, 100);
        var count = await _service.CountAsync(includeFiles: true);

        Assert.Equal([$"review:{several:D}", $"review:{conflict:D}"], list.Items.Select(row => row.Key));
        Assert.Equal(2, list.Total);
        Assert.Equal(2, count.PendingCount);
        Assert.Equal(2, count.NotSureCount);
        // They stay stored and pending: only the read hides them.
        Assert.Equal(4, (await _reviews.GetPendingAsync(100)).Count);
    }

    [Fact]
    public async Task AWikidataOnlyItemAPersonKept_IsNotInTheKeptViewOrItsTotal()
    {
        var hidden = SeedReview("movies", ReviewTrigger.MissingQid, created: At(1), title: "Arrival");
        await _reviews.UpdateStatusAsync(hidden, ReviewStatus.Dismissed, ReviewStatus.KeptByUser);

        var kept = await _service.ListAsync(null, null, includeFiles: true, 0, 100, keptOnly: true);
        var normal = await _service.ListAsync(null, null, includeFiles: true, 0, 100);

        Assert.Empty(kept.Items);
        Assert.Equal(0, normal.KeptTotal);
    }

    [Fact]
    public async Task KeptItems_AppearOnlyWhenAskedFor_AreCounted_AndComeBackAfterPutBack()
    {
        var open = SeedFile("movies", "open.mkv", tried: At(1));
        var keptFile = SeedFile("movies", "kept.mkv", tried: At(2));
        await _unmatched.KeepAsIsAsync(keptFile, DateTimeOffset.UtcNow);
        var keptReview = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(3));
        await _reviews.UpdateStatusAsync(keptReview, ReviewStatus.Dismissed, ReviewStatus.KeptByUser);
        // Dismissals that are not a person's Keep as it is never appear in the kept view.
        var byEngine = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(4));
        await _reviews.UpdateStatusAsync(byEngine, ReviewStatus.Dismissed, "reconciliation");
        var skippedUniverse = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(5));
        await _reviews.UpdateStatusAsync(skippedUniverse, ReviewStatus.Dismissed, "user");

        var normal = await _service.ListAsync(null, null, includeFiles: true, 0, 100);
        var kept = await _service.ListAsync(null, null, includeFiles: true, 0, 100, keptOnly: true);
        var count = await _service.CountAsync(includeFiles: true);

        Assert.Equal([$"file:{open:D}"], normal.Items.Select(row => row.Key));
        Assert.All(normal.Items, row => Assert.False(row.Kept));
        Assert.Equal(2, normal.KeptTotal);
        Assert.Equal(1, count.PendingCount);
        Assert.Equal([$"review:{keptReview:D}", $"file:{keptFile:D}"], kept.Items.Select(row => row.Key));
        Assert.All(kept.Items, row => Assert.True(row.Kept));
        Assert.Equal(2, kept.Total);
        Assert.Equal(2, kept.KeptTotal);
        Assert.Null(Row(kept, keptFile).NextRetryAt);

        // Put back: a review item reopens, a file leaves the kept state without being looked up.
        Assert.False(await _reviews.ReopenKeptAsync(byEngine));
        Assert.False(await _reviews.ReopenKeptAsync(skippedUniverse));
        Assert.True(await _reviews.ReopenKeptAsync(keptReview));
        var retryAt = DateTimeOffset.UtcNow.AddDays(7);
        Assert.True(await _unmatched.UnkeepAsync(keptFile, retryAt));
        Assert.False(await _unmatched.UnkeepAsync(open, retryAt));

        var after = await _service.ListAsync(null, null, includeFiles: true, 0, 100);
        var keptAfter = await _service.ListAsync(null, null, includeFiles: true, 0, 100, keptOnly: true);

        Assert.Equal(3, after.Total);
        Assert.Contains(after.Items, row => row.Key == $"review:{keptReview:D}");
        Assert.NotNull(Row(after, keptFile).NextRetryAt);
        Assert.False(Row(after, keptFile).Unmatched!.KeptAsIs);
        Assert.Equal(0, after.KeptTotal);
        Assert.Empty(keptAfter.Items);
    }

    [Fact]
    public async Task ACallerWhoMayNotSeeFiles_NeverSeesKeptFiles_InTheViewOrTheTotal()
    {
        var keptFile = SeedFile("movies", "kept.mkv", tried: At(1));
        await _unmatched.KeepAsIsAsync(keptFile, DateTimeOffset.UtcNow);

        var kept = await _service.ListAsync(null, null, includeFiles: false, 0, 100, keptOnly: true);
        var normal = await _service.ListAsync(null, null, includeFiles: false, 0, 100);

        Assert.Empty(kept.Items);
        Assert.Equal(0, normal.KeptTotal);
    }

    [Fact]
    public async Task ALowConfidenceItemLinkedToAMatch_OffersToConfirmIt_AndIsReadyToConfirm()
    {
        var id = SeedReview("movies", ReviewTrigger.LowConfidence, created: At(1), title: "Arrival", confidence: 0.62, bridgeIdKey: "tmdb_id");

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.Equal("confirm_match", row.BestGuess!.Action);
        Assert.Equal("Arrival", row.BestGuess.Name);
        Assert.Equal(NeedsReviewGroups.Ready, row.Group);
    }

    [Fact]
    public async Task ALowConfidenceItemWithNoMatchBehindIt_OffersNothing_AndIsInNoMatchFound()
    {
        var unlinked = SeedReview("movies", ReviewTrigger.LowConfidence, created: At(1), title: "scan0042", confidence: 0.62);
        var noScore = SeedReview("movies", ReviewTrigger.LowConfidence, created: At(2), title: "Arrival", confidence: 0, bridgeIdKey: "tmdb_id");

        var list = await _service.ListAsync(null, null, true, 0, 10);

        Assert.All(new[] { unlinked, noScore }, id =>
        {
            var row = list.Items.Single(r => r.Key == $"review:{id:D}");
            Assert.Null(row.BestGuess);
            Assert.Equal(NeedsReviewGroups.NoMatch, row.Group);
        });
    }

    [Fact]
    public async Task AFilmThatMayBeATvShow_IsAChoice_EvenWithASuggestion()
    {
        var suggestion = JsonSerializer.Serialize(new[] { new MovieTvSuggestionDto { TmdbTvId = "42", Name = "Chernobyl", FirstAirYear = 2019 } });
        var id = SeedReview("movies", ReviewTrigger.MovieMatchedAsTv, created: At(1), title: "Chernobyl", candidatesJson: suggestion);

        var row = (await _service.ListAsync(null, null, true, 0, 10)).Items.Single(r => r.Key == $"review:{id:D}");

        Assert.Equal(NeedsReviewGroups.Choose, row.Group);
        Assert.Equal("move_to_tv", row.BestGuess!.Action);
    }

    [Fact]
    public async Task AFilesRetryTimeComesWithItsRow_ButAReviewItemHasNone()
    {
        var file = SeedFile("movies", "a.mkv", tried: At(1));
        var review = SeedReview("movies", ReviewTrigger.MetadataConflict, created: At(2));

        var list = await _service.ListAsync(null, null, true, 0, 10);

        Assert.Equal(At(1).AddDays(7), Row(list, file).NextRetryAt);
        Assert.Null(list.Items.Single(r => r.Key == $"review:{review:D}").NextRetryAt);
    }

    private static NeedsReviewRowDto Row(NeedsReviewListDto list, Guid assetId) =>
        list.Items.Single(row => row.Key == $"file:{assetId:D}");

    private Guid WorkOf(Guid assetId)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT e.work_id FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE ma.id = $id;";
        cmd.Parameters.Add("$id", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        return GuidSql.FromDb(cmd.ExecuteScalar()!);
    }

    private static DateTimeOffset At(int minutes) => new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.Zero).AddMinutes(minutes);

    private Guid SeedFile(string libraryId, string fileName, DateTimeOffset tried)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedAsset(workId, editionId, assetId, libraryId, $"C:/media/{libraryId}/{fileName}");
        _unmatched.MarkUnmatchedAsync(assetId, libraryId, UnmatchedReasons.NoMatch, tried, tried.AddDays(7)).GetAwaiter().GetResult();
        return assetId;
    }

    private Guid SeedReview(
        string libraryId, string trigger, DateTimeOffset created, string? title = null, string? candidatesJson = null,
        double? confidence = null, string? bridgeIdKey = null)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedAsset(workId, editionId, assetId, libraryId, $"C:/media/{libraryId}/{assetId:N}.mkv");
        if (title is not null)
        {
            SeedCanonical(assetId, "title", title);
        }

        if (bridgeIdKey is not null)
        {
            SeedCanonical(assetId, bridgeIdKey, "12345");
        }

        var id = Guid.NewGuid();
        _reviews.InsertAsync(new ReviewQueueEntry
        {
            Id = id,
            EntityId = assetId,
            EntityType = "MediaAsset",
            Trigger = trigger,
            Status = ReviewStatus.Pending,
            ConfidenceScore = confidence,
            Detail = "needs a look",
            CandidatesJson = candidatesJson,
            CreatedAt = created,
            ReviewReadyAt = created,
            AutomationCompletedAt = created,
        }).GetAwaiter().GetResult();
        return id;
    }

    private void SeedCanonical(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES ($id, $key, $value, $now);";
        cmd.Parameters.Add("$id", SqliteType.Blob).Value = GuidSql.ToBlob(entityId);
        cmd.Parameters.AddWithValue("$key", key);
        cmd.Parameters.AddWithValue("$value", value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
    }

    private void SeedWork(Guid id, string mediaType, string workKind, Guid? parent)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO works (id, media_type, work_kind, parent_work_id) VALUES ($id, $mediaType, $kind, $parent);";
        cmd.Parameters.Add("$id", SqliteType.Blob).Value = GuidSql.ToBlob(id);
        cmd.Parameters.AddWithValue("$mediaType", mediaType);
        cmd.Parameters.AddWithValue("$kind", workKind);
        cmd.Parameters.Add("$parent", SqliteType.Blob).Value = parent is null ? DBNull.Value : GuidSql.ToBlob(parent.Value);
        cmd.ExecuteNonQuery();
    }

    // A show and one of its seasons, as the hierarchy resolver builds them.
    private (Guid Show, Guid Season) SeedShow(string title)
    {
        var (show, season) = (Guid.NewGuid(), Guid.NewGuid());
        SeedWork(show, "TV", "parent", null);
        SeedWork(season, "TV", "parent", show);
        SeedCanonical(show, "title", title);
        return (show, season);
    }

    // An unmatched episode file whose Work is a child of the season.
    private Guid SeedEpisodeFile(string libraryId, string fileName, Guid season, DateTimeOffset tried)
    {
        var (workId, editionId, assetId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        SeedWork(workId, "TV", "child", season);
        SeedEditionAndAsset(workId, editionId, assetId, libraryId, $"C:/media/{libraryId}/{fileName}");
        _unmatched.MarkUnmatchedAsync(assetId, libraryId, UnmatchedReasons.NoMatch, tried, tried.AddDays(7)).GetAwaiter().GetResult();
        return assetId;
    }

    private void SeedPreferredCover(Guid workId, string? localPath, string? imageUrl)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, image_url, local_image_path, is_preferred)
            VALUES ($id, $entity, 'Work', 'CoverArt', $url, $path, 1);
            """;
        cmd.Parameters.Add("$id", SqliteType.Blob).Value = GuidSql.ToBlob(Guid.NewGuid());
        cmd.Parameters.Add("$entity", SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.AddWithValue("$url", (object?)imageUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$path", (object?)localPath ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private void SeedAsset(Guid workId, Guid editionId, Guid assetId, string libraryId, string path)
    {
        SeedWork(workId, "Movies", "standalone", null);
        SeedEditionAndAsset(workId, editionId, assetId, libraryId, path);
    }

    private void SeedEditionAndAsset(Guid workId, Guid editionId, Guid assetId, string libraryId, string path)
    {
        using var conn = _db.CreateConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO editions (id, work_id) VALUES ($editionId, $workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, library_id) VALUES ($assetId, $editionId, $hash, $path, $library);
            """;
        cmd.Parameters.Add("$workId", SqliteType.Blob).Value = GuidSql.ToBlob(workId);
        cmd.Parameters.Add("$editionId", SqliteType.Blob).Value = GuidSql.ToBlob(editionId);
        cmd.Parameters.Add("$assetId", SqliteType.Blob).Value = GuidSql.ToBlob(assetId);
        cmd.Parameters.AddWithValue("$hash", $"nrl-{assetId:N}");
        cmd.Parameters.AddWithValue("$path", path);
        cmd.Parameters.AddWithValue("$library", libraryId);
        cmd.ExecuteNonQuery();
    }

    private sealed class ListLoader : IConfigurationLoader
    {
        public ListLoader(params LibraryFolderConfig[] libraries) =>
            Libraries = new LibrariesConfiguration { Libraries = [.. libraries] };

        public LibrariesConfiguration Libraries { get; }

        public event EventHandler<ConfigurationFileChangedEventArgs>? ConfigurationChanged;

        public void RaiseChanged(string relativePath, bool applied = true) =>
            ConfigurationChanged?.Invoke(this, new ConfigurationFileChangedEventArgs(relativePath, relativePath, applied, null));

        public LibrariesConfiguration LoadLibraries() => Libraries;

        public string ConfigDirectoryPath => string.Empty;
        public CoreConfiguration LoadCore() => new();
        public void SaveCore(CoreConfiguration config) { }
        public ScoringSettings LoadScoring() => new();
        public void SaveScoring(ScoringSettings settings) { }
        public MaintenanceSettings LoadMaintenance() => new();
        public void SaveMaintenance(MaintenanceSettings settings) { }
        public HydrationSettings LoadHydration() => new();
        public void SaveHydration(HydrationSettings settings) { }
        public PipelineConfiguration LoadPipelines() => new();
        public void SavePipelines(PipelineConfiguration config) { }
        public DisambiguationSettings LoadDisambiguation() => new();
        public void SaveDisambiguation(DisambiguationSettings settings) { }
        public MediaTypeConfiguration LoadMediaTypes() => new();
        public void SaveMediaTypes(MediaTypeConfiguration config) { }
        public TranscodingSettings LoadTranscoding() => new();
        public void SaveTranscoding(TranscodingSettings settings) { }
        public FieldPriorityConfiguration LoadFieldPriorities() => new();
        public void SaveFieldPriorities(FieldPriorityConfiguration config) { }
        public ProviderConfiguration? LoadProvider(string name) => null;
        public void DeleteProvider(string name) { }
        public void SaveProvider(ProviderConfiguration config) { }
        public IReadOnlyList<ProviderConfiguration> LoadAllProviders() => [];
        public T? LoadConfig<T>(string subdirectory, string name) where T : class => null;
        public void SaveConfig<T>(string subdirectory, string name, T config) where T : class { }
        public T? LoadAi<T>() where T : class => default;
        public void SaveAi<T>(T settings) where T : class { }
        public PaletteConfiguration LoadPalette() => new();
        public void SavePalette(PaletteConfiguration palette) { }
    }
}
