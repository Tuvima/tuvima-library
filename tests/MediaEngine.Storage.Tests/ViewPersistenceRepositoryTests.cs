using Dapper;
using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

public sealed class ViewPersistenceRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ViewProfileRepository _profiles;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewGalleryRepository _galleries;
    private readonly LocalAssetRepository _assets;

    public ViewPersistenceRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _profiles = new ViewProfileRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _galleries = new ViewGalleryRepository(_database);
        _assets = new LocalAssetRepository(_database);
    }

    [Fact]
    public async Task ProfilePolicyAndPreferences_PersistIndependentSharedPermissionsAndDevicePortableState()
    {
        var ownerId = InsertProfile("Owner");
        var visibleProfileId = InsertProfile("Visible profile");

        Assert.Equal(ViewProfilePolicy.Default(ownerId, restricted: true), await _profiles.GetPolicyAsync(ownerId));
        Assert.True(await _profiles.SavePolicyAsync(new ViewProfilePolicy(
            ownerId,
            ViewEnabled: true,
            AccessSharedLibrary: false,
            SubmitToSharedLibrary: true,
            ShareGalleries: true,
            UpdatedAt: null)));

        var policy = await _profiles.GetPolicyAsync(ownerId);
        Assert.False(policy.AccessSharedLibrary);
        Assert.True(policy.SubmitToSharedLibrary);
        Assert.True(policy.ShareGalleries);

        Assert.True(await _profiles.SavePreferencesAsync(new ViewProfilePreferences(
            ownerId,
            ViewScopeKind.Profile,
            visibleProfileId,
            ViewTimelineDensity.Compact,
            null)));
        var preferences = await _profiles.GetPreferencesAsync(ownerId);
        Assert.Equal(ViewScopeKind.Profile, preferences.LastScopeKind);
        Assert.Equal(visibleProfileId, preferences.LastScopeProfileId);
        Assert.Equal(ViewTimelineDensity.Compact, preferences.TimelineDensity);
        Assert.True(preferences.ViewerInfoOpen);

        Assert.True(await _profiles.SavePreferencesAsync(preferences with { ViewerInfoOpen = false }));
        Assert.False((await _profiles.GetPreferencesAsync(ownerId)).ViewerInfoOpen);

        await Assert.ThrowsAsync<ArgumentException>(() => _profiles.SavePreferencesAsync(
            preferences with { LastScopeKind = ViewScopeKind.Mine }));
        Assert.False(await _profiles.SavePolicyAsync(
            ViewProfilePolicy.Default(Guid.NewGuid())));
    }

    [Fact]
    public async Task ProfilePolicyDefaults_OpenAndSendForEveryoneButAChildNeedsPermissionToSend()
    {
        var adultId = InsertProfile("Adult", "StandardUser");
        var childId = InsertProfile("Child");

        var adult = await _profiles.GetPolicyAsync(adultId);
        var child = await _profiles.GetPolicyAsync(childId);

        Assert.True(adult.ViewEnabled);
        Assert.True(adult.AccessSharedLibrary);
        Assert.True(adult.SubmitToSharedLibrary);
        Assert.True(child.AccessSharedLibrary);
        Assert.False(child.SubmitToSharedLibrary);

        Assert.True(await _profiles.SavePolicyAsync(child with { SubmitToSharedLibrary = true }));
        Assert.True((await _profiles.GetPolicyAsync(childId)).SubmitToSharedLibrary);
    }

    [Fact]
    public async Task PersonalSpace_SupportsMultipleSourcesAndStableDeviceIdentityPerOwner()
    {
        var ownerId = InsertProfile("Owner");
        var libraryId = Guid.NewGuid();
        var space = await _spaces.CreateAsync(ownerId, libraryId);
        Assert.Equal(space, await _spaces.GetByOwnerAsync(ownerId));
        Assert.Equal(space, await _spaces.GetByLibraryAsync(libraryId));
        Assert.Equal(space, await _spaces.CreateAsync(ownerId, libraryId));

        var source = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.Empty, space.Id, ViewSourceType.MobileBackup, "Shy's phone", "mobile:shy",
            DateTimeOffset.UtcNow, default, default, ViewSourceStorageMode.Managed,
            RelativePath: "profiles/owner/sources/phone", IncludeSubdirectories: true, Enabled: true));
        var folder = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.Empty, space.Id, ViewSourceType.Folder, "Camera imports", "folder:camera",
            null, default, default, ViewSourceStorageMode.Linked,
            ExternalPath: @"C:\camera-imports", IncludeSubdirectories: false, Enabled: false));
        var device = await _spaces.UpsertDeviceAsync(new ViewDevice(
            Guid.Empty, space.Id, source.Id, "ios-installation-123", "Shy's iPhone",
            "Apple", "iPhone", DateTimeOffset.UtcNow, ViewDeviceBackupState.Complete,
            default, default));

        Assert.Equal(2, (await _spaces.GetSourcesAsync(space.Id)).Count);
        var persistedFolder = (await _spaces.GetSourcesAsync(space.Id))[0];
        Assert.Equal(folder.Id, persistedFolder.Id);
        Assert.Equal(ViewSourceStorageMode.Linked, persistedFolder.StorageMode);
        Assert.Equal(@"C:\camera-imports", persistedFolder.ExternalPath);
        Assert.False(persistedFolder.IncludeSubdirectories);
        Assert.False(persistedFolder.Enabled);
        Assert.Equal(space.Id, Assert.Single(await _spaces.GetAllAsync()).Id);
        Assert.Equal(device.Id, Assert.Single(await _spaces.GetDevicesAsync(space.Id)).Id);

        var asset = await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, space.Id, ownerId, LocalAssetMediaKinds.Image, "Phone photo", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(
                @"C:\phone\IMG_0001.jpg", new string('a', 64), "IMG_0001.jpg", "image/jpeg",
                1024, DateTimeOffset.UtcNow, SourceId: source.Id, DeviceId: device.Id)]));
        var content = Assert.IsType<LocalAssetContentLocation>(_assets.ResolveContent(asset.ItemId));
        Assert.Equal(ownerId, content.OwnerProfileId);
        Assert.Equal(source.Id, content.SourceId);
        Assert.Equal(device.Id, content.DeviceId);

        var otherOwner = InsertProfile("Other");
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _spaces.CreateAsync(otherOwner, libraryId));
        Assert.True(await _spaces.DeleteSourceAsync(space.Id, folder.Id));
        Assert.False(await _spaces.DeleteSourceAsync(space.Id, folder.Id));
    }

    [Fact]
    public async Task Galleries_EnforceOwnershipManualMembershipUniquenessRulesAndShares()
    {
        var owner = await CreateOwnerAsync("Owner");
        var sharedProfileId = InsertProfile("Shared user");
        var other = await CreateOwnerAsync("Other");
        var first = await AddImageAsync(owner, '1', "first.jpg", new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        var second = await AddImageAsync(owner, '2', "second.jpg", new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));
        var foreign = await AddImageAsync(other, '3', "foreign.jpg", new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        var soundtrack = new Collection
        {
            Id = Guid.NewGuid(),
            DisplayName = "Family soundtrack",
            CreatedAt = DateTimeOffset.UtcNow,
            MembershipMode = ContainerMembershipMode.Manual,
            OwnerKind = ContainerOwnerKind.Profile,
            Audience = ContainerAudience.Private,
        };
        soundtrack.RestoreDefinition(CollectionType.Playlist, CollectionScope.User,
            CollectionResolution.Materialized, CollectionMatchMode.All,
            CollectionSortDirection.Asc, CollectionUniverseStatus.Unknown);
        soundtrack.SetVisibility(CollectionScope.User, owner.ProfileId);
        await new CollectionRepository(_database).CreateManagedCollectionAsync(soundtrack, []);

        var manual = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Family", ViewGalleryKind.Manual,
            CoverItemId: first.ItemId, SortOrder: 4, SoundtrackPlaylistId: soundtrack.Id));
        Assert.Equal(soundtrack.Id, manual.SoundtrackPlaylistId);
        var firstAdd = await _galleries.AddItemsAsync(manual.Id, [first.ItemId, second.ItemId, first.ItemId]);
        Assert.Equal(2, firstAdd.Added);
        Assert.Equal(0, firstAdd.AlreadyPresent);
        var duplicateAdd = await _galleries.AddItemsAsync(manual.Id, [first.ItemId]);
        Assert.Equal(0, duplicateAdd.Added);
        Assert.Equal(1, duplicateAdd.AlreadyPresent);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _galleries.AddItemsAsync(manual.Id, [foreign.ItemId]));

        var page = await _galleries.GetItemsAsync(manual.Id, limit: 1);
        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        var remainder = await _galleries.GetItemsAsync(
            manual.Id, page.NextPosition, page.NextItemId, limit: 1);
        Assert.Single(remainder.Items);
        Assert.False(remainder.HasMore);
        Assert.True(await _galleries.SetItemPositionAsync(manual.Id, second.ItemId, 10));
        Assert.Equal(1, await _galleries.RemoveItemsAsync(manual.Id, [second.ItemId]));
        var updated = Assert.IsType<ViewGallery>(await _galleries.UpdateAsync(new UpdateViewGalleryCommand(
            manual.Id, "Family trips", "Updated", ViewGalleryKind.Manual, null,
            first.ItemId, SortOrder: 2, SoundtrackPlaylistId: soundtrack.Id)));
        Assert.Equal("Family trips", updated.Name);
        Assert.Equal(2, updated.SortOrder);
        Assert.Equal(soundtrack.Id, updated.SoundtrackPlaylistId);
        Assert.Equal(soundtrack.Id, (await _galleries.GetAsync(manual.Id))!.SoundtrackPlaylistId);

        await _galleries.ReplaceSharesAsync(manual.Id,
            [(sharedProfileId, ViewGallerySharePermission.View)]);
        Assert.Equal(manual.Id, Assert.Single(await _galleries.GetSharedWithAsync(sharedProfileId)).Id);
        Assert.Equal(ViewGallerySharePermission.View,
            Assert.Single(await _galleries.GetSharesAsync(manual.Id)).Permission);
        Assert.True(await _galleries.IsItemSharedWithProfileAsync(first.ItemId, sharedProfileId));
        Assert.False(await _galleries.IsItemSharedWithProfileAsync(second.ItemId, sharedProfileId));

        var smart = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Favorites", ViewGalleryKind.Smart,
            SmartRuleJson: """
                {"version":1,"groups":[{"id":"favorites","join_with_previous":"or","match_mode":"all","conditions":[{"field":"favorite","op":"eq","value":"true"}]}]}
                """));
        var smartRecipient = InsertProfile("Smart Gallery recipient");
        await _galleries.ReplaceSharesAsync(smart.Id,
            [(smartRecipient, ViewGallerySharePermission.View)]);
        Assert.False(await _galleries.IsItemSharedWithProfileAsync(first.ItemId, smartRecipient));
        await _assets.SetFlagsAsync(first.ItemId, favorite: true, hidden: null);
        Assert.True(await _galleries.IsItemSharedWithProfileAsync(first.ItemId, smartRecipient));
        Assert.False(await _galleries.IsItemSharedWithProfileAsync(foreign.ItemId, smartRecipient));
        Assert.Equal(1, Assert.Single(await _galleries.GetOwnedAsync(owner.ProfileId),
            gallery => gallery.Id == smart.Id).ItemCount);
        Assert.Equal(1, Assert.Single(await _galleries.GetSharedWithAsync(smartRecipient)).ItemCount);
        await _assets.SetFlagsAsync(first.ItemId, favorite: null, hidden: true);
        Assert.Equal(0, Assert.IsType<ViewGallery>(await _galleries.GetAsync(smart.Id)).ItemCount);
        await _assets.SetFlagsAsync(first.ItemId, favorite: null, hidden: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _galleries.AddItemsAsync(smart.Id, [first.ItemId]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _galleries.RemoveItemsAsync(smart.Id, [first.ItemId]));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _galleries.SetItemPositionAsync(smart.Id, first.ItemId, 1));
        await Assert.ThrowsAsync<ArgumentException>(() => _galleries.CreateAsync(
            new CreateViewGalleryCommand(owner.ProfileId, owner.SpaceId, "Broken", ViewGalleryKind.Smart)));

        Assert.True(await _galleries.DeleteAsync(smart.Id));
        Assert.Null(await _galleries.GetAsync(smart.Id));
    }

    [Fact]
    public async Task Timeline_UsesAuthorizedLibrariesKeysetAndSeparatesArchiveFromTrash()
    {
        var firstOwner = await CreateOwnerAsync("First");
        var secondOwner = await CreateOwnerAsync("Second");
        var first = await AddImageAsync(firstOwner, '4', "newest.jpg",
            new DateTimeOffset(2026, 8, 23, 12, 0, 0, TimeSpan.Zero));
        var second = await AddImageAsync(secondOwner, '5', "older.jpg",
            new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));
        var third = await AddImageAsync(firstOwner, '6', "oldest.jpg",
            new DateTimeOffset(2026, 8, 21, 12, 0, 0, TimeSpan.Zero));

        Assert.Equal(2, _assets.Query(new LocalAssetQuery(firstOwner.LibraryId)).Items.Count);
        Assert.Single(_assets.Query(new LocalAssetQuery(secondOwner.LibraryId)).Items);

        var firstPage = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [firstOwner.LibraryId, secondOwner.LibraryId], Limit: 2));
        Assert.Equal([first.ItemId, second.ItemId], firstPage.Items.Select(item => item.Id));
        Assert.True(firstPage.HasMore);
        var finalPage = _assets.QueryTimeline(new LocalAssetTimelineQuery(
            [firstOwner.LibraryId, secondOwner.LibraryId], Limit: 2,
            BeforeEffectiveAt: firstPage.NextCursor!.EffectiveAt,
            BeforeItemId: firstPage.NextCursor.ItemId));
        Assert.Equal(third.ItemId, Assert.Single(finalPage.Items).Id);
        Assert.False(finalPage.HasMore);

        Assert.True(await _assets.SetLifecycleStateAsync(first.ItemId, LocalAssetLifecycleState.Archived));
        Assert.True(await _assets.SetLifecycleStateAsync(second.ItemId, LocalAssetLifecycleState.Trashed));
        Assert.Equal(third.ItemId, Assert.Single(_assets.QueryTimeline(new LocalAssetTimelineQuery(
            [firstOwner.LibraryId, secondOwner.LibraryId])).Items).Id);
        Assert.Equal(first.ItemId, Assert.Single(_assets.QueryTimeline(new LocalAssetTimelineQuery(
            [firstOwner.LibraryId, secondOwner.LibraryId], Lifecycle: LocalAssetLifecycleFilter.Archived)).Items).Id);
        Assert.Equal(second.ItemId, Assert.Single(_assets.QueryTimeline(new LocalAssetTimelineQuery(
            [firstOwner.LibraryId, secondOwner.LibraryId], Lifecycle: LocalAssetLifecycleFilter.Trashed)).Items).Id);
    }

    private static readonly Guid TestHousehold = new("7e57a000-0000-0000-0000-000000000002");

    private Guid InsertProfile(string name, string role = "RestrictedProfile")
    {
        var id = Guid.NewGuid();
        using var connection = _database.CreateConnection();
        connection.Execute("""
            INSERT OR IGNORE INTO households (id, name, created_at) VALUES (@household, 'Test household', @now);
            INSERT INTO profiles (id, display_name, avatar_color, role, created_at, household_id)
            VALUES (@id, @name, '#7C4DFF', @role, @now, @household);
            """, new { id, name, role, now = DateTimeOffset.UtcNow, household = TestHousehold });
        return id;
    }

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnerAsync(string name)
    {
        var profileId = InsertProfile(name);
        var libraryId = Guid.NewGuid();
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id, libraryId);
    }

    private Task<LocalAssetUpsertResult> AddImageAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner,
        char hashCharacter,
        string name,
        DateTimeOffset capturedAt) =>
        _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Image,
            Path.GetFileNameWithoutExtension(name),
            capturedAt,
            [new LocalAssetFileRegistration(
                Path.Combine(@"C:\view", owner.ProfileId.ToString("N"), name),
                new string(hashCharacter, 64), name, "image/jpeg", 1024, DateTimeOffset.UtcNow)]));

    public void Dispose()
    {
        _database.Dispose();
        using (var pool = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_path}"))
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearPool(pool);
        }

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
