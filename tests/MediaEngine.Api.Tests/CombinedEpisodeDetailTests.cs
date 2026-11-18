using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Entities;
using MediaEngine.Domain.Services;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A file named S01E19-20 is one file for two episodes (spec review-mockups-2026-11-18, D9): the show page shows it as ONE entry,
/// "E19-20: Title A + Title B", and counts 20 episodes in 19 files.
/// </summary>
public sealed class CombinedEpisodeDetailTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_combined_episodes_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly DetailComposerService _composer;

    public CombinedEpisodeDetailTests()
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
    public async Task ShowPage_WhenOneFileCoversEpisodes19And20_CountsTwentyEpisodesInNineteenFiles()
    {
        var show = SeedTwentyEpisodesWithOneDoubleFile();

        var page = await ComposeAsync(show);

        var pill = Assert.Single(page!.Metadata, item => item.Kind == "episode_count");
        Assert.Equal("20 episodes · 19 files", pill.Label);
    }

    [Fact]
    public async Task ShowPage_ListsTheDoubleFileAsOneEntryNamedByBothEpisodes()
    {
        var show = SeedTwentyEpisodesWithOneDoubleFile();

        var page = await ComposeAsync(show);

        var season = Assert.Single(page!.SequencePlacement!.Groups);
        Assert.Equal(19, season.Items.Count);
        var combined = Assert.Single(season.Items, item => item.EpisodeCount > 1);
        Assert.Equal("E19-20", combined.PositionText);
        Assert.Equal("Episode 19 + Episode 20", combined.Title);
        Assert.Equal(2, combined.EpisodeCount);
        Assert.Equal("E19-20: Episode 19 + Episode 20", $"{combined.PositionText}: {combined.Title}");
    }

    [Fact]
    public async Task ShowPage_SeasonCountsEpisodesAndFilesApart()
    {
        var show = SeedTwentyEpisodesWithOneDoubleFile();

        var page = await ComposeAsync(show);

        var season = Assert.Single(page!.SequencePlacement!.Groups);
        Assert.Equal(20, season.OwnedCount);
        Assert.Equal(19, season.FileCount);
        Assert.Equal(20, season.TotalKnownItems);
    }

    [Fact]
    public async Task ShowPage_WithNoDoubleFile_CountsEpisodesOnly()
    {
        var show = SeedShow(episodes: 3, doubleFileAt: null);

        var page = await ComposeAsync(show);

        var pill = Assert.Single(page!.Metadata, item => item.Kind == "episode_count");
        Assert.Equal("3 episodes", pill.Label);
        var season = Assert.Single(page.SequencePlacement!.Groups);
        Assert.Equal(0, season.FileCount);
        Assert.All(season.Items, item => Assert.Equal(1, item.EpisodeCount));
    }

    private Guid SeedTwentyEpisodesWithOneDoubleFile() => SeedShow(episodes: 20, doubleFileAt: 19);

    // Episodes 1..N each own a file, except that the file of episode `doubleFileAt` also covers the next episode.
    private Guid SeedShow(int episodes, int? doubleFileAt)
    {
        var show = Guid.NewGuid();
        var season = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                """
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal, ordinal_sort)
                VALUES (@season, @show, 'TV', 'parent', 'accepted', 1, 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@show, 'title', 'Dr Horrible', CURRENT_TIMESTAMP), (@season, 'season_number', '1', CURRENT_TIMESTAMP);
                """,
                new { show, season });
        }

        Guid? doubleAsset = null;
        Guid? firstOfDouble = null;
        for (var number = 1; number <= episodes; number++)
        {
            var covered = doubleFileAt is { } at && number == at + 1;
            var work = InsertEpisode(season, number, hasFile: !covered, out var asset);
            if (covered)
            {
                new MediaAssetCoverageRepository(_database).ReplaceForAssetAsync(
                    doubleAsset!.Value,
                    [
                        new MediaAssetCoverage(doubleAsset.Value, firstOfDouble!.Value, 1, null, null, MediaAssetCoverage.SourceFilename),
                        new MediaAssetCoverage(doubleAsset.Value, work, 2, null, null, MediaAssetCoverage.SourceFilename),
                    ]).GetAwaiter().GetResult();
            }
            else if (doubleFileAt == number)
            {
                doubleAsset = asset;
                firstOfDouble = work;
            }
        }

        return show;
    }

    private Guid InsertEpisode(Guid season, int number, bool hasFile, out Guid asset)
    {
        var work = Guid.NewGuid();
        var edition = Guid.NewGuid();
        asset = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute(
            """
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal, ordinal_sort)
            VALUES (@work, @season, 'TV', 'child', 'accepted', @number, @number);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@work, 'title', @title, CURRENT_TIMESTAMP),
                   (@work, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@work, 'episode_number', @episode, CURRENT_TIMESTAMP);
            """,
            new { work, season, number, title = $"Episode {number}", episode = number.ToString() });
        if (hasFile)
        {
            connection.Execute(
                """
                INSERT INTO editions (id, work_id) VALUES (@edition, @work);
                INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
                VALUES (@asset, @edition, @hash, @path, CURRENT_TIMESTAMP);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@asset, 'title', @title, CURRENT_TIMESTAMP),
                       (@asset, 'season_number', '1', CURRENT_TIMESTAMP),
                       (@asset, 'episode_number', @episode, CURRENT_TIMESTAMP);
                """,
                new
                {
                    work,
                    edition,
                    asset,
                    hash = Guid.NewGuid().ToString("N"),
                    path = $"C:/library/Dr Horrible/{asset:N}.mkv",
                    title = $"Episode {number}",
                    episode = number.ToString(),
                });
        }

        return work;
    }

    private async Task<DetailPageViewModel?> ComposeAsync(Guid id) =>
        await _composer.BuildAuthorizedAsync(
            DetailEntityType.TvShow,
            id,
            DetailPresentationContext.Watch,
            CancellationToken.None,
            null,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(true, true));

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
