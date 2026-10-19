using System.Net;
using MediaEngine.Domain;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;

namespace MediaEngine.Providers.Tests.ProviderRecording;

/// <summary>
/// Records the exact requests, claims and search items of the shipped TVDB provider. The golden
/// files pin behaviour across the WP9 conversion of TVDB to a JSON-defined provider.
/// </summary>
public sealed class TvdbRecordingTests
{
    private const string SearchHits = """
        {"data":[
          {"tvdb_id":"446883","name":"The Leveling of Solo Leveling","year":"2023","image_url":"https://artworks.thetvdb.com/doc.jpg"},
          {"tvdb_id":"389597","name":"Solo Leveling","first_air_time":"2024-01-07","image":"https://artworks.thetvdb.com/anime.jpg","translations":{"eng":"Solo Leveling EN"},"overviews":{"eng":"Overview EN"}},
          {"id":"x1","name":"No Numeric Id","tvdb_id":"abc"}]}
        """;

    private static HttpResponseMessage Respond(RecordedRequest request, Func<RecordedRequest, HttpResponseMessage?>? extra = null)
    {
        var path = request.Uri.AbsolutePath;
        if (extra?.Invoke(request) is { } custom)
        {
            return custom;
        }

        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
        }

        Assert.Equal("bearer-token", request.BearerToken);
        if (path.EndsWith("/search", StringComparison.Ordinal))
        {
            return RecordingHandler.Json(SearchHits);
        }

        if (path.Contains("/translations/eng", StringComparison.Ordinal))
        {
            if (path.Contains("/series/446883", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"data":{"name":"The Leveling of Solo Leveling","overview":"Doc overview"}}""");
            }

            if (path.Contains("/series/", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"data":{"name":"Solo Leveling","overview":"English show overview"}}""");
            }

            return RecordingHandler.Json(path.Contains("/episodes/111/", StringComparison.Ordinal)
                ? """{"data":{"name":"I'm Used to It","overview":"English episode overview"}}"""
                : """{"data":{"name":null}}""");
        }

        if (path.EndsWith("/series/389597/extended", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""
                {"data":{"id":389597,"firstAired":"2024-01-07","year":"2024","image":"https://artworks.thetvdb.com/anime.jpg","overview":"Details",
                 "characters":[
                   {"personName":"Taito Ban","peopleId":"1001","peopleType":"Actor","name":"Jinwoo","personImgURL":"https://artworks.thetvdb.com/p1.jpg"},
                   {"personName":"Director Dan","peopleId":"1002","peopleType":"Director","name":"-"},
                   {"personName":"","peopleId":"1003","peopleType":"Actor","name":"Nameless"},
                   {"personName":"Lacks Id","peopleId":"","peopleType":"Actor","name":"No Id"},
                   {"personName":"Second Actor","peopleId":"1004","peopleType":"actor","name":"","personImgURL":null}]}}
                """);
        }

        if (path.EndsWith("/series/446883/extended", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"id":446883,"year":"2023"}}""");
        }

        if (path.Contains("/episodes/default/eng", StringComparison.Ordinal))
        {
            return RecordingHandler.Json(request.Uri.Query.Contains("page=0", StringComparison.Ordinal)
                ? """{"data":{"episodes":[{"id":110,"seasonNumber":1,"number":2,"aired":"2024-01-14"},{"id":109,"seasonNumber":0,"number":1}]},"links":{"next":"page=1"}}"""
                : """{"data":{"episodes":[{"id":111,"seasonNumber":1,"number":1,"aired":"2024-01-07"}]},"links":{"next":null}}""");
        }

        if (path.EndsWith("/episodes/111/extended", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"id":111,"seriesId":389597,"aired":"2024-01-07"}}""");
        }

        if (path.EndsWith("/episodes/222/extended", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"id":222,"seriesId":999,"aired":"2020-01-01"}}""");
        }

        return RecordingHandler.Status(HttpStatusCode.NotFound);
    }

    private static ProviderLookupRequest Request(
        string? showName = "Solo Leveling", string? season = "1", string? episode = "1",
        Dictionary<string, string>? hints = null) => new()
    {
        EntityId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.TV,
        ShowName = showName,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Hints = hints,
    };

    private static async Task RecordFetchAsync(
        string name, ProviderLookupRequest request, Func<RecordedRequest, HttpResponseMessage?>? extra = null,
        string? apiKey = "installation-key", string? pin = null)
    {
        var handler = new RecordingHandler(r => Respond(r, extra));
        using var rig = new ShippedProviderHarness.TvdbRig(handler, apiKey, pin);
        var claims = await rig.Provider.FetchAsync(request);
        RecordingSnapshot.Verify(name,
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("claims", [RecordingText.Claims(claims)]));
    }

    [Fact]
    public Task FetchByShowName_ComparesNamesThenTranslationsAndReadsEpisodePages() =>
        RecordFetchAsync("tvdb-fetch-by-name", Request(), pin: "subscriber-pin");

    [Fact]
    public Task FetchByConfirmedIds_SkipsSearchAndEpisodePaging() =>
        RecordFetchAsync("tvdb-fetch-confirmed-ids", Request(hints: new Dictionary<string, string>
        {
            [BridgeIdKeys.TvdbId] = "389597",
            [BridgeIdKeys.TvdbEpisodeId] = "111",
        }));

    [Fact]
    public Task FetchWithEpisodeOfAnotherSeries_ReturnsNothing() =>
        RecordFetchAsync("tvdb-fetch-episode-mismatch", Request(hints: new Dictionary<string, string>
        {
            [BridgeIdKeys.TvdbId] = "389597",
            [BridgeIdKeys.TvdbEpisodeId] = "222",
        }));

    [Fact]
    public Task FetchWithOtherIdentityProvider_ReturnsNothingWithoutRequests() =>
        RecordFetchAsync("tvdb-fetch-other-identity-provider", Request(hints: new Dictionary<string, string>
        {
            [MetadataFieldConstants.IdentityProvider] = "tmdb",
        }));

    [Fact]
    public Task FetchWithoutKey_ReturnsNothingWithoutRequests() =>
        RecordFetchAsync("tvdb-fetch-no-key", Request(), apiKey: null);

    [Fact]
    public Task FetchWithoutEpisodeNumbers_ReturnsNothingWithoutRequests() =>
        RecordFetchAsync("tvdb-fetch-no-episode-numbers", Request(season: null, episode: null));

    [Fact]
    public Task FetchWhenSingleSearchHitHasDifferentName_UsesTheOnlyHit() =>
        RecordFetchAsync("tvdb-fetch-single-hit", Request(showName: "Ore dake Level Up"), request =>
            request.Uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? RecordingHandler.Json("""{"data":[{"tvdb_id":"389597","name":"Solo Leveling"}]}""")
                : null);

    [Fact]
    public Task FetchWhenSearchFails_SwallowsTheErrorAndReturnsNothing() =>
        RecordFetchAsync("tvdb-fetch-search-500", Request(), request =>
            request.Uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal)
                ? RecordingHandler.Status(HttpStatusCode.InternalServerError)
                : null);

    [Fact]
    public Task FetchWhenTokenExpires_LogsInAgainOnceAndRetries()
    {
        var unauthorized = 0;
        return RecordFetchAsync("tvdb-fetch-relogin-on-401", Request(), request =>
            request.Uri.AbsolutePath.EndsWith("/search", StringComparison.Ordinal) && unauthorized++ == 0
                ? RecordingHandler.Status(HttpStatusCode.Unauthorized)
                : null);
    }

    [Fact]
    public async Task Search_ReturnsSeriesItemsThenEpisodeItemsWithEnglishTranslations()
    {
        var handler = new RecordingHandler(r => Respond(r));
        using var rig = new ShippedProviderHarness.TvdbRig(handler);
        var lines = new List<string>();

        foreach (var request in new[]
        {
            Request(season: null, episode: null),
            Request(),
            Request(episode: null),
            Request(episode: "99"),
            Request(showName: null, season: null, episode: null),
        })
        {
            var items = await rig.Provider.SearchAsync(request, limit: 5);
            lines.Add($"-- show={request.ShowName} season={request.SeasonNumber} episode={request.EpisodeNumber}");
            lines.AddRange(items.Select(Render));
        }

        RecordingSnapshot.Verify("tvdb-search",
            RecordingText.Section("requests", handler.Lines),
            RecordingText.Section("items", lines));
    }

    private static string Render(SearchResultItem item) => string.Join('|',
        item.Title, item.Author ?? "-", item.Description ?? "-", item.Year ?? "-", item.ThumbnailUrl ?? "-",
        item.ProviderItemId ?? "-", item.Confidence.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture),
        item.ProviderName, item.ResultType ?? "-",
        string.Join(',', (item.ExtraFields ?? new Dictionary<string, string>()).OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{pair.Key}={pair.Value}")));
}
