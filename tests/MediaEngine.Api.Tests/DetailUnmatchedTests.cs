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
/// A title added to its season with "Add anyway" is shown as "Not matched" (Details overlay D13): the detail read model carries a
/// flag on the page and on each child row that is one. Titles placed normally carry none.
/// </summary>
public sealed class DetailUnmatchedTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_detail_unmatched_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly DetailComposerService _composer;

    public DetailUnmatchedTests()
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
    public async Task AnEpisodeAddedAnywayIsFlaggedOnItsSeasonRow_AndTheOthersAreNot()
    {
        var (show, matched, unmatched) = SeedShow();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE works SET placement_unmatched = 1, ordinal = NULL, ordinal_sort = NULL WHERE id = @unmatched;", new { unmatched });
        }

        var page = await _composer.BuildAuthorizedAsync(
            DetailEntityType.TvShow, show, DetailPresentationContext.Watch, CancellationToken.None, null, Profile.SeedProfileId,
            new DetailActionAuthorizationContext());

        Assert.NotNull(page);
        Assert.False(page!.IsUnmatched);
        var items = page.SequencePlacement!.Groups.SelectMany(group => group.Items).ToList();
        Assert.True(items.Single(item => item.Id == unmatched.ToString("D")).IsUnmatched);
        Assert.False(items.Single(item => item.Id == matched.ToString("D")).IsUnmatched);
    }

    [Fact]
    public async Task APageHasNoNotMatchedFlagWhenNothingWasAddedAnyway()
    {
        var (show, _, _) = SeedShow();

        var page = await _composer.BuildAuthorizedAsync(
            DetailEntityType.TvShow, show, DetailPresentationContext.Watch, CancellationToken.None, null, Profile.SeedProfileId,
            new DetailActionAuthorizationContext());

        Assert.False(page!.IsUnmatched);
        Assert.All(page.SequencePlacement!.Groups.SelectMany(group => group.Items), item => Assert.False(item.IsUnmatched));
    }

    [Fact]
    public async Task AnEpisodeAddedAnyway_IsShownWithNoNumber_InAnUnnumberedGroupAtTheEnd()
    {
        var (show, matched, unmatched) = SeedShow();
        using (var connection = _database.CreateConnection())
        {
            // A number reaches the page by some other route (a series list position, data from before the mark): it must not show.
            connection.Execute(
                """
                UPDATE works SET placement_unmatched = 1, ordinal = NULL, ordinal_sort = NULL WHERE id = @unmatched;
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES (@unmatched, 'episode_number', '2', CURRENT_TIMESTAMP)
                ON CONFLICT(entity_id, key) DO NOTHING;
                """,
                new { unmatched });
        }

        var page = await _composer.BuildAuthorizedAsync(
            DetailEntityType.TvShow, show, DetailPresentationContext.Watch, CancellationToken.None, null, Profile.SeedProfileId,
            new DetailActionAuthorizationContext());

        var groups = page!.SequencePlacement!.Groups;
        var last = groups[^1];
        Assert.Equal("unnumbered", last.Key);
        Assert.Equal("Unnumbered", last.Title);
        var row = Assert.Single(last.Items);
        Assert.Equal(unmatched.ToString("D"), row.Id);
        Assert.True(row.IsUnmatched);
        Assert.Null(row.PositionNumber);
        Assert.Null(row.PositionSort);
        Assert.Null(row.PositionLabel);
        Assert.Null(row.PositionText);
        Assert.DoesNotContain(groups.Where(group => group.Key != "unnumbered").SelectMany(group => group.Items), item => item.Id == unmatched.ToString("D"));
        Assert.Contains(groups.SelectMany(group => group.Items), item => item.Id == matched.ToString("D") && !item.IsUnmatched);
    }

    private (Guid Show, Guid Matched, Guid Unmatched) SeedShow()
    {
        var show = Guid.NewGuid();
        var season = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO works (id, media_type, work_kind, curator_state) VALUES (@show, 'TV', 'parent', 'accepted');
                INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal)
                VALUES (@season, @show, 'TV', 'parent', 'accepted', 1);
                INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
                VALUES (@show, 'title', 'Northwatch', CURRENT_TIMESTAMP), (@season, 'season_number', '1', CURRENT_TIMESTAMP);
                """, new { show, season });
        }

        return (show, InsertEpisode(season, "1"), InsertEpisode(season, "2"));
    }

    private Guid InsertEpisode(Guid season, string episodeNumber)
    {
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO works (id, parent_work_id, media_type, work_kind, curator_state, ordinal)
            VALUES (@workId, @season, 'TV', 'child', 'accepted', @ordinal);
            INSERT INTO editions (id, work_id) VALUES (@editionId, @workId);
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, presented_at)
            VALUES (@assetId, @editionId, @hash, @path, CURRENT_TIMESTAMP);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at)
            VALUES (@assetId, 'title', @title, CURRENT_TIMESTAMP),
                   (@assetId, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@workId, 'season_number', '1', CURRENT_TIMESTAMP),
                   (@assetId, 'episode_number', @episodeNumber, CURRENT_TIMESTAMP),
                   (@workId, 'episode_number', @episodeNumber, CURRENT_TIMESTAMP);
            """, new
        {
            workId,
            season,
            editionId,
            assetId,
            episodeNumber,
            ordinal = int.Parse(episodeNumber, System.Globalization.CultureInfo.InvariantCulture),
            hash = Guid.NewGuid().ToString("N"),
            path = $"C:/library/{assetId:N}.mkv",
            title = $"S1E{episodeNumber}",
        });
        return workId;
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }
}
