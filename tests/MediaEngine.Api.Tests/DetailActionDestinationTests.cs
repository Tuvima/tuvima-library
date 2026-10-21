using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// No button without a destination: every action the Engine puts on a detail page either carries a route to open or
/// is one the Dashboard handles itself, and a page with nothing playable has no main button.
/// </summary>
public sealed class DetailActionDestinationTests : IDisposable
{
    // The action keys the Dashboard's DetailPage handles itself. Keep this list identical to
    // DetailActionKeys.Handled in src/MediaEngine.Web/Components/Details/DetailActionKeys.cs; the Dashboard test
    // DetailActionKeysTests repeats the same list, so adding a key on one side only fails a test.
    private static readonly string[] DashboardHandledKeys =
    [
        "play-from-beginning", "read", "listen", "play-album", "shuffle", "shuffle-collection",
        "play-next", "add-queue", "add-playlist",
        "status-complete", "status-reset", "status-hide", "status-show", "status-history",
        "edit", "edit-media", "my-list",
        "reaction-dislike", "reaction-like", "reaction-love", "details", "identify", "change-thumbnail", "delete-from-library",
    ];

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_detail_actions_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly DetailComposerService _composer;

    public DetailActionDestinationTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        var collections = new CollectionRepository(_database);
        var browse = new CollectionBrowseReadService(collections, _database, NullLogger<CollectionBrowseReadService>.Instance);
        var catalog = new CollectionCatalogReadService(
            collections,
            new SeriesManifestRepository(_database),
            new PersonRepository(_database),
            new ArtworkPaletteService(),
            new CollectionMediaLookupReadService(_database),
            _database,
            NullLogger<CollectionCatalogReadService>.Instance);
        _composer = new DetailComposerService(
            _database,
            new LibraryItemRepository(_database),
            new PersonRepository(_database),
            new EntityAssetRepository(_database),
            new CanonicalValueArrayRepository(_database),
            new SeriesManifestRepository(_database),
            new PersonCreditReadService(
                new CanonicalValueArrayRepository(_database),
                new PersonRepository(_database),
                _database),
            collectionBrowse: browse,
            collectionCatalog: catalog,
            profiles: new ProfileRepository(_database));
    }

    [Fact]
    public async Task EveryActionOnEveryKindOfDetailPage_HasARouteOrIsOneTheDashboardHandles()
    {
        var pages = await ComposeEveryKindAsync();

        Assert.True(pages.Count >= 12, "Expected every kind of detail page to compose; got " + string.Join(", ", pages.Keys));
        // The pages carry real action lists (so the check below is not vacuous).
        var movie = pages["movie"];
        Assert.Equal("/watch/player/" + movie.Id, Assert.Single(movie.PrimaryActions).Route);
        Assert.Contains(movie.SecondaryActions, action => action.Key == "my-list");
        Assert.Contains(Flatten(movie.SecondaryActions), action => action.Key == "reaction-like");
        Assert.Contains(movie.OverflowActions, action => action.Key == "edit");
        Assert.DoesNotContain(pages.Values.SelectMany(ActionLists).SelectMany(list => Flatten(list.Actions)), action => action.Key == "copy-link");
        var offenders = new List<string>();
        foreach (var (name, page) in pages)
        {
            foreach (var (list, actions) in ActionLists(page))
            {
                foreach (var action in Flatten(actions))
                {
                    if (string.IsNullOrWhiteSpace(action.Route) && !DashboardHandledKeys.Contains(action.Key))
                    {
                        offenders.Add($"{name}.{list}: '{action.Key}' ({action.Label}) has no route and the Dashboard does not handle it");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public async Task EveryKindOfDetailPageThatCanPlayOrOpenSomething_ShowsAMainButton()
    {
        var pages = await ComposeEveryKindAsync();

        foreach (var name in new[] { "movie", "book", "audiobook", "tv episode", "tv show", "music album", "universe" })
        {
            var primary = Assert.Single(pages[name].PrimaryActions, action => action.Key != "shuffle");
            Assert.True(primary.IsPrimary, name);
        }
    }

    [Fact]
    public async Task APageWithNothingToPlayOrOpen_HasNoMainButton()
    {
        var pages = await ComposeEveryKindAsync();

        // A music track with no read or listen format, a show with no owned episode, a universe with no timeline
        // and a person page have nothing to open, so the generic "Open" button is gone with them.
        Assert.Empty(pages["generic work"].PrimaryActions);
        Assert.Empty(pages["tv show without episodes"].PrimaryActions);
        Assert.Empty(pages["universe without timeline"].PrimaryActions);
        Assert.Empty(pages["person"].PrimaryActions);
        foreach (var page in pages.Values)
        {
            Assert.DoesNotContain(page.PrimaryActions, action => action.Key == "open");
        }
    }

    [Fact]
    public async Task AUniverseDetailPage_OpensTheUniversePage_NotTheTimeline()
    {
        var page = await _composer.BuildAuthorizedAsync(
            DetailEntityType.Universe,
            SeedUniverse("Q4242"),
            DetailPresentationContext.Default,
            CancellationToken.None,
            null,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(CanManageMetadata: true));

        var open = Assert.Single(page!.PrimaryActions);
        Assert.Equal("Open Universe", open.Label);
        Assert.Equal("/universe/Q4242", open.Route);
    }

    [Fact]
    public async Task AWorkPage_LinksToItsUniverse_OnlyWhenTheRequestsResolverAnswersWithALink()
    {
        var work = SeedOwnedWork("Arrival", "Movie");
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@work, 'fictional_universe_qid', 'Q4242', CURRENT_TIMESTAMP);",
                new { work });
        }

        var asked = new List<string>();
        UniverseLinkResolver linking = (qid, _) =>
        {
            asked.Add(qid);
            return Task.FromResult<UniverseLinkViewModel?>(new UniverseLinkViewModel { Qid = qid, Name = "Dune" });
        };
        UniverseLinkResolver notEnough = (_, _) => Task.FromResult<UniverseLinkViewModel?>(null);

        async Task<DetailPageViewModel> ComposeAsync(UniverseLinkResolver? resolver) => (await _composer.BuildAuthorizedAsync(
            DetailEntityType.Movie,
            work,
            DetailPresentationContext.Default,
            CancellationToken.None,
            null,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(CanManageMetadata: true),
            universeLinks: resolver))!;

        var linked = await ComposeAsync(linking);
        Assert.Equal("Q4242", linked.UniverseLink!.Qid);
        Assert.Equal("Dune", linked.UniverseLink.Name);
        Assert.Equal(["Q4242"], asked);
        // The Universe is a quiet line under the title, never a tab of the item page.
        Assert.DoesNotContain(linked.Tabs, tab =>
            tab.Key.Contains("universe", StringComparison.OrdinalIgnoreCase)
            || tab.Label.Contains("universe", StringComparison.OrdinalIgnoreCase));
        // The resolver said the Library does not hold enough of the Universe, and a request with no resolver asks nothing.
        Assert.Null((await ComposeAsync(notEnough)).UniverseLink);
        Assert.Null((await ComposeAsync(null)).UniverseLink);
    }

    [Fact]
    public void TheDashboardKeyListInThisTestStaysInStepWithTheWebProject()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "MediaEngine.Web", "Components", "Details", "DetailActionKeys.cs"));

        foreach (var key in DashboardHandledKeys)
        {
            Assert.Contains($"\"{key}\"", source, StringComparison.Ordinal);
        }

        Assert.Equal(DashboardHandledKeys.Length, System.Text.RegularExpressions.Regex.Matches(source, "public const string \\w+ = \"").Count);
    }

    private async Task<Dictionary<string, DetailPageViewModel>> ComposeEveryKindAsync()
    {
        var profile = Profile.SeedProfileId;
        var pages = new Dictionary<string, DetailPageViewModel>();

        async Task AddAsync(string name, DetailEntityType type, Guid id, string? container = null)
        {
            var page = await _composer.BuildAuthorizedAsync(
                type,
                id,
                DetailPresentationContext.Default,
                CancellationToken.None,
                container,
                profile,
                new DetailActionAuthorizationContext(CanManageMetadata: true));
            Assert.True(page is not null, $"The {name} detail page did not compose.");
            pages[name] = page!;
        }

        await AddAsync("movie", DetailEntityType.Movie, SeedOwnedWork("Arrival", "Movie"));
        await AddAsync("book", DetailEntityType.Book, SeedOwnedWork("Dune", "Book"));
        await AddAsync("audiobook", DetailEntityType.Audiobook, SeedOwnedWork("Dune audiobook", "Audiobook"));
        await AddAsync("comic issue", DetailEntityType.ComicIssue, SeedOwnedWork("Issue one", "Comic"));
        await AddAsync("generic work", DetailEntityType.Work, SeedOwnedWork("Lonely track", "Music"));

        var (show, episode) = SeedTvShow();
        await AddAsync("tv show", DetailEntityType.TvShow, show);
        await AddAsync("tv episode", DetailEntityType.TvEpisode, episode, show.ToString("D"));
        await AddAsync("tv show without episodes", DetailEntityType.TvShow, SeedTvShowWithoutOwnedEpisodes());

        await AddAsync("music album", DetailEntityType.MusicAlbum, SeedMusicAlbum());
        await AddAsync("curated collection", DetailEntityType.Collection, SeedCuratedCollection());
        await AddAsync("movie series", DetailEntityType.MovieSeries, SeedMovieSeries());
        await AddAsync("person", DetailEntityType.Person, SeedPerson());
        await AddAsync("universe", DetailEntityType.Universe, SeedUniverse("Q4242"));
        await AddAsync("universe without timeline", DetailEntityType.Universe, SeedUniverse(null));
        return pages;
    }

    private static IEnumerable<(string List, IReadOnlyList<DetailAction> Actions)> ActionLists(DetailPageViewModel page)
    {
        yield return (nameof(page.PrimaryActions), page.PrimaryActions);
        yield return (nameof(page.SecondaryActions), page.SecondaryActions);
        yield return (nameof(page.OverflowActions), page.OverflowActions);
    }

    /// <summary>A menu action that only groups children (such as Rate) goes nowhere itself; its children must.</summary>
    private static IEnumerable<DetailAction> Flatten(IEnumerable<DetailAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.Children.Count == 0)
            {
                yield return action;
                continue;
            }

            foreach (var child in Flatten(action.Children))
            {
                yield return child;
            }
        }
    }

    private Guid SeedOwnedWork(string title, string mediaType)
    {
        var workId = Guid.NewGuid();
        InsertWork(workId, title, mediaType, parentWorkId: null, workKind: "standalone");
        return workId;
    }

    private (Guid Show, Guid Episode) SeedTvShow()
    {
        var show = Guid.NewGuid();
        var season = Guid.NewGuid();
        var episode = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state) VALUES (@season, @show, 'TV', 'parent', 'accepted');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@show, 'title', 'Northwatch', CURRENT_TIMESTAMP);
            """, new { show, season });
        InsertWork(episode, "Echoes in the Frost", "TV", parentWorkId: season, workKind: "child",
            extra: [("season_number", "1"), ("episode_number", "1")]);
        return (show, episode);
    }

    private Guid SeedTvShowWithoutOwnedEpisodes()
    {
        var show = Guid.NewGuid();
        var episode = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, is_catalog_only)
            VALUES (@episode, @show, 'TV', 'child', 'accepted', 1);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@show, 'title', 'Ghost Show', CURRENT_TIMESTAMP),
                   (@episode, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@episode, 'episode_number', '1', CURRENT_TIMESTAMP);
            """, new { show, episode });
        return show;
    }

    private Guid SeedMusicAlbum()
    {
        var album = Guid.NewGuid();
        var track = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, media_type, work_kind) VALUES (@album, 'Music', 'parent');
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@album, 'album', 'The Album', CURRENT_TIMESTAMP), (@album, 'title', 'The Album', CURRENT_TIMESTAMP);
            INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) VALUES (@album, 'album_artist', 0, 'The Artist');
            """, new { album });
        InsertWork(track, "Track One", "Music", parentWorkId: album, workKind: "child",
            extra: [("album", "The Album"), ("track_number", "1")]);
        using var artists = _database.CreateConnection();
        artists.Execute(
            "INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value) SELECT ma.id, 'artist', 0, 'The Artist' FROM media_assets ma JOIN editions e ON e.id = ma.edition_id WHERE e.work_id = @track;",
            new { track });
        return album;
    }

    private Guid SeedCuratedCollection()
    {
        var first = SeedOwnedWork("Curated one", "Movie");
        var second = SeedOwnedWork("Curated two", "Book");
        var collection = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, scope, resolution, membership_mode, owner_kind, audience)
            VALUES (@collection, 'Weekend picks', 'Custom', 'library', 'materialized', 'Manual', 'Library', 'Everyone');
            INSERT INTO collection_items (id, collection_id, work_id, sort_order)
            VALUES (@itemOne, @collection, @first, 1), (@itemTwo, @collection, @second, 2);
            """, new { collection, first, second, itemOne = Guid.NewGuid(), itemTwo = Guid.NewGuid() });
        return collection;
    }

    private Guid SeedMovieSeries()
    {
        var collection = Guid.NewGuid();
        var movie = SeedOwnedWork("Dune Part One", "Movie");
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type) VALUES (@collection, 'Dune films', 'ContentGroup');
            UPDATE works SET collection_id = @collection WHERE id = @movie;
            """, new { collection, movie });
        return collection;
    }

    private Guid SeedPerson()
    {
        var person = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            "INSERT INTO persons (id, name, created_at) VALUES (@person, 'Frank Herbert', CURRENT_TIMESTAMP);",
            new { person });
        return person;
    }

    private Guid SeedUniverse(string? qid)
    {
        var universe = Guid.NewGuid();
        var movie = SeedOwnedWork("Universe film", "Movie");
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type, wikidata_qid) VALUES (@universe, 'Test Universe', 'Universe', @qid);
            UPDATE works SET collection_id = @universe WHERE id = @movie;
            """, new { universe, qid, movie });
        return universe;
    }

    private void InsertWork(Guid workId, string title, string mediaType, Guid? parentWorkId, string workKind,
        IReadOnlyList<(string Key, string Value)>? extra = null)
    {
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state)
            VALUES (@workId, @parentWorkId, @mediaType, @workKind, 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            parentWorkId,
            mediaType,
            workKind,
            editionId,
            assetId,
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.bin",
            title,
        });
        foreach (var (key, value) in extra ?? [])
        {
            connection.Execute(
                "INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@assetId, @key, @value, CURRENT_TIMESTAMP), (@workId, @key, @value, CURRENT_TIMESTAMP);",
                new { assetId, workId, key, value });
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
