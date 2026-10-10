using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class LocalAssetRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(
        Path.GetTempPath(),
        $"tuvima-local-assets-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _repository;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewGalleryRepository _galleries;

    public LocalAssetRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _repository = new LocalAssetRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _galleries = new ViewGalleryRepository(_database);
    }

    [Fact]
    public async Task Upsert_SeparatesLogicalItemFilesAndExactDuplicateSources()
    {
        var libraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        var capturedAt = new DateTimeOffset(2026, 8, 19, 19, 32, 0, TimeSpan.Zero);
        var first = await _repository.UpsertAsync(new LocalAssetRegistration(
            libraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Image,
            "Lake at sunset",
            capturedAt,
            [
                File(@"C:\personal\IMG_1000.HEIC", Hash('a'), "IMG_1000.HEIC", "image/heic"),
                File(
                    @"C:\personal\IMG_1000.MOV",
                    Hash('b'),
                    "IMG_1000.MOV",
                    "video/quicktime",
                    LocalAssetFileRoles.LivePhotoVideo),
            ],
            Width: 4032,
            Height: 3024,
            DurationSeconds: 2.7,
            DeviceMake: "Apple",
            DeviceModel: "iPhone 15 Pro",
            Latitude: 41.8781,
            Longitude: -87.6298,
            LocationName: "Chicago",
            Tags: ["lake", "vacation"]));

        var duplicate = await _repository.UpsertAsync(new LocalAssetRegistration(
            libraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Image,
            null,
            capturedAt,
            [File(@"D:\backup\IMG_1000 copy.HEIC", Hash('a'), "IMG_1000 copy.HEIC", "image/heic")]));
        var otherLibraryId = Guid.NewGuid();
        var otherOwner = await CreateOwnership(otherLibraryId);
        var otherLibrary = await _repository.UpsertAsync(new LocalAssetRegistration(
            otherLibraryId,
            otherOwner.SpaceId,
            otherOwner.ProfileId,
            LocalAssetMediaKinds.Image,
            "Shared bytes, separate library identity",
            capturedAt,
            [File(@"E:\shared\IMG_1000.HEIC", Hash('a'), "IMG_1000.HEIC", "image/heic")]));

        Assert.True(first.ItemAdded);
        Assert.Equal(2, first.FilesAdded);
        Assert.Equal(2, first.SourcesAdded);
        Assert.False(duplicate.ItemAdded);
        Assert.Equal(first.ItemId, duplicate.ItemId);
        Assert.Equal(0, duplicate.FilesAdded);
        Assert.Equal(1, duplicate.SourcesAdded);
        Assert.True(otherLibrary.ItemAdded);
        Assert.NotEqual(first.ItemId, otherLibrary.ItemId);
        Assert.Equal(0, otherLibrary.FilesAdded);

        var item = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(
            _repository.Find(first.ItemId));
        Assert.Equal(3, item.SourceCount);
        Assert.Equal(2, item.Files.Count);
        Assert.Equal(2, Assert.Single(item.Files, file => file.Role == LocalAssetFileRoles.Primary).SourceCount);
        Assert.Contains(item.Files, file => file.Role == LocalAssetFileRoles.LivePhotoVideo);
        Assert.Equal(["lake", "vacation"], item.Tags);
        Assert.Equal("Chicago", item.LocationName);

        var primary = Assert.IsType<LocalAssetContentLocation>(
            _repository.ResolveContent(first.ItemId));
        Assert.Equal(@"D:\backup\IMG_1000 copy.HEIC", primary.FilePath);
        Assert.Equal(Hash('a'), primary.ContentHash);
        var motion = Assert.IsType<LocalAssetContentLocation>(
            _repository.ResolveContent(first.ItemId, LocalAssetFileRoles.LivePhotoVideo));
        Assert.Equal("video/quicktime", motion.MimeType);
        Assert.Equal(1, _repository.Find(otherLibrary.ItemId)!.SourceCount);
        Assert.Equal(@"E:\shared\IMG_1000.HEIC", _repository.ResolveContent(otherLibrary.ItemId)!.FilePath);

        using var connection = _database.CreateConnection();
        Assert.Equal("blob", connection.QuerySingle<string>(
            "SELECT typeof(id) FROM local_items WHERE id = @id;", new { id = first.ItemId }));
        Assert.Equal("blob", connection.QuerySingle<string>(
            "SELECT typeof(library_id) FROM local_items WHERE id = @id;", new { id = first.ItemId }));
        Assert.Equal("blob", connection.QuerySingle<string>(
            "SELECT typeof(item_id) FROM local_item_search_keys WHERE item_id = @id;", new { id = first.ItemId }));
        Assert.Equal(2, connection.QuerySingle<int>("SELECT COUNT(*) FROM local_files;"));
        Assert.Equal(4, connection.QuerySingle<int>("SELECT COUNT(*) FROM local_file_sources;"));
    }

    [Fact]
    public async Task Search_IndexesNormalizedMetadataDocumentTextAndTagsWithinLibrary()
    {
        var libraryId = Guid.NewGuid();
        var otherLibraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        var otherOwner = await CreateOwnership(otherLibraryId);
        var document = await _repository.UpsertAsync(new LocalAssetRegistration(
            libraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Document,
            "Road Trip Plan",
            new DateTimeOffset(2026, 8, 24, 0, 0, 0, TimeSpan.Zero),
            [File(@"C:\docs\road-trip.pdf", Hash('c'), "road-trip.pdf", "application/pdf")],
            PageCount: 12,
            DeviceMake: "Microsoft",
            DeviceModel: "Word PDF Export",
            LocationName: "Yellowstone National Park",
            DocumentText: "Quarterly revenue is unrelated filler text.",
            Tags: ["Summer Vacation", "Itinerary"]));
        await _repository.UpsertAsync(new LocalAssetRegistration(
            otherLibraryId,
            otherOwner.SpaceId,
            otherOwner.ProfileId,
            LocalAssetMediaKinds.Document,
            "Road Trip Plan Secret Copy",
            null,
            [File(@"D:\private\road-trip.pdf", Hash('d'), "road-trip.pdf", "application/pdf")],
            DocumentText: "Quarterly revenue"));

        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "road trip")).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "quarterly revenue")).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "Yellowstone")).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "summer vac")).Items);
        Assert.Empty(_repository.Query(new LocalAssetQuery(libraryId, Search: "secret")).Items);
        Assert.Empty(_repository.Query(new LocalAssetQuery(
            libraryId,
            MediaKinds: [LocalAssetMediaKinds.Image])).Items);

        await _repository.ReplaceTagsAsync(document.ItemId, ["Budget"]);
        Assert.Empty(_repository.Query(new LocalAssetQuery(libraryId, Search: "itinerary")).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "budget")).Items);
    }

    [Fact]
    public async Task TimelineIndexAndAnchor_ReturnRealFilteredCalendarBuckets()
    {
        var libraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        foreach (var (suffix, date) in new[]
                 {
                     ('1', new DateTimeOffset(2026, 2, 15, 10, 0, 0, TimeSpan.Zero)),
                     ('2', new DateTimeOffset(2026, 2, 1, 10, 0, 0, TimeSpan.Zero)),
                     ('3', new DateTimeOffset(2026, 1, 20, 10, 0, 0, TimeSpan.Zero)),
                 })
        {
            await _repository.UpsertAsync(new LocalAssetRegistration(libraryId, owner.SpaceId,
                owner.ProfileId, LocalAssetMediaKinds.Image, $"Photo {suffix}", date,
                [File($@"C:\photos\{suffix}.jpg", Hash(suffix), $"{suffix}.jpg", "image/jpeg")]));
        }

        var buckets = _repository.QueryTimelineIndex(new LocalAssetTimelineQuery([libraryId]));
        Assert.Collection(buckets,
            february => { Assert.Equal((2026, 2, 2), (february.Year, february.Month, february.AssetCount)); },
            january => { Assert.Equal((2026, 1, 1), (january.Year, january.Month, january.AssetCount)); });

        var anchored = _repository.QueryTimeline(new LocalAssetTimelineQuery([libraryId],
            AnchorBefore: new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Photo 3", Assert.Single(anchored.Items).Title);
    }

    [Fact]
    public async Task ManualMetadataEdits_AreSearchableAndSurviveEmbeddedLocationRefresh()
    {
        var libraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        var registration = new LocalAssetRegistration(libraryId, owner.SpaceId, owner.ProfileId,
            LocalAssetMediaKinds.Image, "Trail", DateTimeOffset.UtcNow,
            [File(@"C:\photos\trail.jpg", Hash('8'), "trail.jpg", "image/jpeg")],
            Latitude: 40.1, Longitude: -105.1, LocationName: "Camera location");
        var result = await _repository.UpsertAsync(registration);

        Assert.True(await _repository.UpdateDescriptionAsync(result.ItemId, "Sunrise above the ridge"));
        await _repository.ReplaceTagsAsync(result.ItemId, ["hiking", "Colorado"]);
        Assert.True(await _repository.UpdateLocationAsync(result.ItemId,
            new(39.7392, -104.9903, "Civic Center", "Denver", "Colorado", "United States", "us")));
        await _repository.UpsertAsync(registration with { Latitude = 41.5, Longitude = -106.5, LocationName = "Refreshed camera location" });

        var edited = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(_repository.Find(result.ItemId));
        Assert.Equal("Sunrise above the ridge", edited.Description);
        Assert.Equal("Denver", edited.LocationCity);
        Assert.Equal("US", edited.LocationCountryCode);
        Assert.True(edited.LocationUserOverride);
        Assert.Equal(39.7392, edited.Latitude);
        Assert.Equal(41.5, edited.EmbeddedLatitude);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "sunrise ridge")).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "Denver Colorado")).Items);

        Assert.True(await _repository.UpdateLocationAsync(result.ItemId, new(null, null, ResetToEmbedded: true)));
        var reset = _repository.Find(result.ItemId)!;
        Assert.False(reset.LocationUserOverride);
        Assert.Equal(41.5, reset.Latitude);
        Assert.Equal(-106.5, reset.Longitude);
    }

    [Fact]
    public async Task ManualCapturedDateAndPeople_SurviveRefreshAndRemainSearchable()
    {
        var libraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        var embedded = new DateTimeOffset(2025, 1, 2, 10, 0, 0, TimeSpan.Zero);
        var corrected = new DateTimeOffset(2024, 12, 25, 18, 30, 0, TimeSpan.Zero);
        var registration = new LocalAssetRegistration(libraryId, owner.SpaceId, owner.ProfileId,
            LocalAssetMediaKinds.Image, "Dinner", embedded,
            [File(@"C:\photos\dinner.jpg", Hash('9'), "dinner.jpg", "image/jpeg")]);
        var result = await _repository.UpsertAsync(registration);

        Assert.True(await _repository.UpdateCapturedAtAsync(result.ItemId, corrected, false));
        await _repository.ReplacePeopleAsync(result.ItemId, ["Naomi Nagata"]);
        await _repository.UpsertAsync(registration with { CapturedAt = embedded.AddDays(2) });

        var edited = _repository.Find(result.ItemId)!;
        Assert.Equal(corrected, edited.CapturedAt);
        Assert.Equal(embedded.AddDays(2), edited.EmbeddedCapturedAt);
        Assert.True(edited.CapturedAtUserOverride);
        Assert.Equal(["Naomi Nagata"], edited.People);
        Assert.Single(_repository.Query(new LocalAssetQuery(libraryId, Search: "Naomi")).Items);

        Assert.True(await _repository.UpdateCapturedAtAsync(result.ItemId, null, true));
        var reset = _repository.Find(result.ItemId)!;
        Assert.False(reset.CapturedAtUserOverride);
        Assert.Equal(embedded.AddDays(2), reset.CapturedAt);
    }

    [Fact]
    public async Task FlagsCollectionsAndAnnotations_PreserveLibraryIsolationAndProvenance()
    {
        var libraryId = Guid.NewGuid();
        var otherLibraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        var otherOwner = await CreateOwnership(otherLibraryId);
        var first = await AddImage(owner, libraryId, 'e', "family.jpg");
        var other = await AddImage(otherOwner, otherLibraryId, 'f', "private.jpg");

        Assert.True(await _repository.SetFlagsAsync(first.ItemId, favorite: true, hidden: true));
        Assert.Empty(_repository.Query(new LocalAssetQuery(libraryId)).Items);
        Assert.Single(_repository.Query(new LocalAssetQuery(
            libraryId,
            FavoritesOnly: true,
            IncludeHidden: true,
            HiddenOnly: true)).Items);
        Assert.Empty(_repository.QueryTimeline(new LocalAssetTimelineQuery([libraryId])).Items);
        Assert.Equal(first.ItemId, Assert.Single(_repository.QueryTimeline(new LocalAssetTimelineQuery(
            [libraryId],
            FavoritesOnly: true,
            IncludeHidden: true,
            HiddenOnly: true)).Items).Id);

        var gallery = await _galleries.CreateAsync(new CreateViewGalleryCommand(
            owner.ProfileId, owner.SpaceId, "Family", ViewGalleryKind.Manual,
            "Our favorite media", CoverItemId: first.ItemId));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _galleries.AddItemsAsync(gallery.Id, [first.ItemId, other.ItemId]));
        var addResult = await _galleries.AddItemsAsync(gallery.Id, [first.ItemId]);
        Assert.Equal(1, addResult.Added);
        var refreshed = Assert.Single(await _galleries.GetOwnedAsync(owner.ProfileId));
        Assert.Equal(1, refreshed.ItemCount);
        Assert.Equal(first.ItemId, refreshed.CoverItemId);
        Assert.Single(_repository.Query(new LocalAssetQuery(
            libraryId,
            IncludeHidden: true,
            GalleryId: gallery.Id)).Items);

        var annotationId = await _repository.AddAnnotationAsync(first.ItemId, new LocalAssetAnnotation(
            "object_label",
            "dog",
            "future-inference-worker",
            Confidence: 0.92,
            ModelName: "placeholder-model",
            ModelVersion: "v1",
            ProvenanceJson: "{\"frame\":12}"));

        using var connection = _database.CreateConnection();
        var stored = connection.QuerySingle<(string Source, double Confidence, string Provenance)>("""
            SELECT source AS Source, confidence AS Confidence, provenance_json AS Provenance
              FROM local_item_annotations WHERE id = @annotationId;
            """, new { annotationId });
        Assert.Equal("future-inference-worker", stored.Source);
        Assert.Equal(0.92, stored.Confidence);
        Assert.Equal("{\"frame\":12}", stored.Provenance);
        Assert.Equal("blob", connection.QuerySingle<string>(
            "SELECT typeof(item_id) FROM local_item_annotations WHERE id = @annotationId;",
            new { annotationId }));
    }

    [Fact]
    public async Task InvalidHashesRolesCoordinatesAndDerivativeMetadata_FailBeforeWriting()
    {
        var libraryId = Guid.NewGuid();
        var owner = await CreateOwnership(libraryId);
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertAsync(
            new LocalAssetRegistration(
                libraryId,
                owner.SpaceId,
                owner.ProfileId,
                LocalAssetMediaKinds.Image,
                null,
                null,
                [File(@"C:\bad.jpg", "not-a-sha256", "bad.jpg", "image/jpeg")])));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.UpsertAsync(
            new LocalAssetRegistration(
                libraryId,
                owner.SpaceId,
                owner.ProfileId,
                "spreadsheet",
                null,
                null,
                [File(@"C:\bad.xlsx", Hash('1'), "bad.xlsx", "application/vnd.ms-excel")])));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _repository.UpsertAsync(
            new LocalAssetRegistration(
                libraryId,
                owner.SpaceId,
                owner.ProfileId,
                LocalAssetMediaKinds.Image,
                null,
                null,
                [File(@"C:\bad.jpg", Hash('2'), "bad.jpg", "image/jpeg")],
                Latitude: 91)));
        await Assert.ThrowsAsync<ArgumentException>(() => _repository.UpsertAsync(
            new LocalAssetRegistration(
                libraryId,
                owner.SpaceId,
                owner.ProfileId,
                LocalAssetMediaKinds.Image,
                null,
                null,
                [File(
                    @"C:\preview.jpg",
                    Hash('3'),
                    "preview.jpg",
                    "image/jpeg",
                    LocalAssetFileRoles.Derivative)])));

        using var connection = _database.CreateConnection();
        Assert.Equal(0, connection.QuerySingle<int>("SELECT COUNT(*) FROM local_items;"));
    }

    [Fact]
    public async Task SharedRegistration_PersistsServerScopeAndRejectsPersonalSource()
    {
        var sharedLibraryId = (await new ViewSharedLibraryRepository(_database).GetAsync()).LibraryId;
        var sharedSourceId = Guid.NewGuid();
        var personalLibraryId = Guid.NewGuid();
        var personalOwner = await CreateOwnership(personalLibraryId);
        var personalSourceId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO view_sources
                    (id,scope_kind,personal_space_id,library_id,source_type,name,source_key,
                     storage_mode,relative_path,include_subdirectories,enabled,created_at,updated_at)
                VALUES
                    (@sharedSourceId,'shared',NULL,@sharedLibraryId,'folder','Shared Timeline','shared:timeline',
                     'managed','Shared/Timeline',1,1,@now,@now),
                    (@personalSourceId,'personal',@personalSpaceId,@personalLibraryId,'folder','Private',NULL,
                     'managed','Private',1,1,@now,@now);
                """, new
            {
                sharedSourceId,
                sharedLibraryId,
                personalSourceId,
                personalSpaceId = personalOwner.SpaceId,
                personalLibraryId,
                now = DateTimeOffset.UtcNow,
            });
        }

        var shared = await _repository.UpsertAsync(new LocalAssetRegistration(
            sharedLibraryId,
            null,
            null,
            LocalAssetMediaKinds.Image,
            "Accepted copy",
            DateTimeOffset.UtcNow,
            [File(@"C:\shared\accepted.jpg", Hash('9'), "accepted.jpg", "image/jpeg") with
                { SourceId = sharedSourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared));

        var item = Assert.IsType<MediaEngine.Contracts.LocalAssets.LocalAssetDto>(_repository.Find(shared.ItemId));
        Assert.Equal(LocalAssetScopeKinds.Shared, item.ScopeKind);
        Assert.Null(item.PersonalSpaceId);
        Assert.Null(item.OwnerProfileId);
        var content = Assert.IsType<LocalAssetContentLocation>(_repository.ResolveContent(shared.ItemId));
        Assert.Equal(sharedLibraryId, content.LibraryId);
        Assert.Equal(sharedSourceId, content.SourceId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => _repository.UpsertAsync(new LocalAssetRegistration(
            sharedLibraryId,
            null,
            null,
            LocalAssetMediaKinds.Image,
            "Invalid private source",
            DateTimeOffset.UtcNow,
            [File(@"C:\private\leak.jpg", Hash('8'), "leak.jpg", "image/jpeg") with
                { SourceId = personalSourceId }],
            ScopeKind: LocalAssetScopeKinds.Shared)));
    }

    [Fact]
    public async Task Timeline_WithoutLocationAndPersonFilters_AreScopedAndCursorPaged()
    {
        var library = Guid.NewGuid();
        var owner = await CreateOwnership(library);
        var first = await AddImage(owner, library, 'a', "one.jpg");
        var second = await AddImage(owner, library, 'b', "two.jpg");
        var mapped = await AddImage(owner, library, 'c', "mapped.jpg");
        await _repository.UpdateLocationAsync(mapped.ItemId, new LocalAssetLocationUpdate(0, 0, "Equator", null, null, null, null, false));
        await _repository.ReplacePeopleAsync(first.ItemId, ["Alex"]);
        await _repository.ReplacePeopleAsync(second.ItemId, ["Alexandra"]);
        var query = new LocalAssetTimelineQuery([library], Limit: 1, WithoutLocation: true);
        var page = _repository.QueryTimeline(query);
        Assert.Single(page.Items);
        Assert.True(page.HasMore);
        var next = _repository.QueryTimeline(query with { BeforeEffectiveAt = page.NextCursor!.EffectiveAt, BeforeItemId = page.NextCursor.ItemId });
        Assert.Single(next.Items);
        Assert.NotEqual(page.Items[0].Id, next.Items[0].Id);
        Assert.DoesNotContain(mapped.ItemId, page.Items.Concat(next.Items).Select(item => item.Id));
        var person = _repository.QueryTimeline(new LocalAssetTimelineQuery([library], PersonKey: "alex"));
        Assert.Equal(first.ItemId, Assert.Single(person.Items).Id);
        Assert.Empty(_repository.QueryTimeline(new LocalAssetTimelineQuery([Guid.NewGuid()], PersonKey: "alex")).Items);
        Assert.Empty(_repository.QueryTimeline(query with { From = DateTimeOffset.UtcNow.AddDays(1) }).Items);
    }

    [Fact]
    public async Task AddedAtPagingUsesAdditionRatherThanCaptureAndKeepsGuidTies()
    {
        var library = Guid.NewGuid(); var owner = await CreateOwnership(library);
        var otherLibrary = Guid.NewGuid(); var otherOwner = await CreateOwnership(otherLibrary);
        var added = new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
        var ids = new List<Guid>();
        for (var n = 0; n < 6; n++)
        {
            var item = await AddImage(owner, library, (char)('a' + n), $"tie{n}.jpg"); ids.Add(item.ItemId);
            using var connection = _database.CreateConnection();
            connection.Execute("UPDATE local_items SET created_at=@added, captured_at=@capture WHERE id=@id;", new { added, capture = added.AddYears(n - 10), id = item.ItemId });
        }
        await AddImage(otherOwner, otherLibrary, '9', "private.jpg");
        var first = _repository.QueryTimeline(new LocalAssetTimelineQuery([library], Limit: 2, SortByAddedAt: true));
        Assert.True(first.HasMore);
        var seen = first.Items.ToList();
        while (seen.Count < ids.Count)
        {
            var last = seen.Last();
            var page = _repository.QueryTimeline(new LocalAssetTimelineQuery([library], Limit: 2, SortByAddedAt: true, AddedBefore: last.CreatedAt, AddedAfterKey: "view:" + last.Id.ToString("N")));
            Assert.NotEmpty(page.Items); seen.AddRange(page.Items);
        }
        Assert.Equal(ids.OrderBy(id => id.ToString("N"), StringComparer.Ordinal), seen.Select(i => i.Id));
        Assert.Equal(ids.Count, seen.Select(i => i.Id).Distinct().Count());
        Assert.All(seen, i => Assert.Equal(added, i.CreatedAt));
        var afterCatalogue = _repository.QueryTimeline(new LocalAssetTimelineQuery([library], SortByAddedAt: true, AddedBefore: added, AddedAfterKey: "catalogue:ffffffffffffffffffffffffffffffff"));
        Assert.Equal(ids.Count, afterCatalogue.Items.Count);
        var captureTimeline = _repository.QueryTimeline(new LocalAssetTimelineQuery([library]));
        Assert.Equal(ids.Last(), captureTimeline.Items.First().Id);
    }

    private Task<LocalAssetUpsertResult> AddImage(
        (Guid ProfileId, Guid SpaceId) owner,
        Guid libraryId,
        char hashCharacter,
        string fileName) =>
        _repository.UpsertAsync(new LocalAssetRegistration(
            libraryId,
            owner.SpaceId,
            owner.ProfileId,
            LocalAssetMediaKinds.Image,
            Path.GetFileNameWithoutExtension(fileName),
            DateTimeOffset.UtcNow,
            [File(Path.Combine(@"C:\personal", fileName), Hash(hashCharacter), fileName, "image/jpeg")]));

    private async Task<(Guid ProfileId, Guid SpaceId)> CreateOwnership(Guid libraryId)
    {
        var profileId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, @name, '#7C4DFF', 'RestrictedProfile', @now);
                """, new { profileId, name = $"Profile {profileId:N}", now = DateTimeOffset.UtcNow });
        }
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id);
    }

    private static LocalAssetFileRegistration File(
        string path,
        string hash,
        string fileName,
        string mimeType,
        string role = LocalAssetFileRoles.Primary) =>
        new(path, hash, fileName, mimeType, 1024, DateTimeOffset.UtcNow, role);

    private static string Hash(char character) => new(character, 64);

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }
}
