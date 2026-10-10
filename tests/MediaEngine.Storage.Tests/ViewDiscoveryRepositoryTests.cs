using Dapper;
using MediaEngine.Domain.PersonalMedia;
using MediaEngine.Storage.Contracts;
using MediaEngine.TestSupport;

namespace MediaEngine.Storage.Tests;

public sealed class ViewDiscoveryRepositoryTests : IDisposable
{
    private sealed class QueryPlanRow
    {
        public string Detail { get; init; } = string.Empty;
    }

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tuvima-view-discovery-{Guid.NewGuid():N}.db");
    private readonly DatabaseConnection _database;
    private readonly LocalAssetRepository _assets;
    private readonly ViewPersonalSpaceRepository _spaces;
    private readonly ViewDiscoveryRepository _discovery;
    private char _hash = '1';

    public ViewDiscoveryRepositoryTests()
    {
        _database = new DatabaseConnection(_path);
        _database.InitializeSchema();
        _assets = new LocalAssetRepository(_database);
        _spaces = new ViewPersonalSpaceRepository(_database);
        _discovery = new ViewDiscoveryRepository(_database);
    }

    [Fact]
    public async Task Places_UsesOnlyAuthorizedActiveVisibleAssetsAndSupportsCursorAndSearch()
    {
        var first = await CreateOwnership();
        var second = await CreateOwnership();
        var privateOwner = await CreateOwnership();
        var chicagoOne = await AddAsset(first, "Chicago one", 41.8781, -87.6298, "Chicago");
        await AddAsset(first, "Chicago two", 41.8790, -87.6301, "Chicago");
        await AddAsset(second, "Chicago three", 41.8800, -87.6310, "Chicago");
        await AddAsset(first, "Maui", 20.7984, -156.3319, "Maui");
        var hidden = await AddAsset(first, "Hidden Paris", 48.8566, 2.3522, "Paris");
        await _assets.SetFlagsAsync(hidden, favorite: null, hidden: true);
        var archived = await AddAsset(first, "Archived Rome", 41.9028, 12.4964, "Rome");
        await _assets.SetLifecycleStateAsync(archived, LocalAssetLifecycleState.Archived);
        await AddAsset(privateOwner, "Private Tokyo", 35.6762, 139.6503, "Tokyo");

        var firstPage = _discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(
            [first.LibraryId, second.LibraryId], Limit: 1));

        var chicago = Assert.Single(firstPage.Items);
        Assert.StartsWith("chicago@", chicago.Key, StringComparison.Ordinal);
        Assert.Equal(3, chicago.AssetCount);
        Assert.True(firstPage.HasMore);
        Assert.NotNull(firstPage.NextCursor);
        Assert.NotEqual(Guid.Empty, chicago.RepresentativeAssetId);
        Assert.NotEqual(chicagoOne, Guid.Empty);

        var secondPage = _discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(
            [first.LibraryId, second.LibraryId], Limit: 10, Cursor: firstPage.NextCursor));
        Assert.StartsWith("maui@", Assert.Single(secondPage.Items).Key, StringComparison.Ordinal);
        Assert.DoesNotContain(secondPage.Items, place => place.Name is "Paris" or "Rome" or "Tokyo");

        var search = _discovery.QueryPlaces(new ViewPlaceDiscoveryQuery(
            [first.LibraryId], Search: "Mau"));
        Assert.Equal("Maui", Assert.Single(search.Items).Name);
        Assert.True(search.HasEligibleData);
    }

    [Fact]
    public async Task People_RequiresNamedOrReviewedIdentityEvidenceAndRetainsProvenance()
    {
        var owner = await CreateOwnership();
        var privateOwner = await CreateOwnership();
        var alice = await AddAsset(owner, "Alice photo", null, null, null);
        var bob = await AddAsset(owner, "Bob photo", null, null, null);
        var unreviewed = await AddAsset(owner, "Unreviewed face", null, null, null);
        var ignored = await AddAsset(owner, "Object label", null, null, null);
        var privateAsset = await AddAsset(privateOwner, "Private person", null, null, null);
        await _assets.AddAnnotationAsync(alice, new LocalAssetAnnotation(
            "person_name", "Alice", "user", ProvenanceJson: "{\"method\":\"manual\"}"));
        await _assets.AddAnnotationAsync(bob, new LocalAssetAnnotation(
            "face_identity", "Bob", "review-tool", Confidence: 0.98, ReviewedAt: DateTimeOffset.UtcNow));
        await _assets.AddAnnotationAsync(unreviewed, new LocalAssetAnnotation(
            "face_identity", "Unreviewed", "future-face-worker", Confidence: 0.60));
        await _assets.AddAnnotationAsync(ignored, new LocalAssetAnnotation(
            "object_label", "Person-shaped object", "metadata"));
        await _assets.AddAnnotationAsync(privateAsset, new LocalAssetAnnotation(
            "person_name", "Private Eve", "user"));

        var page = _discovery.QueryPeople(new ViewPeopleDiscoveryQuery([owner.LibraryId]));

        Assert.Equal(2, page.Items.Count);
        var aliceResult = Assert.Single(page.Items, person => person.DisplayName == "Alice");
        Assert.Contains("person_name", aliceResult.AnnotationKinds);
        Assert.Contains("user", aliceResult.ProvenanceSources);
        Assert.False(aliceResult.HasReviewedEvidence);
        var bobResult = Assert.Single(page.Items, person => person.DisplayName == "Bob");
        Assert.True(bobResult.HasReviewedEvidence);
        Assert.Contains("face_identity", bobResult.AnnotationKinds);
        Assert.DoesNotContain(page.Items, person => person.DisplayName is "Unreviewed" or "Private Eve");
    }

    [Fact]
    public async Task Atlas_ReturnsTruthfulFacetsAndDateOrderedPlaceStoryMedia()
    {
        var owner = await CreateOwnership();
        var privateOwner = await CreateOwnership();
        var newest = await AddAsset(owner, "Recent Chicago", 41.8781, -87.6298, "Chicago", new DateTimeOffset(2025, 8, 2, 12, 0, 0, TimeSpan.Zero));
        var older = await AddAsset(owner, "Older Chicago", 41.8784, -87.6301, "Chicago", new DateTimeOffset(2022, 5, 1, 12, 0, 0, TimeSpan.Zero), LocalAssetMediaKinds.Video);
        await AddAsset(owner, "Unmapped", null, null, null, new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero));
        await AddAsset(privateOwner, "Private London", 51.5072, -.1276, "London", new DateTimeOffset(2025, 2, 1, 12, 0, 0, TimeSpan.Zero));

        var atlas = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery([owner.LibraryId]));

        var hotspot = Assert.Single(atlas.Hotspots);
        Assert.Equal(2, hotspot.AssetCount);
        Assert.Equal(1, hotspot.ImageCount);
        Assert.Equal(1, hotspot.VideoCount);
        Assert.Equal(2, atlas.MappedAssetCount);
        Assert.Equal(1, atlas.ImageCount);
        Assert.Equal(1, atlas.VideoCount);
        Assert.Equal(1, atlas.UnmappedAssetCount);
        Assert.Equal([2025, 2022], atlas.AvailableYears);
        Assert.Equal(2, atlas.Timeline?.Count);
        Assert.Equal(2, atlas.Timeline?.Sum(bucket => bucket.AssetCount));

        var filtered = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery([owner.LibraryId], Year: 2025));
        Assert.Equal(1, Assert.Single(filtered.Hotspots).AssetCount);
        Assert.Equal(1, filtered.ImageCount);
        Assert.Equal(0, filtered.VideoCount);
        var noMatch = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery([owner.LibraryId], Search: "Not a real place"));
        Assert.Equal(0, noMatch.MappedAssetCount);
        Assert.Equal(0, noMatch.ImageCount);
        Assert.Equal(0, noMatch.VideoCount);

        var ranged = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery(
            [owner.LibraryId], From: new DateTimeOffset(2022, 1, 1, 0, 0, 0, TimeSpan.Zero),
            To: new DateTimeOffset(2022, 12, 31, 23, 59, 59, TimeSpan.Zero)));
        Assert.Equal(1, Assert.Single(ranged.Hotspots).AssetCount);
        Assert.Equal(2, ranged.Timeline?.Sum(bucket => bucket.AssetCount));

        Assert.True(await _assets.SetFlagsAsync(older, favorite: true, hidden: null));
        var favorites = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery([owner.LibraryId], FavoritesOnly: true));
        Assert.Equal(1, Assert.Single(favorites.Hotspots).AssetCount);
        Assert.Equal(1, favorites.Timeline?.Sum(bucket => bucket.AssetCount));

        var story = _discovery.QueryPlaceAssets(new ViewPlaceAssetDiscoveryQuery(
            [owner.LibraryId], hotspot.Key));
        Assert.Equal([newest, older], story.AssetIds);
        Assert.Equal(2, story.Total);
        Assert.False(story.HasMore);
    }

    [Fact]
    public void Schema_HasDiscoveryScopeAndEvidenceIndexes()
    {
        using var connection = _database.CreateConnection();
        var indexes = connection.Query<string>("""
            SELECT name FROM sqlite_master
             WHERE type = 'index' AND name LIKE 'ix_%discovery'
                OR type = 'index' AND name = 'ix_local_item_annotations_people';
            """).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("ix_local_items_active_discovery", indexes);
        Assert.Contains("ix_local_item_annotations_people", indexes);
        Assert.True(connection.QuerySingle<bool>("SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = 'ix_local_items_library_atlas_timeline');"));

        var plan = connection.Query<QueryPlanRow>("""
            EXPLAIN QUERY PLAN
            SELECT li.id
              FROM local_items li
              JOIN local_item_metadata lm ON lm.item_id = li.id
             WHERE li.library_id = zeroblob(16)
               AND li.hidden = 0 AND li.archived_at IS NULL AND li.trashed_at IS NULL
               AND lm.latitude IS NOT NULL AND lm.longitude IS NOT NULL;
            """).ToList();
        Assert.Contains(plan, step => step.Detail.Contains("ix_local_items_active_discovery", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LargeIsolatedFixture_KeepsAllCountsAndPagesBeyondFiveHundred()
    {
        var owner = await CreateOwnership();
        var start = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        for (var i = 0; i < 520; i++)
        {
            await AddAsset(owner, $"Missing {i}", null, null, null, start.AddDays(i));
        }
        for (var i = 0; i < 200; i++)
        {
            await AddAsset(owner, $"Mapped {i}", 47.6062, -122.3321, "Seattle", start.AddDays(i * 45), i % 5 == 0 ? "video" : "image");
        }
        var atlas = _discovery.QueryAtlas(new ViewAtlasDiscoveryQuery([owner.LibraryId], TimelineResolution: "day"));
        Assert.Equal(520, atlas.UnmappedAssetCount);
        Assert.Equal(200, atlas.MappedAssetCount);
        Assert.InRange(atlas.Timeline!.Count, 1, 180);
        Assert.Equal(200, atlas.Timeline.Sum(bucket => bucket.AssetCount));
        Assert.All(atlas.Timeline, bucket => Assert.True(bucket.End > bucket.Start));
        var ids = new HashSet<Guid>();
        LocalAssetTimelineCursor? cursor = null;
        do
        {
            var page = _assets.QueryTimeline(new LocalAssetTimelineQuery([owner.LibraryId], Limit: 100,
                BeforeEffectiveAt: cursor?.EffectiveAt, BeforeItemId: cursor?.ItemId, WithoutLocation: true));
            foreach (var item in page.Items)
            {
                Assert.True(ids.Add(item.Id));
            }
            cursor = page.NextCursor;
        } while (cursor is not null);
        Assert.Equal(520, ids.Count);
    }

    private async Task<(Guid ProfileId, Guid SpaceId, Guid LibraryId)> CreateOwnership()
    {
        var profileId = Guid.NewGuid();
        var libraryId = Guid.NewGuid();
        using (var connection = _database.CreateConnection())
        {
            connection.Execute("""
                INSERT INTO profiles (id, display_name, avatar_color, role, created_at)
                VALUES (@profileId, @name, '#7C4DFF', 'RestrictedProfile', @now);
                """, new { profileId, name = $"Profile {profileId:N}", now = DateTimeOffset.UtcNow });
        }
        var space = await _spaces.CreateAsync(profileId, libraryId);
        return (profileId, space.Id, libraryId);
    }

    private async Task<Guid> AddAsset(
        (Guid ProfileId, Guid SpaceId, Guid LibraryId) owner,
        string title,
        double? latitude,
        double? longitude,
        string? locationName,
        DateTimeOffset? capturedAt = null,
        string mediaKind = LocalAssetMediaKinds.Image)
    {
        var hashCharacter = _hash++;
        var result = await _assets.UpsertAsync(new LocalAssetRegistration(
            owner.LibraryId,
            owner.SpaceId,
            owner.ProfileId,
            mediaKind,
            title,
            capturedAt ?? DateTimeOffset.UtcNow.AddMinutes(-_hash),
            [new LocalAssetFileRegistration(
                $@"C:\personal\{title}.jpg",
                ((int)hashCharacter).ToString("x64"),
                $"{title}.jpg",
                "image/jpeg",
                1024,
                DateTimeOffset.UtcNow)],
            Latitude: latitude,
            Longitude: longitude,
            LocationName: locationName));
        return result.ItemId;
    }

    public void Dispose()
    {
        _database.Dispose();
        TestTemp.DeleteDatabase(_path);
    }
}
