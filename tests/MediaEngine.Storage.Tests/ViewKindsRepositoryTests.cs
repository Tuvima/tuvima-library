using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;

namespace MediaEngine.Storage.Tests;

// Library setup WP8a: the data structure behind View kinds, the per-kind permission and the "added by" tag.
public sealed class ViewKindsRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-kinds-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly ViewProfileRepository _profiles;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly LocalAssetRepository _assets;

    public ViewKindsRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _profiles = new ViewProfileRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _assets = new LocalAssetRepository(_database);
    }

    [Fact]
    public void FreshStore_AppliesTheViewKindsStepOnceAndGivesTheNewColumnsTheirDefaults()
    {
        using var connection = _database.CreateConnection();

        Assert.Equal(1, connection.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM data_store_upgrades WHERE name = 'view_kinds_and_added_by';"));
        string[] Columns(string table) => connection.Query<string>($"SELECT name FROM pragma_table_info('{table}');").ToArray();
        Assert.Contains("content_kind", Columns("view_sources"));
        Assert.Contains("added_by_profile_id", Columns("view_sources"));
        Assert.Contains("skipped_file_count", Columns("view_sources"));
        Assert.Contains("content_kind", Columns("local_items"));
        Assert.Contains("added_by_profile_id", Columns("local_items"));
        Assert.Contains("photos_enabled", Columns("profile_view_policies"));
        Assert.Contains("personal_videos_enabled", Columns("profile_view_policies"));
        Assert.Contains("ix_local_items_added_by",
            connection.Query<string>("SELECT name FROM pragma_index_list('local_items');").ToList());

        string Default(string table, string column) => connection.ExecuteScalar<string>(
            "SELECT dflt_value FROM pragma_table_info(@table) WHERE name = @column;", new { table, column })!;
        Assert.Equal("'photos'", Default("view_sources", "content_kind"));
        Assert.Equal("0", Default("view_sources", "skipped_file_count"));
        Assert.Equal("1", Default("profile_view_policies", "photos_enabled"));
        Assert.Equal("1", Default("profile_view_policies", "personal_videos_enabled"));
    }

    [Fact]
    public async Task AnItemWrittenWithoutAKindTakesTheOneThatMatchesItsFileType_AndAudioHasNone()
    {
        var owner = await CreateOwnerAsync("Owner");

        var photo = await AddAsync(owner, 'a', LocalAssetMediaKinds.Image);
        var video = await AddAsync(owner, 'b', LocalAssetMediaKinds.Video);
        var document = await AddAsync(owner, 'c', LocalAssetMediaKinds.Document);
        var audio = await AddAsync(owner, 'd', LocalAssetMediaKinds.Audio);
        var receipt = await AddAsync(owner, 'e', LocalAssetMediaKinds.Image, contentKind: ViewContentKinds.Documents);

        Assert.Equal(ViewContentKinds.Photos, _assets.GetContentKind(photo.ItemId));
        Assert.Equal(ViewContentKinds.PersonalVideos, _assets.GetContentKind(video.ItemId));
        Assert.Equal(ViewContentKinds.Documents, _assets.GetContentKind(document.ItemId));
        Assert.Null(_assets.GetContentKind(audio.ItemId));
        Assert.Equal(ViewContentKinds.Documents, _assets.GetContentKind(receipt.ItemId));
        Assert.Null(_assets.GetContentKind(Guid.NewGuid()));
        await Assert.ThrowsAsync<ArgumentException>(() => AddAsync(owner, 'f', LocalAssetMediaKinds.Image, contentKind: "snapshots"));
    }

    [Fact]
    public async Task EachKindHasItsOwnSwitchThatSurvivesOtherChangesAndDefaultsToOn()
    {
        var profileId = InsertProfile("Person");

        var fresh = await _profiles.GetPolicyAsync(profileId);
        Assert.True(fresh.PhotosEnabled);
        Assert.True(fresh.PersonalVideosEnabled);
        Assert.Empty(fresh.BlockedContentKinds);

        Assert.True(await _profiles.SavePolicyAsync(fresh with { PhotosEnabled = false, PersonalVideosEnabled = false, DocumentsEnabled = false }));
        var saved = await _profiles.GetPolicyAsync(profileId);
        Assert.Equal(
            [ViewContentKinds.Photos, ViewContentKinds.PersonalVideos, ViewContentKinds.Documents],
            saved.BlockedContentKinds);

        Assert.True(await _profiles.SavePolicyAsync(saved with { ShareGalleries = true, PhotosEnabled = true }));
        var again = await _profiles.GetPolicyAsync(profileId);
        Assert.Equal([ViewContentKinds.PersonalVideos, ViewContentKinds.Documents], again.BlockedContentKinds);
    }

    [Fact]
    public async Task ASourceKeepsItsKindWhoAddedItAndHowManyFilesTheLastScanSkipped()
    {
        var owner = await CreateOwnerAsync("Owner");
        var adder = InsertProfile("Adder");
        var now = DateTimeOffset.UtcNow;

        var saved = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), owner.SpaceId, ViewSourceType.Folder, "Paperwork", "test:paperwork", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: @"D:\Paperwork",
            ContentKind: ViewContentKinds.Documents, AddedByProfileId: adder));
        await _spaces.SetSourceSkippedFileCountAsync(owner.SpaceId, saved.Id, 3);

        var read = Assert.Single(await _spaces.GetSourcesAsync(owner.SpaceId));
        Assert.Equal(ViewContentKinds.Documents, read.ContentKind);
        Assert.Equal(adder, read.AddedByProfileId);
        Assert.Equal(3, read.SkippedFileCount);

        // Renaming or changing the kind later keeps who added it and the count.
        await _spaces.UpsertSourceAsync(read with { Name = "Paperwork 2", ContentKind = ViewContentKinds.Photos, AddedByProfileId = null });
        var renamed = Assert.Single(await _spaces.GetSourcesAsync(owner.SpaceId));
        Assert.Equal(ViewContentKinds.Photos, renamed.ContentKind);
        Assert.Equal(adder, renamed.AddedByProfileId);
        Assert.Equal(3, renamed.SkippedFileCount);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            _spaces.SetSourceSkippedFileCountAsync(owner.SpaceId, saved.Id, -1));
    }

    [Fact]
    public async Task AnItemRecordsWhoAddedItAndTheirCurrentNameFollowsARename()
    {
        var owner = await CreateOwnerAsync("Owner");
        var adder = InsertProfile("Adder");
        var added = await AddAsync(owner, 'a', LocalAssetMediaKinds.Image, addedBy: adder);
        var untagged = await AddAsync(owner, 'b', LocalAssetMediaKinds.Image);

        Assert.Equal(adder, _assets.Find(added.ItemId)!.AddedByProfileId);
        Assert.Equal("Adder", _assets.Find(added.ItemId)!.AddedByName);
        Assert.Null(_assets.Find(untagged.ItemId)!.AddedByProfileId);
        Assert.Null(_assets.Find(untagged.ItemId)!.AddedByName);

        using (var connection = _database.CreateConnection())
        {
            connection.Execute("UPDATE profiles SET display_name = 'Adder Renamed' WHERE id = @adder;", new { adder });
        }

        Assert.Equal("Adder Renamed", _assets.Find(added.ItemId)!.AddedByName);

        // A deleted profile reads as nobody, and its items stay.
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("DELETE FROM profiles WHERE id = @adder;", new { adder });
        }

        var orphan = _assets.Find(added.ItemId)!;
        Assert.Null(orphan.AddedByProfileId);
        Assert.Null(orphan.AddedByName);
    }

    [Fact]
    public async Task TheTimelineFiltersByWhoAddedItAndLeavesOutBlockedKinds()
    {
        var owner = await CreateOwnerAsync("Owner");
        var alice = InsertProfile("Alice");
        var bob = InsertProfile("Bob");
        await AddAsync(owner, 'a', LocalAssetMediaKinds.Image, addedBy: alice);
        await AddAsync(owner, 'b', LocalAssetMediaKinds.Image, addedBy: alice);
        await AddAsync(owner, 'c', LocalAssetMediaKinds.Video, addedBy: bob);
        await AddAsync(owner, 'd', LocalAssetMediaKinds.Document, addedBy: bob);

        LocalAssetTimelineQuery Query(IReadOnlyCollection<Guid>? addedBy = null, params string[] excluded) =>
            new([owner.LibraryId], AddedByProfileIds: addedBy, ExcludedContentKinds: excluded);

        Assert.Equal(4, _assets.QueryTimeline(Query()).Items.Count);
        Assert.Equal(2, _assets.QueryTimeline(Query([alice])).Items.Count);
        Assert.Equal(4, _assets.QueryTimeline(Query([alice, bob])).Items.Count);
        Assert.Empty(_assets.QueryTimeline(Query([Guid.NewGuid()])).Items);
        Assert.Equal(2, _assets.QueryTimeline(Query(null, ViewContentKinds.Photos)).Items.Count);
        Assert.Equal(2, _assets.QueryTimeline(Query([alice, bob], ViewContentKinds.Photos)).Items.Count);
        Assert.Single(_assets.QueryTimeline(Query([bob], ViewContentKinds.PersonalVideos)).Items);
        Assert.Equal(3, _assets.QueryTimelineIndex(Query(null, ViewContentKinds.Documents)).Sum(bucket => bucket.AssetCount));

        var counts = _assets.QueryAddedBy(Query());
        Assert.Equal([("Alice", 2), ("Bob", 2)], counts.Select(count => (count.Name, count.ItemCount)).ToArray());
        var withoutVideos = _assets.QueryAddedBy(Query(null, ViewContentKinds.PersonalVideos));
        Assert.Equal([("Alice", 2), ("Bob", 1)], withoutVideos.Select(count => (count.Name, count.ItemCount)).ToArray());
        var withoutBobsKinds = _assets.QueryAddedBy(Query(null, ViewContentKinds.PersonalVideos, ViewContentKinds.Documents));
        Assert.Equal([alice], withoutBobsKinds.Select(count => count.ProfileId).ToArray());
    }

    [Fact]
    public async Task PlacesAndTheAtlasLeaveOutPicturesAndVideosOfAKindTheProfileMayNotUse()
    {
        var owner = await CreateOwnerAsync("Owner");
        await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Image, "Lighthouse photo", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(@"C:\view\lighthouse.jpg", new string('a', 64), "lighthouse.jpg", "image/jpeg", 10, DateTimeOffset.UtcNow)],
            Latitude: 10.0, Longitude: 20.0, LocationName: "Lighthouse"));
        await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, LocalAssetMediaKinds.Video, "Harbour clip", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(@"C:\view\harbour.mp4", new string('b', 64), "harbour.mp4", "video/mp4", 10, DateTimeOffset.UtcNow)],
            Latitude: 30.0, Longitude: 40.0, LocationName: "Harbour"));
        var discovery = new ViewDiscoveryRepository(_database);
        IReadOnlyCollection<Guid> libraries = [owner.LibraryId];

        Assert.Equal(["Harbour", "Lighthouse"], discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(libraries)).Items.Select(place => place.Name).Order().ToArray());
        var withoutPhotos = discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(libraries, ExcludedContentKinds: [ViewContentKinds.Photos]));
        Assert.Equal("Harbour", Assert.Single(withoutPhotos.Items).Name);
        var atlas = discovery.QueryAtlas(new ViewAtlasDiscoveryQuery(libraries, ExcludedContentKinds: [ViewContentKinds.PersonalVideos]));
        Assert.Equal("Lighthouse", Assert.Single(atlas.Hotspots).Name);
        var none = discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(libraries,
            ExcludedContentKinds: [ViewContentKinds.Photos, ViewContentKinds.PersonalVideos]));
        Assert.Empty(none.Items);
    }

    [Fact]
    public async Task ChangingASharedFoldersKindKeepsItemsTheSharedLibraryHoldsAndRemovesTheOthersItNoLongerAccepts()
    {
        InsertProfile("Household member");
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(TestHousehold);
        var sharedRepository = new ViewSharedLibraryRepository(_database);
        var folder = await sharedRepository.UpsertSourceAsync(SharedFolder(shared.LibraryId, ViewContentKinds.Photos));
        var held = await AddSharedAsync(shared.LibraryId, folder.Id, '1', LocalAssetMediaKinds.Image);
        var loose = await AddSharedAsync(shared.LibraryId, folder.Id, '2', LocalAssetMediaKinds.Image);
        using (var connection = _database.CreateConnection())
        {
            // The Shared library holds one of the two with its own record, so re-applying the folder must leave it alone.
            connection.Execute("""
                INSERT INTO view_shared_assets (item_id, destination_kind, promoted_at)
                VALUES (@held, 'folder', @now);
                """, new { held, now = DateTimeOffset.UtcNow });
        }

        await sharedRepository.UpsertSourceAsync(folder with { ContentKind = ViewContentKinds.PersonalVideos });

        using var check = _database.CreateConnection();
        Assert.Equal(1, check.ExecuteScalar<long>("SELECT COUNT(*) FROM local_items WHERE id = @held;", new { held }));
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM local_items WHERE id = @loose;", new { loose }));
        var skipped = Assert.Single(await sharedRepository.GetSourcesAsync(), source => source.Id == folder.Id);
        Assert.Equal(ViewContentKinds.PersonalVideos, skipped.ContentKind);
        Assert.Equal(1, skipped.SkippedFileCount);
    }

    [Fact]
    public async Task ChangingASharedFoldersKindMovesItsAcceptedItemsToTheNewKind()
    {
        InsertProfile("Household member");
        var shared = await new ViewSharedLibraryRepository(_database).EnsureForHouseholdAsync(TestHousehold);
        var sharedRepository = new ViewSharedLibraryRepository(_database);
        var folder = await sharedRepository.UpsertSourceAsync(SharedFolder(shared.LibraryId, ViewContentKinds.Photos));
        var picture = await AddSharedAsync(shared.LibraryId, folder.Id, 'a', LocalAssetMediaKinds.Image);

        await sharedRepository.UpsertSourceAsync(folder with { ContentKind = ViewContentKinds.Documents });

        Assert.Equal(ViewContentKinds.Documents, _assets.GetContentKind(picture));
        Assert.Equal(0, Assert.Single(await sharedRepository.GetSourcesAsync()).SkippedFileCount);
    }

    [Fact]
    public async Task ChangingAnIntakeFoldersKindLeavesEachItemWithItsOwnKind()
    {
        var owner = await CreateOwnerAsync("Owner");
        var now = DateTimeOffset.UtcNow;
        var intake = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), owner.SpaceId, ViewSourceType.Folder, "Phone backup", ViewKindAcceptance.BrowserUploadSourceKey, null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: @"D:\Backup", ContentKind: ViewContentKinds.Photos));
        var clip = await AddInSourceAsync(owner, intake.Id, 'c', LocalAssetMediaKinds.Video, ViewContentKinds.PersonalVideos);
        var picture = await AddInSourceAsync(owner, intake.Id, '3', LocalAssetMediaKinds.Image, ViewContentKinds.Photos);

        await _spaces.UpsertSourceAsync(intake with { ContentKind = ViewContentKinds.Documents });

        Assert.Equal(ViewContentKinds.PersonalVideos, _assets.GetContentKind(clip));
        Assert.Equal(ViewContentKinds.Photos, _assets.GetContentKind(picture));
    }

    [Fact]
    public async Task AnItemRemovedByChangingAFoldersKindLeavesNoSearchTagOrGalleryRowsBehind()
    {
        var owner = await CreateOwnerAsync("Owner");
        var now = DateTimeOffset.UtcNow;
        var folder = await _spaces.UpsertSourceAsync(new ViewSource(
            Guid.NewGuid(), owner.SpaceId, ViewSourceType.Folder, "Pictures", "test:pictures", null, now, now,
            ViewSourceStorageMode.Linked, ExternalPath: @"D:\Pictures", ContentKind: ViewContentKinds.Photos));
        var picture = await AddInSourceAsync(owner, folder.Id, '3', LocalAssetMediaKinds.Image, ViewContentKinds.Photos);
        var tagId = Guid.NewGuid();
        var gallery = await new ViewGalleryRepository(_database).CreateAsync(
            new CreateViewGalleryCommand(owner.ProfileId, owner.SpaceId, "Trip", ViewGalleryKind.Manual));
        await new ViewGalleryRepository(_database).AddItemsAsync(gallery.Id, [picture]);
        long searchRowId;
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("INSERT INTO tags (id, name, created_at) VALUES (@tagId, 'holiday', @now);" +
                " INSERT INTO local_item_tag_links (item_id, tag_id, added_at) VALUES (@picture, @tagId, @now);",
                new { tagId, picture, now });
            searchRowId = connection.ExecuteScalar<long>(
                "SELECT rowid FROM local_item_search_keys WHERE item_id = @picture;", new { picture });
            Assert.Equal(1, connection.ExecuteScalar<long>(
                "SELECT COUNT(*) FROM local_item_search WHERE rowid = @searchRowId;", new { searchRowId }));
        }

        await _spaces.UpsertSourceAsync(folder with { ContentKind = ViewContentKinds.PersonalVideos });

        using var check = _database.CreateConnection();
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM local_items WHERE id = @picture;", new { picture }));
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM local_item_search WHERE rowid = @searchRowId;", new { searchRowId }));
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM local_item_tag_links WHERE item_id = @picture;", new { picture }));
        Assert.Equal(0, check.ExecuteScalar<long>("SELECT COUNT(*) FROM view_gallery_items WHERE item_id = @picture;", new { picture }));
        Assert.Equal(1, check.ExecuteScalar<long>("SELECT COUNT(*) FROM tags WHERE id = @tagId;", new { tagId }));
    }

    private static ViewSharedSource SharedFolder(Guid libraryId, string contentKind) => new(
        Guid.NewGuid(), libraryId, ViewSourceType.Folder, "Shared pictures", "test:shared", null,
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, ViewSourceStorageMode.Managed,
        RelativePath: "Shared/test", ContentKind: contentKind);

    private async Task<Guid> AddSharedAsync(Guid libraryId, Guid sourceId, char hash, string mediaKind) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            libraryId, null, null, mediaKind, $"Shared {hash}", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration($@"C:\shared\{hash}.bin", new string(hash, 64), $"{hash}.bin",
                "application/octet-stream", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared, ContentKind: ViewContentKinds.Photos))).ItemId;

    private async Task<Guid> AddInSourceAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, Guid sourceId, char hash, string mediaKind, string contentKind) =>
        (await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, mediaKind, $"Item {hash}", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration($@"C:\view\{hash}.bin", new string(hash, 64), $"{hash}.bin",
                "application/octet-stream", 1024, DateTimeOffset.UtcNow) with { SourceId = sourceId }],
            ContentKind: contentKind))).ItemId;

    private Task<LocalAssetUpsertResult> AddAsync(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner, char hash, string mediaKind,
        string? contentKind = null, Guid? addedBy = null) =>
        _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId, owner.SpaceId, owner.ProfileId, mediaKind, $"Item {hash}", DateTimeOffset.UtcNow,
            [new LocalAssetFileRegistration(
                Path.Combine(@"C:\view", owner.ProfileId.ToString("N"), $"{hash}.bin"),
                new string(hash, 64), $"{hash}.bin", "application/octet-stream", 1024, DateTimeOffset.UtcNow)],
            ContentKind: contentKind, AddedByProfileId: addedBy));

    private static readonly Guid TestHousehold = new("7e57a000-0000-0000-0000-000000000003");

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

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnerAsync(string name)
    {
        var profileId = InsertProfile(name);
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

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
