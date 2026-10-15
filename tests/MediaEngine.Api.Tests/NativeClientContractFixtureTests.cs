using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
using MediaEngine.Contracts.Progress;
using MediaEngine.Contracts.Universe;

namespace MediaEngine.Api.Tests;

public sealed class NativeClientContractFixtureTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    [Fact]
    public void CheckedInV1Fixtures_MatchThePublishedContractTypes()
    {
        var discovery = Read<TuvimaDiscoveryResponse>("discovery.json");
        var authorization = Read<DeviceAuthorizationResponse>("device-authorization.json");
        var token = Read<OAuthTokenResponse>("oauth-token.json");
        var manifest = Read<PlaybackManifestDto>("playback-manifest-hls.json");

        Assert.Contains("1", discovery.SupportedApiVersions);
        Assert.Equal(600, authorization.ExpiresIn);
        Assert.Equal("Bearer", token.TokenType);
        Assert.Equal(PlaybackDeliveryModes.Hls, manifest.RecommendedDelivery);
        Assert.Equal("ready", manifest.HlsStatus);
        Assert.NotNull(manifest.Resume);
    }

    [Fact]
    public void CollectionAndUniverseFixtures_MatchThePublishedContractTypes()
    {
        var collections = Read<PagedResponse<CollectionManagementCatalogDto>>("collections.json");
        var items = Read<PagedResponse<CollectionItemDto>>("collection-items.json");
        var universe = Read<UniverseClientDetailDto>("universe.json");

        Assert.True(collections.HasMore);
        Assert.Equal("2", collections.NextCursor);
        Assert.Equal(3, collections.TotalCount);
        Assert.All(collections.Items, entry =>
        {
            // Both entries in the fixture are Universes: they carry the QID that opens /api/v1/universe/{qid}.
            Assert.Equal(CollectionGroupTypes.Universe, entry.GroupType);
            Assert.Matches("^Q[0-9]+$", entry.UniverseQid);
            Assert.True(entry.ItemCount > 0);
            // Apps only read: authoring fields never reach them.
            Assert.Null(entry.RuleJson);
            Assert.Empty(entry.SelectedProfileIds);
            Assert.False(entry.CanEdit);
        });
        Assert.True(items.HasMore);
        Assert.Equal("2", items.NextCursor);
        Assert.Equal(2, items.Items.Count);
        Assert.All(items.Items, item => Assert.NotEqual(Guid.Empty, item.WorkId));
        Assert.Equal("Q4242", universe.Qid);
        Assert.Equal(CollectionGroupTypes.Universe, universe.GroupType);
        Assert.Equal(universe.ItemCount, universe.Shelves.Sum(shelf => shelf.ItemCount));
        Assert.All(universe.Shelves, shelf =>
        {
            Assert.Equal(CollectionGroupTypes.Series, shelf.GroupType);
            Assert.NotEmpty(shelf.Shelf.Items);
            Assert.StartsWith("/api/v1/display/groups/", shelf.Shelf.SeeAllRoute, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void CheckedInViewFixtures_MatchThePublishedPhotoContractTypes()
    {
        var timeline = Read<ViewClientTimelinePageDto>("view-timeline.json");
        var item = Read<LocalAssetDto>("view-item.json");
        var galleries = Read<ViewGalleryListResponse>("view-galleries.json");

        Assert.Equal([(2026, 3), (2026, 2)], timeline.Months.Select(month => (month.Year, month.Month)));
        Assert.True(timeline.HasMore);
        Assert.NotNull(timeline.NextCursor);
        Assert.Equal(2, timeline.Months[0].Items.Count);
        Assert.All(timeline.Months.SelectMany(month => month.Items), photo =>
        {
            Assert.Contains(photo.MediaKind, new[] { "image", "video" });
            Assert.StartsWith($"/api/v1/view/items/{photo.Id:D}/", photo.ThumbnailUrl, StringComparison.Ordinal);
            Assert.EndsWith("/original", photo.ContentUrl, StringComparison.Ordinal);
        });
        Assert.Equal("video", item.MediaKind);
        Assert.Equal(1920, item.Width);
        Assert.Equal("Pixel", item.DeviceMake);
        Assert.Equal("Paris", item.LocationName);
        Assert.Null(item.DocumentInfo);
        Assert.Single(galleries.Owned);
        Assert.Single(galleries.SharedWithYou);

        // The fixtures use the wire names the types publish: nothing in them is renamed, extra or case-shifted.
        AssertSameWireNames("view-timeline.json", timeline);
        AssertSameWireNames("view-item.json", item);
        AssertSameWireNames("view-galleries.json", galleries);
    }

    [Fact]
    public void ProfileSwitchFixtures_MatchThePublishedContractTypes()
    {
        var profiles = Read<DeviceProfileListDto>("device-profiles.json");
        var paired = Read<OAuthTokenResponse>("oauth-token.json");
        var switched = Read<OAuthTokenResponse>("switch-profile.json");

        Assert.Equal(2, profiles.Profiles.Count);
        Assert.Single(profiles.Profiles, profile => profile.IsActive);
        Assert.Contains(profiles.Profiles, profile => profile.IsKids && profile.RequiresPin && profile.AvatarUrl is null);
        Assert.Equal(profiles.Profiles.Single(profile => profile.IsActive).ProfileId, paired.ProfileId);

        // A switch keeps the device and changes only the profile; the response is the ordinary token response.
        Assert.Equal(paired.DeviceId, switched.DeviceId);
        Assert.NotEqual(paired.ProfileId, switched.ProfileId);
        Assert.Contains(profiles.Profiles, profile => profile.ProfileId == switched.ProfileId);
        Assert.NotEqual(paired.RefreshToken, switched.RefreshToken);
    }

    [Fact]
    public void ProfileSwitchRequest_UsesSnakeCaseWireNames()
    {
        var json = JsonSerializer.Serialize(new SwitchDeviceProfileRequest { ProfileId = Guid.Empty, Pin = "1234" }, JsonOptions);

        Assert.Contains("\"profile_id\"", json);
        Assert.Contains("\"pin\"", json);
    }

    [Fact]
    public void OfflineFixtures_MatchThePublishedContractTypes()
    {
        var downloads = Read<DeviceDownloadListDto>("device-downloads.json");
        var next = Read<NextEpisodesDto>("next-episodes.json");

        Assert.Equal(3, downloads.Downloads.Count);
        Assert.All(downloads.Downloads, download => Assert.True(download.StoredBytes > 0));
        Assert.All(downloads.Downloads.Take(2), download =>
        {
            Assert.Equal(DeviceDownloadKinds.Variant, download.Kind);
            Assert.Null(download.PhotoItemId);
            Assert.NotEqual(Guid.Empty, download.AssetId);
            Assert.NotEqual(Guid.Empty, download.VariantId);
        });
        Assert.NotNull(downloads.Downloads[0].ExpiresAt);
        Assert.Null(downloads.Downloads[1].ExpiresAt);

        // A kept photo names its View item and carries the empty asset and variant ids; it never expires.
        var photo = downloads.Downloads[2];
        Assert.Equal(DeviceDownloadKinds.Photo, photo.Kind);
        Assert.Equal(Guid.Parse("88888888-8888-8888-8888-888888888888"), photo.PhotoItemId);
        Assert.Equal(Guid.Empty, photo.AssetId);
        Assert.Equal(Guid.Empty, photo.VariantId);
        Assert.Null(photo.ExpiresAt);

        Assert.Equal(2, next.Episodes.Count);
        Assert.Equal([4, 5], next.Episodes.Select(episode => episode.EpisodeNumber!.Value));
        Assert.DoesNotContain(next.Episodes, episode => episode.AssetId == next.AfterAssetId);

        AssertSameWireNames("device-downloads.json", downloads);
        AssertSameWireNames("next-episodes.json", next);
    }

    [Fact]
    public void OfflineRequests_UseSnakeCaseWireNames()
    {
        var upsert = JsonSerializer.Serialize(new UpsertDeviceDownloadRequest
        {
            AssetId = Guid.Empty,
            VariantId = Guid.Empty,
            StoredBytes = 1,
            StoredAt = DateTimeOffset.UnixEpoch,
        }, JsonOptions);
        Assert.Contains("\"asset_id\"", upsert);
        Assert.Contains("\"variant_id\"", upsert);
        Assert.Contains("\"stored_bytes\"", upsert);
        Assert.Contains("\"stored_at\"", upsert);
        Assert.Contains("\"kind\"", upsert);
        Assert.Contains("\"photo_item_id\"", upsert);

        // The new fields are optional: a report without them still reads, as a variant.
        var variantOnly = JsonSerializer.Deserialize<UpsertDeviceDownloadRequest>(
            "{\"asset_id\":\"44444444-4444-4444-4444-444444444444\",\"variant_id\":\"55555555-5555-5555-5555-555555555555\",\"stored_bytes\":5,\"stored_at\":\"2030-01-01T00:00:00+00:00\"}",
            JsonOptions)!;
        Assert.Null(variantOnly.Kind);
        Assert.Null(variantOnly.PhotoItemId);

        var progress = JsonSerializer.Serialize(
            new ProgressUpdateRequest(null, 50, null) { Offline = true, ClientTimestamp = DateTimeOffset.UnixEpoch }, JsonOptions);
        Assert.Contains("\"offline\":true", progress);
        Assert.Contains("\"client_timestamp\"", progress);
        Assert.Contains("\"expected_revision\"", progress);

        var heartbeat = JsonSerializer.Serialize(
            new PlayerHeartbeatDto { Offline = true, ClientTimestamp = DateTimeOffset.UnixEpoch }, JsonOptions);
        Assert.Contains("\"offline\":true", heartbeat);
        Assert.Contains("\"client_timestamp\"", heartbeat);

        // The new fields are optional: a body without them still reads, as a normal (non-offline) request.
        var plain = JsonSerializer.Deserialize<ProgressUpdateRequest>("{\"progress_pct\":10,\"expected_revision\":3}", JsonOptions)!;
        Assert.False(plain.Offline);
        Assert.Null(plain.ClientTimestamp);
    }

    [Fact]
    public void CheckedInV1Fixtures_NeverContainSecretsOrServerPaths()
    {
        foreach (var path in Directory.EnumerateFiles(FixtureDirectory(), "*.json"))
        {
            var contents = File.ReadAllText(path);
            Assert.DoesNotContain("X-Tuvima-Service-Key", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("outputPath", contents, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(@"C:\", contents, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void AssertSameWireNames<T>(string fileName, T value)
    {
        using var fixture = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory(), fileName)));
        using var actual = JsonDocument.Parse(JsonSerializer.Serialize(value, JsonOptions));
        AssertSameWireNames(fileName, "$", fixture.RootElement, actual.RootElement);
    }

    private static void AssertSameWireNames(string fileName, string path, JsonElement fixture, JsonElement actual)
    {
        if (fixture.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            var expected = fixture.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            var published = actual.EnumerateObject().Select(property => property.Name).Order(StringComparer.Ordinal).ToArray();
            // A fixture may leave out optional properties the type adds later, but may never name one the type lacks.
            Assert.True(expected.All(published.Contains), $"{fileName} {path} names a property the contract does not publish: {string.Join(", ", expected.Except(published))}.");
            foreach (var property in fixture.EnumerateObject())
            {
                AssertSameWireNames(fileName, $"{path}.{property.Name}", property.Value, actual.GetProperty(property.Name));
            }
        }
        else if (fixture.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var element in fixture.EnumerateArray())
            {
                AssertSameWireNames(fileName, $"{path}[{index}]", element, actual[index]);
                index++;
            }
        }
    }

    private static T Read<T>(string fileName) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(FixtureDirectory(), fileName)), JsonOptions)
        ?? throw new InvalidOperationException($"Fixture '{fileName}' did not deserialize as {typeof(T).Name}.");

    private static string FixtureDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "fixtures", "native-client-v1"));
}
