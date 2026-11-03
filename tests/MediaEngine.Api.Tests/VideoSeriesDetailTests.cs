using Dapper;
using MediaEngine.Api.Services.Details;
using MediaEngine.Api.Services.Details.Internals;
using MediaEngine.Api.Services.ReadServices;
using MediaEngine.Contracts.Details;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Providers.Services;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Api.Tests;

/// <summary>
/// A Videos series (a course or saved channel folder) opens as a show page even when nothing online knows it:
/// no cover, no cast, no description, no year, and with or without sections.
/// </summary>
public sealed class VideoSeriesDetailTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_video_series_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly DetailComposerService _composer;

    public VideoSeriesDetailTests()
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
    public async Task ACourseWithSectionsAndNoOnlineDetailsOpensAsAShowPage()
    {
        var series = SeedSeries(sections: true);

        var page = await ComposeAsync(series);

        Assert.NotNull(page);
        Assert.Equal(DetailEntityType.TvShow, page!.EntityType);
        Assert.Equal(series.ToString("D"), page.Id);
    }

    [Fact]
    public async Task ACourseWithLessonsDirectlyInItAndNoOnlineDetailsOpensAsAShowPage()
    {
        var series = SeedSeries(sections: false);

        var page = await ComposeAsync(series);

        Assert.NotNull(page);
        Assert.Equal(DetailEntityType.TvShow, page!.EntityType);
    }

    [Theory]
    [InlineData(DetailEntityType.Collection)]
    [InlineData(DetailEntityType.MovieSeries)]
    [InlineData(DetailEntityType.Work)]
    [InlineData(DetailEntityType.TvShow)]
    public async Task AVideosSeriesFolderOpenedUnderAnyOtherAddressOpensTheShowPage(DetailEntityType asked)
    {
        var series = SeedSeries(sections: true);

        var resolved = await _composer.ResolveVideoSeriesTypeAsync(asked, series);
        var page = await ComposeAsync(series, resolved);

        Assert.Equal(DetailEntityType.TvShow, resolved);
        Assert.NotNull(page);
        Assert.Equal(DetailEntityType.TvShow, page!.EntityType);
    }

    [Fact]
    public async Task AVideoInsideASeriesAndATvShowKeepTheirOwnAddress()
    {
        var series = SeedSeries(sections: false);
        Guid lesson;
        Guid tvShow = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            lesson = connection.ExecuteScalar<Guid>("SELECT id FROM works WHERE parent_work_id = @series LIMIT 1;", new { series });
            connection.Execute("INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@tvShow, 'TV', 'parent', 'accepted');", new { tvShow });
        }

        Assert.Equal(DetailEntityType.Work, await _composer.ResolveVideoSeriesTypeAsync(DetailEntityType.Work, lesson));
        Assert.Equal(DetailEntityType.Collection, await _composer.ResolveVideoSeriesTypeAsync(DetailEntityType.Collection, tvShow));
        Assert.Equal(DetailEntityType.Collection, await _composer.ResolveVideoSeriesTypeAsync(DetailEntityType.Collection, Guid.NewGuid()));
        Assert.Equal(DetailEntityType.Person, await _composer.ResolveVideoSeriesTypeAsync(DetailEntityType.Person, series));
    }

    private async Task<DetailPageViewModel?> ComposeAsync(Guid id, DetailEntityType type = DetailEntityType.TvShow) =>
        await _composer.BuildAuthorizedAsync(
            type,
            id,
            DetailPresentationContext.Watch,
            CancellationToken.None,
            null,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(true, true));

    private Guid SeedSeries(bool sections)
    {
        var series = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute(
                "INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@series, 'Videos', 'parent', 'accepted');",
                new { series });
        }

        var parents = new List<Guid>();
        if (sections)
        {
            for (var index = 1; index <= 2; index++)
            {
                var section = Guid.NewGuid();
                using var connection = _database.CreateConnection();
                connection.Execute(
                    "INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal, ordinal_sort) VALUES (@section, @series, 'Videos', 'parent', 'accepted', @index, @index);",
                    new { section, series, index });
                parents.Add(section);
            }
        }
        else
        {
            parents.Add(series);
        }

        var sectionNumber = 1;
        foreach (var parent in parents)
        {
            for (var episode = 1; episode <= 3; episode++)
            {
                InsertLesson(parent, sections ? sectionNumber : 1, episode);
            }

            sectionNumber++;
        }

        return series;
    }

    private void InsertLesson(Guid parent, int section, int episode)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal, ordinal_sort)
            VALUES (@workId, @parent, 'Videos', 'child', 'accepted', @episode, @episode);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP),
                   (@assetId, 'season_number', @section, CURRENT_TIMESTAMP),
                   (@workId, 'season_number', @section, CURRENT_TIMESTAMP),
                   (@assetId, 'episode_number', @episode, CURRENT_TIMESTAMP),
                   (@workId, 'episode_number', @episode, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            parent,
            editionId,
            assetId,
            episode = episode.ToString(),
            section = section.ToString(),
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mp4",
            title = $"Lesson {episode}",
        });
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
