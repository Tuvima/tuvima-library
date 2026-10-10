using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;
using Microsoft.Data.Sqlite;

namespace MediaEngine.Storage.Tests;

public sealed class CollectionViewSourceRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(), $"tuvima-collection-view-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly CollectionViewSourceRepository _sources;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewGalleryRepository _galleries;
    private readonly ViewProfileRepository _profiles;
    private readonly LocalAssetRepository _assets;

    public CollectionViewSourceRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _sources = new CollectionViewSourceRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _galleries = new ViewGalleryRepository(_database);
        _profiles = new ViewProfileRepository(_database);
        _assets = new LocalAssetRepository(_database);
    }

    [Fact]
    public async Task Sources_AddListUpdateAndRemove_KeepExclusiveDynamicReferences()
    {
        var owner = await CreateOwnerAsync("Owner");
        var collectionId = InsertCollection("Summer", "Custom", profileId: owner.ProfileId);
        var gallery = await CreateGalleryAsync(owner, "Trips");
        var replacementGallery = await CreateGalleryAsync(owner, "Family");

        var gallerySource = await _sources.AddGalleryAsync(new(
            collectionId, owner.ProfileId, gallery.Id, Position: 2));
        var rule = ViewSmartRuleDefinition.Create(1,
            """{"media_kind":"image","favorite":true}""");
        var ruleSource = await _sources.AddSmartRuleAsync(new(
            collectionId, owner.ProfileId, rule, Position: 1));

        var listed = await _sources.ListAsync(collectionId, owner.ProfileId);
        Assert.Equal([ruleSource.Id, gallerySource.Id], listed.Select(source => source.Id));
        Assert.Null(listed[0].GalleryId);
        Assert.NotNull(listed[0].SmartRule);
        Assert.Equal(gallery.Id, listed[1].GalleryId);
        Assert.Null(listed[1].SmartRule);

        var updated = Assert.IsType<CollectionViewSource>(await _sources.UpdateAsync(new(
            ruleSource.Id,
            collectionId,
            owner.ProfileId,
            CollectionViewSourceKind.Gallery,
            replacementGallery.Id,
            SmartRule: null,
            Position: 0)));
        Assert.Equal(CollectionViewSourceKind.Gallery, updated.Kind);
        Assert.Equal(replacementGallery.Id, updated.GalleryId);
        Assert.Null(updated.SmartRule);
        Assert.True(await _sources.RemoveAsync(
            collectionId, gallerySource.Id, owner.ProfileId));
        Assert.False(await _sources.RemoveAsync(
            collectionId, gallerySource.Id, owner.ProfileId));
    }

    [Fact]
    public async Task Sources_RejectInvalidCollectionProfileGalleryAndIndividualAssetRules()
    {
        var owner = await CreateOwnerAsync("Owner");
        var other = await CreateOwnerAsync("Other");
        var collectionId = InsertCollection("Owner collection", "Custom", owner.ProfileId);
        var automaticId = InsertCollection("Automatic", "Automatic", owner.ProfileId);
        var foreignGallery = await CreateGalleryAsync(other, "Other gallery");

        await Assert.ThrowsAsync<InvalidOperationException>(() => _sources.AddGalleryAsync(new(
            automaticId, owner.ProfileId, foreignGallery.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sources.AddGalleryAsync(new(
            collectionId, other.ProfileId, foreignGallery.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sources.AddGalleryAsync(new(
            collectionId, owner.ProfileId, foreignGallery.Id)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _sources.AddGalleryAsync(new(
            collectionId, owner.ProfileId, Guid.NewGuid())));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ViewSmartRuleDefinition.Create(2, """{"favorite":true}"""));
        Assert.Throws<ArgumentException>(() =>
            ViewSmartRuleDefinition.Create(1, "not json"));
        Assert.Throws<ArgumentException>(() =>
            ViewSmartRuleDefinition.Create(1, "{}"));
        Assert.Throws<ArgumentException>(() =>
            ViewSmartRuleDefinition.Create(1, $$"""{"local_asset_ids":["{{Guid.NewGuid():D}}"]}"""));
        Assert.Throws<ArgumentException>(() =>
            ViewSmartRuleDefinition.Create(1, """{"field":"item_id","operator":"equals"}"""));
        Assert.Throws<ArgumentException>(() =>
            ViewSmartRuleDefinition.Create(1, """{"field":"localAssetId","operator":"equals"}"""));
    }

    [Fact]
    public async Task GalleryMembershipChanges_DoNotRewriteCollectionSource_AndDeleteCascadesOnlyReference()
    {
        var owner = await CreateOwnerAsync("Owner");
        var collectionId = InsertCollection("Dynamic gallery", "Custom", profileId: null);
        var gallery = await CreateGalleryAsync(owner, "Live gallery");
        var source = await _sources.AddGalleryAsync(new(
            collectionId, owner.ProfileId, gallery.Id));
        var asset = await AddImageAsync(owner, 'a', "photo.jpg");
        var before = GetSourceStamp(source.Id);
        var collectionStamp = GetCollectionStamp(collectionId);

        Assert.Equal(1, (await _galleries.AddItemsAsync(gallery.Id, [asset.ItemId])).Added);
        Assert.Equal(1, await _galleries.RemoveItemsAsync(gallery.Id, [asset.ItemId]));

        Assert.Equal(before, GetSourceStamp(source.Id));
        Assert.Equal(collectionStamp, GetCollectionStamp(collectionId));
        using (var connection = _database.CreateConnection())
        {
            var columns = connection.Query<string>("""
                SELECT name FROM pragma_table_info('collection_view_sources')
                 WHERE lower(name) LIKE '%asset%' OR lower(name) LIKE '%item%';
                """).ToArray();
            Assert.Empty(columns);
        }

        Assert.True(await _galleries.DeleteAsync(gallery.Id));
        Assert.Empty(await _sources.ListAsync(collectionId, owner.ProfileId));
        using var finalConnection = _database.CreateConnection();
        Assert.Equal(1L, finalConnection.ExecuteScalar<long>(
            "SELECT COUNT(1) FROM collections WHERE id = @collectionId;", new { collectionId }));
    }

    [Fact]
    public async Task SchemaCannotStoreAnIndividualLocalItemAsACollectionSource()
    {
        var owner = await CreateOwnerAsync("Owner");
        var collectionId = InsertCollection("No assets", "Custom", profileId: null);
        var asset = await AddImageAsync(owner, 'b', "single.jpg");

        using var connection = _database.CreateConnection();
        var exception = Assert.Throws<SqliteException>(() => connection.Execute("""
            INSERT INTO collection_view_sources
                (id, collection_id, owner_profile_id, source_kind, gallery_id,
                 position, created_at, updated_at)
            VALUES
                (@id, @collectionId, @ownerProfileId, 'gallery', @itemId,
                 0, @now, @now);
            """, new
        {
            id = Guid.NewGuid(),
            collectionId,
            ownerProfileId = owner.ProfileId,
            itemId = asset.ItemId,
            now = DateTimeOffset.UtcNow,
        }));
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    [Fact]
    public async Task AuthorizedProjection_ReturnsOnlyCountFreeSourcesTheViewerMayResolve()
    {
        var owner = await CreateOwnerAsync("Owner");
        var viewer = await CreateOwnerAsync("Viewer");
        var stranger = await CreateOwnerAsync("Stranger");
        var collectionId = InsertCollection("Shared curated", "Custom", profileId: null);
        var gallery = await CreateGalleryAsync(owner, "Shared gallery");
        var gallerySource = await _sources.AddGalleryAsync(new(
            collectionId, owner.ProfileId, gallery.Id, Position: 1));
        var ruleSource = await _sources.AddSmartRuleAsync(new(
            collectionId,
            owner.ProfileId,
            ViewSmartRuleDefinition.Create(1, """{"captured_after":"2026-01-01"}"""),
            Position: 2));

        Assert.Empty(await _sources.GetAuthorizedProjectionAsync(
            [collectionId], viewer.ProfileId));
        await _galleries.ReplaceSharesAsync(gallery.Id,
            [(viewer.ProfileId, ViewGallerySharePermission.View)]);
        Assert.Equal(gallerySource.Id, Assert.Single(
            await _sources.GetAuthorizedProjectionAsync([collectionId], viewer.ProfileId)).SourceId);

        var visible = await _sources.GetAuthorizedProjectionAsync(
            [collectionId], viewer.ProfileId);
        Assert.Equal([gallerySource.Id], visible.Select(source => source.SourceId));
        Assert.All(visible, source => Assert.Equal(owner.ProfileId, source.OwnerProfileId));
        Assert.Empty(await _sources.GetAuthorizedProjectionAsync(
            [collectionId], stranger.ProfileId));
    }

    private Guid InsertProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
            VALUES (@id, @name, '#7C4DFF', 'RestrictedProfile', @now);
            """, new { id, name, now = DateTimeOffset.UtcNow });
        return id;
    }

    private Guid InsertCollection(string name, string type, Guid? profileId)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO collections
                (id, display_name, collection_type, scope, profile_id, modified_at, created_at)
            VALUES
                (@id, @name, @type, @scope, @profileId, @stamp, @stamp);
            """, new
        {
            id,
            name,
            type,
            scope = profileId.HasValue ? "user" : "library",
            profileId,
            stamp = "2026-01-01T00:00:00.0000000+00:00",
        });
        return id;
    }

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnerAsync(string name)
    {
        var profileId = InsertProfile(name);
        var libraryId = Guid.NewGuid();
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id, libraryId);
    }

    private Task<ViewGallery> CreateGalleryAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner,
        string name) =>
        _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, name, ViewGalleryKind.Manual));

    private Task<LocalAssetUpsertResult> AddImageAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner,
        char hashCharacter,
        string name) =>
        _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Image,
            Path.GetFileNameWithoutExtension(name),
            DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(
                Path.Combine(@"C:\view", owner.ProfileId.ToString("N"), name),
                new string(hashCharacter, 64),
                name,
                "image/jpeg",
                1024,
                DateTimeOffset.UtcNow)]));

    private string GetSourceStamp(Guid sourceId)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<string>(
            "SELECT updated_at FROM collection_view_sources WHERE id = @sourceId;",
            new { sourceId });
    }

    private string GetCollectionStamp(Guid collectionId)
    {
        using var connection = _database.CreateConnection();
        return connection.QuerySingle<string>(
            "SELECT modified_at FROM collections WHERE id = @collectionId;",
            new { collectionId });
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }
}
