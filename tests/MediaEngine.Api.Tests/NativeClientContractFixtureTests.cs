using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.LocalAssets;
using MediaEngine.Contracts.Playback;

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
