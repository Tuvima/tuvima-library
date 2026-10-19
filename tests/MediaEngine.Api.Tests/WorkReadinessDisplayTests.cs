using Dapper;
using MediaEngine.Api.Services.Display;
using MediaEngine.Contracts.Display;
using MediaEngine.Domain.Enums;
using MediaEngine.Storage;

namespace MediaEngine.Api.Tests;

/// <summary>
/// Home, the hero, the lanes and Watch show a Work only once it is Ready: matched, placed in its show and with its
/// artwork. Everything else stays off the page until it is.
/// </summary>
public sealed class WorkReadinessDisplayTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DatabaseConnection _db;
    private readonly WorkReadinessRepository _readiness;
    private readonly DisplayComposerService _composer;

    public WorkReadinessDisplayTests()
    {
        DapperConfiguration.Configure();
        _dbPath = Path.Combine(Path.GetTempPath(), $"tuvima_ready_display_{Guid.NewGuid():N}.db");
        _db = new DatabaseConnection(_dbPath);
        _db.InitializeSchema();
        _db.RunStartupChecks();
        _readiness = new WorkReadinessRepository(_db);
        var cards = new DisplayCardBuilder();
        _composer = new DisplayComposerService(
            new RealRowsProjection(new DisplayWorkProjectionReader(_db)),
            cards,
            new DisplayShelfBuilder(cards));
    }

    public void Dispose()
    {
        _db.Dispose();
        foreach (var path in new[] { _dbPath, _dbPath + "-wal", _dbPath + "-shm" })
        {
            try { File.Delete(path); } catch (IOException) { /* temp database cleanup is best effort */ }
        }
    }

    [Fact]
    public async Task Pages_NeverShowMoviesThatAreQueuedMatchingInReviewUnmatchedOrFailed_ButShowAMatchedOneWithArtwork()
    {
        var hidden = new List<string>();
        foreach (var state in new[]
                 {
                     IdentityJobState.Queued, IdentityJobState.RetailSearching, IdentityJobState.RetailMatchedNeedsReview,
                     IdentityJobState.RetailNoMatch, IdentityJobState.QidNeedsReview, IdentityJobState.Failed,
                 })
        {
            var title = $"Movie In {state}";
            var (_, assetId) = await SeedMovieAsync(title);
            await SetJobAsync(assetId, state);
            hidden.Add(title);
        }

        var (_, reviewAsset) = await SeedMovieAsync("Movie With A Pending Review");
        await SetJobAsync(reviewAsset, IdentityJobState.RetailMatched);
        await AddReviewAsync(reviewAsset);
        hidden.Add("Movie With A Pending Review");

        var (_, readyAsset) = await SeedMovieAsync("Movie That Is Ready");
        await SetJobAsync(readyAsset, IdentityJobState.RetailMatched);

        await _readiness.RefreshAllAsync(Array.Empty<string>());

        foreach (var page in await BuildPagesAsync())
        {
            Assert.Empty(TitlesOn(page).Intersect(hidden));
        }

        Assert.Contains("Movie That Is Ready", TitlesOn(await _composer.BuildHomeAsync()));
        Assert.Contains("Movie That Is Ready", TitlesOn(await _composer.BuildBrowseAsync("watch", null, null, null, 0, 100)));
        Assert.Contains("Movie That Is Ready", TitlesOn(await _composer.BuildBrowseAsync("watch", "Movies", null, null, 0, 100)));
    }

    [Fact]
    public async Task Pages_ShowNothing_WhileNoWorkIsReady()
    {
        var (_, assetId) = await SeedMovieAsync("Still Matching");
        await SetJobAsync(assetId, IdentityJobState.RetailSearching);
        await _readiness.RefreshAllAsync(Array.Empty<string>());

        foreach (var page in await BuildPagesAsync())
        {
            Assert.Empty(TitlesOn(page));
            Assert.Null(page.Hero);
            Assert.Empty(page.Spotlights);
        }
    }

    [Fact]
    public async Task TwentyEpisodesOfThreeShows_AppearOnlyShowByShow_AsEachShowIsMatched()
    {
        var shows = new Dictionary<string, List<Guid>>
        {
            ["Show Alpha"] = await SeedShowAsync("Show Alpha", 7),
            ["Show Beta"] = await SeedShowAsync("Show Beta", 7),
            ["Show Gamma"] = await SeedShowAsync("Show Gamma", 6),
        };
        Assert.Equal(20, shows.Values.Sum(episodes => episodes.Count));

        // Nothing is matched yet: Home and Watch show none of the 20 episodes, and not a single show either.
        await _readiness.RefreshAllAsync(Array.Empty<string>());
        foreach (var page in await BuildPagesAsync())
        {
            Assert.Empty(TitlesOn(page));
        }

        // Show Alpha's episodes are matched: the show appears once, the others stay away.
        foreach (var assetId in shows["Show Alpha"])
        {
            await SetJobAsync(assetId, IdentityJobState.RetailMatched);
        }

        await _readiness.RefreshAllAsync(Array.Empty<string>());
        foreach (var page in (await BuildPagesAsync()).Take(3))
        {
            var titles = TitlesOn(page);
            Assert.Contains(titles, title => title.StartsWith("Show Alpha", StringComparison.Ordinal));
            Assert.DoesNotContain(titles, title => title.StartsWith("Show Beta", StringComparison.Ordinal));
            Assert.DoesNotContain(titles, title => title.StartsWith("Show Gamma", StringComparison.Ordinal));
            Assert.All(page.Shelves, shelf => Assert.True(shelf.Items.Count(card => card.Title == "Show Alpha") <= 1));
            Assert.True(page.Catalog.Count(card => card.Title == "Show Alpha") <= 1);
        }

        Assert.Contains("Show Alpha", TitlesOn(await _composer.BuildHomeAsync()));
        Assert.Contains("Show Alpha", TitlesOn(await _composer.BuildBrowseAsync("watch", null, null, null, 0, 100)));
    }

    [Fact]
    public async Task AnEpisodeThatStandsAloneWithoutItsShow_IsNotShownEvenWhenMatchedWithArtwork()
    {
        var (_, assetId) = await SeedEpisodeAsync(showId: null, "Lone Episode", "lone");
        await SetJobAsync(assetId, IdentityJobState.RetailMatched);
        await _readiness.RefreshAllAsync(Array.Empty<string>());

        foreach (var page in await BuildPagesAsync())
        {
            Assert.DoesNotContain("Lone Episode", TitlesOn(page));
        }
    }

    // Home, the Watch lane, the Watch TV page and the Watch Movies page, in that order.
    private async Task<IReadOnlyList<DisplayPageDto>> BuildPagesAsync() =>
    [
        await _composer.BuildHomeAsync(),
        await _composer.BuildBrowseAsync("watch", null, null, null, 0, 100),
        await _composer.BuildBrowseAsync("watch", "TV", null, null, 0, 100),
        await _composer.BuildBrowseAsync("watch", "Movies", null, null, 0, 100),
    ];

    private static List<string> TitlesOn(DisplayPageDto page)
    {
        var titles = page.Shelves.SelectMany(shelf => shelf.Items).Select(card => card.Title)
            .Concat(page.Catalog.Select(card => card.Title))
            .ToList();
        if (page.Hero is not null)
        {
            titles.Add(page.Hero.Title);
        }

        titles.AddRange(page.Spotlights.Select(spotlight => spotlight.Title));
        return titles;
    }

    private async Task<(Guid WorkId, Guid AssetId)> SeedMovieAsync(string title)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await InsertWorkAsync(workId, "Movies", "standalone", parentId: null);
        await InsertAssetAsync(workId, assetId, title.Replace(' ', '_'));
        await CanonicalAsync(assetId, "title", title);
        await CanonicalAsync(assetId, "cover_state", "present");
        return (workId, assetId);
    }

    private async Task<List<Guid>> SeedShowAsync(string showName, int episodeCount)
    {
        var showId = Guid.NewGuid();
        await InsertWorkAsync(showId, "TV", "parent", parentId: null);
        await CanonicalAsync(showId, "title", showName);
        await CanonicalAsync(showId, "cover_state", "present");

        var assets = new List<Guid>();
        for (var number = 1; number <= episodeCount; number++)
        {
            var (_, assetId) = await SeedEpisodeAsync(showId, $"{showName} Episode {number}", $"{showName}_{number}", number);
            assets.Add(assetId);
        }

        return assets;
    }

    private async Task<(Guid WorkId, Guid AssetId)> SeedEpisodeAsync(Guid? showId, string title, string fileName, int number = 1)
    {
        var workId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        await InsertWorkAsync(workId, "TV", showId is null ? "standalone" : "child", showId);
        await InsertAssetAsync(workId, assetId, fileName.Replace(' ', '_'));
        await CanonicalAsync(assetId, "title", title);
        await CanonicalAsync(assetId, "episode_title", title);
        await CanonicalAsync(assetId, "season_number", "1");
        await CanonicalAsync(assetId, "episode_number", number.ToString());
        await CanonicalAsync(assetId, "cover_state", "present");
        await SetJobAsync(assetId, IdentityJobState.Queued);
        return (workId, assetId);
    }

    private async Task InsertWorkAsync(Guid workId, string mediaType, string workKind, Guid? parentId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO works (id, media_type, work_kind, parent_work_id, parent_key, curator_state)
            VALUES (@workId, @mediaType, @workKind, @parentId, @parentKey, 'accepted')
            """,
            new { workId, mediaType, workKind, parentId, parentKey = workKind == "parent" ? $"parent:{workId:N}" : null });
    }

    private async Task InsertAssetAsync(Guid workId, Guid assetId, string fileName)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            """
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'Video');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at, status)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP, 'Normal');
            """,
            new
            {
                editionId = Guid.NewGuid(),
                workId,
                assetId,
                hash = Guid.NewGuid().ToString("N"),
                path = $"/library/{fileName}.mkv",
            });
    }

    private async Task CanonicalAsync(Guid entityId, string key, string value)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT OR REPLACE INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@entityId, @key, @value, @at)",
            new { entityId, key, value, at = DateTimeOffset.UtcNow.ToString("O") });
    }

    private async Task SetJobAsync(Guid assetId, IdentityJobState state)
    {
        var at = DateTimeOffset.UtcNow.ToString("O");
        using var conn = _db.CreateConnection();
        var updated = await conn.ExecuteAsync(
            "UPDATE identity_jobs SET state = @state, updated_at = @at WHERE entity_id = @assetId",
            new { state = state.ToString(), at, assetId });
        if (updated == 0)
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO identity_jobs (id, entity_id, entity_type, media_type, state, created_at, updated_at)
                VALUES (@id, @assetId, 'MediaAsset', 'Movies', @state, @at, @at)
                """,
                new { id = Guid.NewGuid(), assetId, state = state.ToString(), at });
        }
    }

    private async Task AddReviewAsync(Guid assetId)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync(
            "INSERT INTO review_queue (id, entity_id, entity_type, trigger, status) VALUES (@id, @assetId, 'MediaAsset', 'LowConfidence', 'Pending')",
            new { id = Guid.NewGuid(), assetId });
    }

    /// <summary>The composer reads the real projection rows; there is no journey, favourite or collection data.</summary>
    private sealed class RealRowsProjection(DisplayWorkProjectionReader works) : IDisplayProjectionReadService
    {
        public Task<IReadOnlyList<DisplayWorkRow>> LoadWorksAsync(CancellationToken ct) => works.LoadAsync(ct);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadJourneyAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);

        public Task<IReadOnlyList<DisplayJourneyRow>> LoadStatesAsync(Guid? profileId, string? lane, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayJourneyRow>>([]);

        public Task<IReadOnlySet<Guid>> LoadFavoriteWorkIdsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlySet<Guid>>(new HashSet<Guid>());

        public Task<IReadOnlyList<DisplayHomeCollectionRow>> LoadHomeCollectionsAsync(Guid? profileId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DisplayHomeCollectionRow>>([]);
    }
}
