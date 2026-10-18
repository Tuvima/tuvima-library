using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.Display;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A TV season has no page of its own (D2, D13): asking the Engine for one answers with its show's page, with that
/// season selected and its episodes listed. A season the profile cannot see does not exist (D8).
/// </summary>
public sealed class TvSeasonDetailTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_tv_season_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly DetailComposerService _composer;

    public TvSeasonDetailTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
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
    public async Task ASeasonOpensItsShowWithThatSeasonSelectedAndOnlyItsEpisodesListed()
    {
        var show = SeedShow("Northwatch");

        var page = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo);

        Assert.NotNull(page);
        Assert.Equal(DetailEntityType.TvShow, page!.EntityType);
        Assert.Equal(show.Id.ToString("D"), page.Id);
        Assert.Equal("Northwatch", page.Title);
        var placement = page.SequencePlacement!;
        Assert.Equal("season-2", placement.CurrentGroupKey);
        Assert.Equal("season-2", placement.CurrentItem.GroupKey);
        var selected = placement.Groups.Single(group => group.Key == placement.CurrentGroupKey);
        Assert.Equal(show.SeasonTwo.ToString("D"), selected.EntityId);
        Assert.Equal(show.SeasonTwoEpisodes.Select(id => id.ToString("D")), selected.Items.Select(item => item.Id));
        Assert.All(selected.Items, item => Assert.Equal("season-2", item.GroupKey));
    }

    [Fact]
    public async Task EachSeasonOpensWithItsOwnEpisodesSelected()
    {
        var show = SeedShow("Northwatch");

        var seasonOne = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonOne);
        var seasonTwo = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo);

        Assert.Equal("season-1", seasonOne!.SequencePlacement!.CurrentGroupKey);
        Assert.Equal("season-2", seasonTwo!.SequencePlacement!.CurrentGroupKey);
        Assert.Equal(show.SeasonOneEpisodes.Select(id => id.ToString("D")),
            seasonOne.SequencePlacement.Groups.Single(group => group.Key == "season-1").Items.Select(item => item.Id));
    }

    [Fact]
    public async Task TheShowAddressWithASeasonLandsOnTheSamePageAsTheSeasonAddress()
    {
        var show = SeedShow("Northwatch");

        var bySeason = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo);
        var byShow = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo, container: show.Id.ToString("D"));

        Assert.NotNull(byShow);
        Assert.Equal(bySeason!.Id, byShow!.Id);
        Assert.Equal(DetailEntityType.TvShow, byShow.EntityType);
        Assert.Equal("season-2", byShow.SequencePlacement!.CurrentGroupKey);
        Assert.Equal(
            bySeason.SequencePlacement!.Groups.Select(group => (group.Key, group.EntityId)),
            byShow.SequencePlacement.Groups.Select(group => (group.Key, group.EntityId)));
    }

    [Fact]
    public async Task TheShowPageWithoutASeasonStillOpensOnItsFirstSeason()
    {
        var show = SeedShow("Northwatch");

        var page = await ComposeAsync(DetailEntityType.TvShow, show.Id);

        Assert.Equal("season-1", page!.SequencePlacement!.CurrentGroupKey);
    }

    [Fact]
    public async Task ASeasonAskedForInsideAnotherShowIsNotFound()
    {
        var show = SeedShow("Northwatch");
        var other = SeedShow("Southwatch");

        Assert.Null(await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo, container: other.Id.ToString("D")));
        Assert.NotNull(await ComposeAsync(DetailEntityType.TvSeason, other.SeasonTwo, container: other.Id.ToString("D")));
    }

    [Fact]
    public async Task ASeasonThatWasMergedIntoAnotherOpensTheShowOfTheSeasonItBecame()
    {
        var show = SeedShow("Northwatch");
        var mergedAway = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@mergedAway, 'merged_into_work_id', @target, CURRENT_TIMESTAMP);
                """, new { mergedAway, target = show.SeasonTwo.ToString("D") });
        }

        var page = await ComposeAsync(DetailEntityType.TvSeason, mergedAway);

        Assert.NotNull(page);
        Assert.Equal(DetailEntityType.TvShow, page!.EntityType);
        Assert.Equal(show.Id.ToString("D"), page.Id);
        Assert.Equal("season-2", page.SequencePlacement!.CurrentGroupKey);
    }

    [Fact]
    public async Task AnIdThatIsNotASeasonIsNotFoundInsteadOfAShowPage()
    {
        var show = SeedShow("Northwatch");

        Assert.Null(await ComposeAsync(DetailEntityType.TvSeason, Guid.NewGuid()));
        Assert.Null(await ComposeAsync(DetailEntityType.TvSeason, show.Id));
        Assert.Null(await ComposeAsync(DetailEntityType.TvSeason, show.SeasonOneEpisodes[0]));
    }

    [Fact]
    public async Task ASeasonWhoseEpisodesTheProfileCannotSeeIsNotFound()
    {
        var show = SeedShow("Northwatch");
        var visibleToProfile = show.SeasonOneEpisodes
            .Select(episode => new DisplayWorkRow { WorkId = episode, AssetId = show.AssetOf[episode], MediaType = "TV" })
            .ToList();

        var seasonOne = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonOne, authorizedWorks: visibleToProfile);
        var seasonTwo = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonTwo, authorizedWorks: visibleToProfile);
        var nothingVisible = await ComposeAsync(DetailEntityType.TvSeason, show.SeasonOne, authorizedWorks: []);

        Assert.NotNull(seasonOne);
        Assert.Equal("season-1", seasonOne!.SequencePlacement!.CurrentGroupKey);
        Assert.Null(seasonTwo);
        Assert.Null(nothingVisible);
    }

    private async Task<DetailPageViewModel?> ComposeAsync(
        DetailEntityType type,
        Guid id,
        string? container = null,
        IReadOnlyList<DisplayWorkRow>? authorizedWorks = null) =>
        await _composer.BuildAuthorizedAsync(
            type,
            id,
            DetailPresentationContext.Watch,
            CancellationToken.None,
            container,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(),
            authorizedWorks: authorizedWorks);

    private sealed record SeededShow(
        Guid Id,
        Guid SeasonOne,
        Guid SeasonTwo,
        IReadOnlyList<Guid> SeasonOneEpisodes,
        IReadOnlyList<Guid> SeasonTwoEpisodes,
        IReadOnlyDictionary<Guid, Guid> AssetOf);

    /// <summary>A show with two seasons of two owned episodes each, every season with its own work.</summary>
    private SeededShow SeedShow(string title)
    {
        var show = Guid.NewGuid();
        var seasonOne = Guid.NewGuid();
        var seasonTwo = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal)
                VALUES (@seasonOne, @show, 'TV', 'parent', 'accepted', 1), (@seasonTwo, @show, 'TV', 'parent', 'accepted', 2);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@show, 'title', @title, CURRENT_TIMESTAMP),
                       (@seasonOne, 'season_number', '1', CURRENT_TIMESTAMP),
                       (@seasonTwo, 'season_number', '2', CURRENT_TIMESTAMP);
                """, new { show, seasonOne, seasonTwo, title });
        }

        var assets = new Dictionary<Guid, Guid>();
        var first = new List<Guid>();
        var second = new List<Guid>();
        foreach (var (season, number, list) in new[] { (seasonOne, "1", first), (seasonTwo, "2", second) })
        {
            for (var episode = 1; episode <= 2; episode++)
            {
                var (workId, assetId) = InsertEpisode(season, number, episode.ToString());
                assets[workId] = assetId;
                list.Add(workId);
            }
        }

        return new SeededShow(show, seasonOne, seasonTwo, first, second, assets);
    }

    private (Guid WorkId, Guid AssetId) InsertEpisode(Guid season, string seasonNumber, string episodeNumber)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state)
            VALUES (@workId, @season, 'TV', 'child', 'accepted');
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP),
                   (@assetId, 'season_number', @seasonNumber, CURRENT_TIMESTAMP),
                   (@workId, 'season_number', @seasonNumber, CURRENT_TIMESTAMP),
                   (@assetId, 'episode_number', @episodeNumber, CURRENT_TIMESTAMP),
                   (@workId, 'episode_number', @episodeNumber, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            season,
            editionId,
            assetId,
            seasonNumber,
            episodeNumber,
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mkv",
            title = $"S{seasonNumber}E{episodeNumber}",
        });
        return (workId, assetId);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
