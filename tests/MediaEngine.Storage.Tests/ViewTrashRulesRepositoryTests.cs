using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

/// <summary>
/// The Trash rules the data store owns: an item with a file in a linked (read-only) folder is read-only and cannot be
/// trashed, and an item in Trash leaves every Manual Gallery (list, count, cover) until it is restored.
/// </summary>
public sealed class ViewTrashRulesRepositoryTests : IDisposable
{
    private static readonly Guid TestHousehold = new("7e57a000-0000-0000-0000-000000000003");
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-trash-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewGalleryRepository _galleries;
    private readonly LocalAssetRepository _assets;

    public ViewTrashRulesRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _spaces = new ViewPersonalSpaceRepository(_database);
        _galleries = new ViewGalleryRepository(_database);
        _assets = new LocalAssetRepository(_database);
    }

    [Fact]
    public async Task AnItemWithAnyFileInALinkedFolder_IsReadOnlyAndCannotBeTrashed()
    {
        var owner = await CreateOwnerAsync();
        var managed = await AddPhotoAsync(owner, '1', "managed", "managed");
        var linked = await AddPhotoAsync(owner, '2', "linked", "linked");
        var livePhoto = (await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Image, "Live", null,
            [
                File(owner, '3', "live.jpg", "image/jpeg", InsertSource(owner, "managed")),
                File(owner, '4', "live.mov", "video/quicktime", InsertSource(owner, "linked")) with { Role = LocalAssetFileRoles.LivePhotoVideo },
            ]))).ItemId;
        var noSource = (await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Image, "No source", null,
            [File(owner, '5', "none.jpg", "image/jpeg", null)]))).ItemId;

        var readOnly = _assets.GetReadOnlyItemIds([managed, linked, livePhoto, noSource, Guid.NewGuid(), Guid.Empty]);

        Assert.Equal(new HashSet<Guid> { linked, livePhoto }, readOnly);
        Assert.Empty(_assets.GetReadOnlyItemIds([]));
        Assert.True(_assets.Find(managed)!.CanTrash);
        Assert.False(_assets.Find(linked)!.CanTrash);
        Assert.False(_assets.Find(livePhoto)!.CanTrash);
        Assert.True(_assets.Find(noSource)!.CanTrash);
    }

    [Fact]
    public async Task CanTrash_IsFalseForReadOnlyAndSharedLibraryItems_AlsoOnListedPages()
    {
        var owner = await CreateOwnerAsync();
        var managed = await AddPhotoAsync(owner, '1', "managed", "managed");
        var linked = await AddPhotoAsync(owner, '2', "linked", "linked");
        var sharedLibraryId = (await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(TestHousehold)).LibraryId;
        var sharedSource = InsertSharedSource(sharedLibraryId);
        var shared = (await _assets.UpsertAsync(new LocalAssetRegistration(
            sharedLibraryId, null, null, LocalAssetMediaKinds.Image, "Shared", null,
            [new LocalAssetFileRegistration(@"C:\shared\s.jpg", new string('f', 64), "s.jpg", "image/jpeg", 1024, DateTimeOffset.UtcNow) with { SourceId = sharedSource }],
            ScopeKind: LocalAssetScopeKinds.Shared))).ItemId;

        Assert.False(_assets.Find(shared)!.CanTrash);

        var page = _assets.QueryTimeline(new LocalAssetTimelineQuery([owner.LibraryId], 50));
        var byId = page.Items.ToDictionary(item => item.Id);
        Assert.True(byId[managed].CanTrash);
        Assert.False(byId[linked].CanTrash);
    }

    [Fact]
    public async Task ATrashedItem_LeavesItsManualGalleryListCountAndCover_AndComesBackOnRestore()
    {
        var owner = await CreateOwnerAsync();
        var first = await AddPhotoAsync(owner, '1', "first", "managed");
        var second = await AddPhotoAsync(owner, '2', "second", "managed");
        var gallery = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Trip", ViewGalleryKind.Manual, CoverItemId: first));
        await _galleries.AddItemsAsync(gallery.Id, [first, second]);
        Assert.Equal(2, (await _galleries.GetAsync(gallery.Id))!.ItemCount);

        await _assets.SetLifecycleStateAsync(first, LocalAssetLifecycleState.Trashed);

        var afterTrash = (await _galleries.GetAsync(gallery.Id))!;
        Assert.Equal(1, afterTrash.ItemCount);
        Assert.Null(afterTrash.CoverItemId);
        Assert.Equal(second, Assert.Single((await _galleries.GetItemsAsync(gallery.Id)).Items).ItemId);
        Assert.Equal(1, Assert.Single(await _galleries.GetOwnedAsync(owner.ProfileId)).ItemCount);

        // Saving the Gallery while its cover is in Trash does not wipe the cover.
        await _galleries.UpdateAsync(new UpdateViewGalleryCommand(
            gallery.Id, "Trip 2026", null, ViewGalleryKind.Manual, null, null, SortOrder: 0));

        await _assets.SetLifecycleStateAsync(first, LocalAssetLifecycleState.Active);

        var restored = (await _galleries.GetAsync(gallery.Id))!;
        Assert.Equal(2, restored.ItemCount);
        Assert.Equal(first, restored.CoverItemId);
        Assert.Equal(
            new HashSet<Guid> { first, second },
            (await _galleries.GetItemsAsync(gallery.Id)).Items.Select(item => item.ItemId).ToHashSet());
    }

    [Fact]
    public async Task ATrashedItem_CannotBeChosenAsAGalleryCover()
    {
        var owner = await CreateOwnerAsync();
        var photo = await AddPhotoAsync(owner, '1', "photo", "managed");
        var gallery = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Trip", ViewGalleryKind.Manual));
        await _assets.SetLifecycleStateAsync(photo, LocalAssetLifecycleState.Trashed);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Other", ViewGalleryKind.Manual, CoverItemId: photo)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _galleries.UpdateAsync(new UpdateViewGalleryCommand(
            gallery.Id, "Trip", null, ViewGalleryKind.Manual, null, photo, SortOrder: 0)));

        await _assets.SetLifecycleStateAsync(photo, LocalAssetLifecycleState.Active);
        var updated = await _galleries.UpdateAsync(new UpdateViewGalleryCommand(
            gallery.Id, "Trip", null, ViewGalleryKind.Manual, null, photo, SortOrder: 0));
        Assert.Equal(photo, updated!.CoverItemId);
    }

    [Fact]
    public async Task ATrashedItem_IsNoLongerSharedThroughAManualGallery_AndSharesAgainOnRestore()
    {
        var owner = await CreateOwnerAsync();
        var recipient = InsertProfile("Recipient");
        var photo = await AddPhotoAsync(owner, '1', "photo", "managed");
        var gallery = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Shared trip", ViewGalleryKind.Manual));
        await _galleries.AddItemsAsync(gallery.Id, [photo]);
        await _galleries.ReplaceSharesAsync(gallery.Id, [(recipient, ViewGallerySharePermission.View)]);
        Assert.True(await _galleries.IsItemSharedWithProfileAsync(photo, recipient));

        await _assets.SetLifecycleStateAsync(photo, LocalAssetLifecycleState.Trashed);

        Assert.False(await _galleries.IsItemSharedWithProfileAsync(photo, recipient));
        Assert.Empty(await _galleries.GetItemsSharedWithProfileAsync([photo], recipient));

        await _assets.SetLifecycleStateAsync(photo, LocalAssetLifecycleState.Active);
        Assert.True(await _galleries.IsItemSharedWithProfileAsync(photo, recipient));
    }

    [Fact]
    public async Task ASmartGallery_StillCountsOnlyActiveItems()
    {
        var owner = await CreateOwnerAsync();
        var first = await AddPhotoAsync(owner, '1', "first", "managed");
        var second = await AddPhotoAsync(owner, '2', "second", "managed");
        await _assets.SetFlagsAsync(first, favorite: true, hidden: null);
        await _assets.SetFlagsAsync(second, favorite: true, hidden: null);
        var smart = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Favorites", ViewGalleryKind.Smart,
            SmartRuleJson: """
                {"version":1,"groups":[{"id":"favorites","join_with_previous":"or","match_mode":"all","conditions":[{"field":"favorite","op":"eq","value":"true"}]}]}
                """));
        Assert.Equal(2, (await _galleries.GetAsync(smart.Id))!.ItemCount);

        await _assets.SetLifecycleStateAsync(first, LocalAssetLifecycleState.Trashed);

        Assert.Equal(1, (await _galleries.GetAsync(smart.Id))!.ItemCount);
    }

    private async Task<Guid> AddPhotoAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, char seed, string title, string storageMode) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Image, title, null,
            [File(owner, seed, $"{title}.jpg", "image/jpeg", InsertSource(owner, storageMode))]))).ItemId;

    private static LocalAssetFileRegistration File(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, char seed, string name, string mimeType, Guid? sourceId) =>
        new LocalAssetFileRegistration(
            Path.Combine(@"C:\trash", owner.ProfileId.ToString("N"), name),
            new string(seed, 64), name, mimeType, 1024, DateTimeOffset.UtcNow)
        {
            SourceId = sourceId,
        };

    private Guid InsertSource((Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, string storageMode)
    {
        var sourceId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_sources
                (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                 relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
            VALUES (@sourceId, 'personal', @spaceId, @libraryId, 'folder', @storageMode, @sourceKey, @storageMode,
                    @relative, @external, 1, 1, @now, @now);
            """, new
        {
            sourceId,
            spaceId = owner.SpaceId,
            libraryId = owner.LibraryId,
            storageMode,
            sourceKey = $"test:{sourceId:N}",
            relative = storageMode == "managed" ? "Personal/Uploads/test" : null,
            external = storageMode == "linked" ? @"D:\Photos" : null,
            now = DateTimeOffset.UtcNow,
        });
        return sourceId;
    }

    private Guid InsertSharedSource(Guid libraryId)
    {
        var sourceId = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT INTO view_sources
                (id, scope_kind, personal_space_id, library_id, source_type, name, source_key, storage_mode,
                 relative_path, external_path, include_subdirectories, enabled, created_at, updated_at)
            VALUES (@sourceId, 'shared', NULL, @libraryId, 'folder', 'Shared', @sourceKey, 'managed',
                    'Shared/Photos/test', NULL, 1, 1, @now, @now);
            """, new { sourceId, libraryId, sourceKey = $"shared:{sourceId:N}", now = DateTimeOffset.UtcNow });
        return sourceId;
    }

    private Guid InsertProfile(string name)
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@household, 'Test household', @now);
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
            VALUES (@id, @name, '#7C4DFF', 'StandardUser', @now, @household);
            """, new { id, name, now = DateTimeOffset.UtcNow, household = TestHousehold });
        return id;
    }

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnerAsync()
    {
        var profileId = InsertProfile("Owner");
        var libraryId = Guid.NewGuid();
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id, libraryId);
    }

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (System.IO.File.Exists(_path))
        {
            System.IO.File.Delete(_path);
        }
    }
}
