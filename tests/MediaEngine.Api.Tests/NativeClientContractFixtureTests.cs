using System.Text.Json;
using MediaEngine.Contracts.Authentication;
using MediaEngine.Contracts.Collections;
using MediaEngine.Contracts.Paging;
using MediaEngine.Contracts.Playback;
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
            Assert.Equal(CollectionGroupTypes.Collection, entry.GroupType);
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

    private static T Read<T>(string fileName) where T : class =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(Path.Combine(FixtureDirectory(), fileName)), JsonOptions)
        ?? throw new InvalidOperationException($"Fixture '{fileName}' did not deserialize as {typeof(T).Name}.");

    private static string FixtureDirectory() => Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..",
        "tests", "fixtures", "native-client-v1"));
}
