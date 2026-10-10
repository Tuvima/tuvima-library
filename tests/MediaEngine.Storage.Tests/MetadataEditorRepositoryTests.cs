using Dapper;
using MediaEngine.Storage;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class MetadataEditorRepositoryTests : IDisposable
{
    private readonly string _databasePath;
    private readonly DatabaseConnection _database;
    private readonly MetadataEditorRepository _repository;

    public MetadataEditorRepositoryTests()
    {
        DapperConfiguration.Configure();
        _databasePath = Path.Combine(Path.GetTempPath(), $"tuvima_metadata_endpoint_{Guid.NewGuid():N}.db");
        _database = new DatabaseConnection(_databasePath);
        _database.InitializeSchema();
        _database.RunStartupChecks();
        _repository = new MetadataEditorRepository(_database);
    }

    [Fact]
    public async Task EditorAndArtworkResolution_UseCurrentHierarchyForWorkAssetAndCollectionLaunches()
    {
        var hierarchy = await SeedHierarchyAsync();

        var rootLaunch = await _repository.ResolveEditorLaunchAsync(hierarchy.RootWorkId);
        var assetLaunch = await _repository.ResolveEditorLaunchAsync(hierarchy.AssetId);
        var collectionLaunch = await _repository.ResolveEditorLaunchAsync(hierarchy.CollectionId);
        var artwork = await _repository.ResolveArtworkContextAsync(hierarchy.ChildWorkId);

        Assert.NotNull(rootLaunch);
        Assert.Equal("Work", rootLaunch.LaunchEntityKind);
        Assert.Equal(hierarchy.AssetId, rootLaunch.RepresentativeAssetId);
        Assert.Equal("C:/library/episode.mkv", rootLaunch.RepresentativeMediaFilePath);

        Assert.NotNull(assetLaunch);
        Assert.Equal("MediaAsset", assetLaunch.LaunchEntityKind);
        Assert.Equal(hierarchy.ChildWorkId, assetLaunch.WorkId);
        Assert.Equal(hierarchy.RootWorkId, assetLaunch.RootWorkId);

        Assert.NotNull(collectionLaunch);
        Assert.Equal("Collection", collectionLaunch.LaunchEntityKind);
        Assert.Equal(hierarchy.RootWorkId, collectionLaunch.WorkId);

        Assert.Equal(hierarchy.ChildWorkId, artwork.WorkId);
        Assert.Equal(hierarchy.RootWorkId, artwork.RootWorkId);
        Assert.Equal(hierarchy.AssetId, artwork.PrimaryAssetId);
        Assert.Contains(hierarchy.RootWorkId, artwork.ArtworkEntityIds);
        Assert.Contains(hierarchy.ChildWorkId, artwork.ArtworkEntityIds);
        Assert.Contains(hierarchy.AssetId, artwork.ArtworkEntityIds);
    }

    [Fact]
    public async Task ReclassificationDisplayOverridesAndArtistResolution_AreTypedAndGuidSafe()
    {
        var hierarchy = await SeedHierarchyAsync();
        var personId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            await connection.ExecuteAsync("""
                UPDATE works
                SET display_overrides_json = '{"display_title":"My Episode"}'
                WHERE id = @workId;
                INSERT INTO persons (id, name, created_at)
                VALUES (@personId, 'The Artist', @now);
                INSERT INTO person_media_links (media_asset_id, person_id, role)
                VALUES (@assetId, @personId, 'Artist');
                INSERT INTO canonical_value_arrays (entity_id, key, ordinal, value)
                VALUES (@assetId, 'artist', 0, 'The Artist');
                """, new
            {
                workId = hierarchy.ChildWorkId,
                personId,
                now = DateTimeOffset.UtcNow.ToString("O"),
                assetId = hierarchy.AssetId,
            });
        }

        var byWork = await _repository.ResolveReclassifyTargetAsync(hierarchy.ChildWorkId);
        var byAsset = await _repository.ResolveReclassifyTargetAsync(hierarchy.AssetId);
        await _repository.UpdateWorkMediaTypeAsync(hierarchy.ChildWorkId, "Movies");
        var overrides = await _repository.GetDisplayOverridesAsync(hierarchy.ChildWorkId);
        var linkedArtist = await _repository.ResolveArtistArtworkOwnerAsync(hierarchy.AssetId, null);
        var namedArtist = await _repository.ResolveArtistArtworkOwnerAsync(null, "the artist");
        var representative = await _repository.ResolveRepresentativeAssetAsync(
            [Guid.NewGuid(), hierarchy.RootWorkId]);

        Assert.Equal(hierarchy.AssetId, byWork.TargetAssetId);
        Assert.Equal(hierarchy.ChildWorkId, byWork.WorkId);
        Assert.Equal(hierarchy.AssetId, byAsset.TargetAssetId);
        Assert.Equal(hierarchy.ChildWorkId, byAsset.WorkId);
        Assert.Equal("My Episode", overrides["display_title"]);
        Assert.Equal(personId, linkedArtist);
        Assert.Equal(personId, namedArtist);
        Assert.Equal(hierarchy.AssetId, representative);

        using var verify = _database.CreateConnection();
        Assert.Equal("Movies", await verify.ExecuteScalarAsync<string>(
            "SELECT media_type FROM works WHERE id = @workId",
            new { workId = hierarchy.ChildWorkId }));
    }

    [Fact]
    public async Task UnknownEntitiesAndCancellation_DegradePredictably()
    {
        var unknownId = Guid.NewGuid();
        var reclassify = await _repository.ResolveReclassifyTargetAsync(unknownId);
        var artwork = await _repository.ResolveArtworkContextAsync(unknownId);

        Assert.Equal(unknownId, reclassify.TargetAssetId);
        Assert.Null(reclassify.WorkId);
        Assert.Null(await _repository.ResolveEditorLaunchAsync(unknownId));
        Assert.Equal([unknownId], artwork.ArtworkEntityIds);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _repository.ResolveEditorLaunchAsync(unknownId, cancellation.Token));
    }

    private async Task<SeededHierarchy> SeedHierarchyAsync()
    {
        var collectionId = Guid.NewGuid();
        var rootWorkId = Guid.NewGuid();
        var childWorkId = Guid.NewGuid();
        var editionId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        await connection.ExecuteAsync("""
            INSERT INTO collections (id, display_name, collection_type)
            VALUES (@collectionId, 'Test Show', 'Universe');
            INSERT INTO works (id, collection_id, media_type, work_kind, ownership)
            VALUES (@rootWorkId, @collectionId, 'TV', 'parent', 'Owned');
            INSERT INTO works (id, collection_id, parent_work_id, media_type, work_kind, ownership)
            VALUES (@childWorkId, @collectionId, @rootWorkId, 'TV', 'child', 'Owned');
            INSERT INTO editions (id, work_id, format_label)
            VALUES (@editionId, @childWorkId, 'MKV');
            INSERT INTO media_assets (id, edition_id, content_hash, file_path_root, status, writeback_status)
            VALUES (@assetId, @editionId, @hash, 'C:/library/episode.mkv', 'Normal', 'ok');
            """, new
        {
            collectionId,
            rootWorkId,
            childWorkId,
            editionId,
            assetId,
            hash = Guid.NewGuid().ToString("N"),
        });

        return new SeededHierarchy(collectionId, rootWorkId, childWorkId, editionId, assetId);
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_databasePath);
    }

    private sealed record SeededHierarchy(
        Guid CollectionId,
        Guid RootWorkId,
        Guid ChildWorkId,
        Guid EditionId,
        Guid AssetId);
}
