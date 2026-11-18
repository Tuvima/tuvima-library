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
/// The picture the person chose for a Series (universal editor v8 D5) is stored under the Series collection. Its shelf tile and its page
/// show that picture when there is one, and keep the automatic stack of the members' covers or the root work's cover when there is not.
/// </summary>
public sealed class SeriesChosenPictureTests : IDisposable
{
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_series_picture_{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CollectionBrowseReadService _browse;
    private readonly DetailComposerService _composer;

    public SeriesChosenPictureTests()
    {
        DapperConfiguration.Configure();
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        ReadyWorks.TreatEveryWorkAsReady(_database);
        var collections = new CollectionRepository(_database);
        _browse = new CollectionBrowseReadService(collections, _database, NullLogger<CollectionBrowseReadService>.Instance);
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
            collectionBrowse: _browse,
            collectionCatalog: catalog,
            profiles: new ProfileRepository(_database));
    }

    public void Dispose()
    {
        TestTemp.ReleaseSqlitePool(_databasePath);
        try { _database.Dispose(); } catch (ObjectDisposedException) { /* Already closed by the pool sweep. */ }
        try { File.Delete(_databasePath); } catch (IOException) { /* The OS temp sweep removes a handle SQLite releases late. */ }
    }

    [Fact]
    public async Task ASeriesTile_WithAChosenCover_ShowsThatCover_NotTheStackOfItsMembersCovers()
    {
        var series = SeedSeries();
        var coverId = ChoosePicture(series.CollectionId, "Primary");
        var backgroundId = ChoosePicture(series.CollectionId, "Background");

        var group = Assert.Single(await _browse.GetSystemViewGroupsAsync("Books", "series", CancellationToken.None));

        Assert.Equal($"/api/v1/display/artwork/assets/{coverId:D}/content", group.CoverUrl);
        Assert.Equal($"/api/v1/display/artwork/assets/{backgroundId:D}/content", group.BackgroundUrl);
        Assert.Empty(group.PreviewItems);
    }

    [Fact]
    public async Task ASeriesTile_WithNoChosenCover_KeepsTheAutomaticStackOfItsMembersCovers()
    {
        SeedSeries();

        var group = Assert.Single(await _browse.GetSystemViewGroupsAsync("Books", "series", CancellationToken.None));

        Assert.Single(group.PreviewItems);
        Assert.DoesNotContain("/api/v1/display/artwork/assets/", group.CoverUrl ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASeriesPage_WithAChosenCoverBackgroundAndLogo_ShowsThem_InsteadOfTheRootWorksCover()
    {
        var series = SeedSeries();
        InsertRootCover(series.RootWorkId);
        var coverId = ChoosePicture(series.CollectionId, "Primary");
        var backgroundId = ChoosePicture(series.CollectionId, "Background");
        var logoId = ChoosePicture(series.CollectionId, "Logo");

        var page = await ComposeAsync(series.CollectionId);

        Assert.NotNull(page);
        Assert.Equal($"/api/v1/display/artwork/assets/{coverId:D}/content", page!.Artwork.CoverUrl);
        Assert.Contains(backgroundId.ToString("D"), page.Artwork.HeroArtwork.Url ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal($"/api/v1/display/artwork/assets/{logoId:D}/content", page.Artwork.LogoUrl);
    }

    [Fact]
    public async Task ASeriesPage_WithNoChosenCover_StillShowsTheRootWorksCover()
    {
        var series = SeedSeries();
        var rootCoverId = InsertRootCover(series.RootWorkId);

        var page = await ComposeAsync(series.CollectionId);

        Assert.NotNull(page);
        Assert.Equal($"/stream/artwork/{rootCoverId:D}", page!.Artwork.CoverUrl);
    }

    private Task<DetailPageViewModel?> ComposeAsync(Guid collectionId) =>
        _composer.BuildAuthorizedAsync(
            DetailEntityType.BookSeries,
            collectionId,
            DetailPresentationContext.Default,
            CancellationToken.None,
            null,
            Profile.SeedProfileId,
            new DetailActionAuthorizationContext(true, true));

    private (Guid CollectionId, Guid RootWorkId) SeedSeries()
    {
        var collectionId = Guid.NewGuid();
        var rootId = Guid.NewGuid();
        var workId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections (id, display_name, collection_type) VALUES (@collectionId, 'The Test Series', 'Series');
            INSERT INTO works (id, media_type, work_kind) VALUES (@rootId, 'Books', 'parent');
            INSERT INTO works (id, media_type, work_kind) VALUES (@workId, 'Books', 'standalone');
            INSERT INTO editions (id, work_id, format_label) VALUES (@editionId, @workId, 'EPUB');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root)
            VALUES (@assetId, @editionId, @hash, @path);
            INSERT INTO canonical_values (entity_id, key, value, last_scored_at) VALUES
                (@assetId, 'title', 'First Book', CURRENT_TIMESTAMP),
                (@assetId, 'series', 'The Test Series', CURRENT_TIMESTAMP),
                (@assetId, 'series_index', '1', CURRENT_TIMESTAMP);
            UPDATE works SET parent_work_id = @rootId, collection_id = @collectionId, work_kind = 'child', ordinal = 1, ordinal_sort = 1 WHERE id = @workId;
            """, new { collectionId, rootId, workId, editionId, assetId, hash = $"hash-{assetId:N}", path = $"C:/library/{assetId:N}.epub" });
        return (collectionId, rootId);
    }

    private Guid InsertRootCover(Guid rootWorkId)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO entity_assets (id, entity_id, entity_type, asset_type, local_image_path, is_preferred, is_user_override)
            VALUES (@id, @rootWorkId, 'Work', 'CoverArt', 'root-cover.jpg', 1, 1);
            """, new { id, rootWorkId });
        return id;
    }

    private Guid ChoosePicture(Guid collectionId, string role)
    {
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO artwork_assets (id, content_hash, original_path) VALUES (@assetId, @hash, 'chosen.jpg');
            INSERT INTO entity_artwork_links (id, entity_id, entity_type, artwork_asset_id, role, context, is_preferred, is_user_override)
            VALUES (@linkId, @collectionId, 'Collection', @assetId, @role, '', 1, 1);
            """, new { assetId, hash = $"art-{assetId:N}", linkId = Guid.NewGuid(), collectionId, role });
        return assetId;
    }
}
