using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Models;
using MediaEngine.Providers.Tests.ProviderRecording;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// The edge cases of the shipped TVDB file that the old hand-written provider handled one way and
/// the general flow runner must handle identically.
/// </summary>
public sealed class TvdbFlowParityTests
{
    private static RecordingHandler Handler() => new(request =>
    {
        var path = request.Uri.AbsolutePath;
        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
        }

        if (path.EndsWith("/search", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":[{"tvdb_id":"2","name":"Orbit Kids"},{"tvdb_id":"1","name":"  Orbit  "}]}""");
        }

        return RecordingHandler.Json("""{"data":null}""");
    });

    private static ProviderLookupRequest Request(string? showName, string? series, IReadOnlyDictionary<string, string>? hints = null, string? season = "1", string? episode = "1") => new()
    {
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.TV,
        ShowName = showName,
        Series = series,
        SeasonNumber = season,
        EpisodeNumber = episode,
        Hints = hints,
    };

    [Fact]
    public async Task BlankShowName_DoesNotFallBackToTheSeriesAndSearchesNothing()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        var claims = await rig.Provider.FetchAsync(Request("   ", "Orbit"));
        var items = await rig.Provider.SearchAsync(Request("   ", "Orbit"), limit: 5);

        Assert.Empty(claims);
        Assert.Empty(items);
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingShowName_FallsBackToTheSeries()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request(null, "Orbit"));

        Assert.Contains(handler.Lines, line => line.Contains("/search?query=Orbit", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("")]
    [InlineData("tmdb")]
    public async Task AnyOtherIdentityProviderHint_StopsTheFlow(string hint)
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        var claims = await rig.Provider.FetchAsync(Request("Orbit", null, new Dictionary<string, string> { ["identity_provider"] = hint }));

        Assert.Empty(claims);
        Assert.Empty(handler.Lines);
    }

    [Fact]
    public async Task OwnIdentityProviderHint_LetsTheFlowRun()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Orbit", null, new Dictionary<string, string> { ["identity_provider"] = "TVDB" }));

        Assert.Contains(handler.Lines, line => line.Contains("/search", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchResultNames_AreTrimmedBeforeTheyAreComparedWithTheShowName()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Orbit", null));

        Assert.Contains(handler.Lines, line => line.Contains("/series/1/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/series/2/extended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpisodeWithoutSeason_StillSearchesTheShowAsSeasonOne()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Orbit", null, season: null));

        Assert.Contains(handler.Lines, line => line.Contains("/search?query=Orbit", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SeasonWithoutEpisode_StillSearchesNothing()
    {
        var handler = Handler();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Orbit", null, episode: null));

        Assert.DoesNotContain(handler.Lines, line => line.Contains("/search", StringComparison.Ordinal));
    }

    private static RecordingHandler HandlerWithHits(string hitsJson) => new(request =>
    {
        var path = request.Uri.AbsolutePath;
        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
        }

        return path.EndsWith("/search", StringComparison.Ordinal)
            ? RecordingHandler.Json(hitsJson)
            : RecordingHandler.Json("""{"data":null}""");
    });

    [Fact]
    public async Task ReorderedFolderWording_StillPicksTheSeriesWhenMostWordsAreShared()
    {
        var handler = HandlerWithHits("""{"data":[{"tvdb_id":"999","name":"Something Else Entirely"},{"tvdb_id":"343791","name":"MasterClass: Stephen Curry Teaches Shooting, Ball-Handling, and Scoring"}]}""");
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Stephen Curry - MasterClass - Shooting, Ball-Handling, and Scoring", null, season: null));

        Assert.Contains(handler.Lines, line => line.Contains("/series/343791/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/series/999/extended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASharedFirstWordAlone_IsNotEnoughToPickASeries()
    {
        var handler = HandlerWithHits("""{"data":[{"tvdb_id":"1","name":"Lost in Space"},{"tvdb_id":"2","name":"Lost Girl"}]}""");
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Lost", null));

        Assert.DoesNotContain(handler.Lines, line => line.Contains("/extended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnExactNameFurtherDown_BeatsACloseNameAbove()
    {
        var handler = HandlerWithHits("""{"data":[{"tvdb_id":"1","name":"Orbit Kids Special"},{"tvdb_id":"2","name":"Orbit Kids"}]}""");
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Orbit Kids", null));

        Assert.Contains(handler.Lines, line => line.Contains("/series/2/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/series/1/extended", StringComparison.Ordinal));
    }

    private static RecordingHandler HandlerWithQueryHits() => new(request =>
    {
        var path = request.Uri.AbsolutePath;
        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
        }

        if (path.EndsWith("/search", StringComparison.Ordinal))
        {
            var query = Uri.UnescapeDataString(request.Uri.Query).Replace('+', ' ');
            return query.Contains("Stephen Curry MasterClass", StringComparison.Ordinal)
                ? RecordingHandler.Json("""{"data":[{"tvdb_id":"7","name":"Stephen Curry"},{"tvdb_id":"343791","name":"MasterClass: Stephen Curry Teaches Shooting, Ball-Handling, and Scoring"}]}""")
                : query.Contains("Stephen Curry", StringComparison.Ordinal) && !query.Contains("Shooting", StringComparison.Ordinal)
                    ? RecordingHandler.Json("""{"data":[{"tvdb_id":"7","name":"Stephen Curry"}]}""")
                    : RecordingHandler.Json("""{"data":[]}""");
        }

        return RecordingHandler.Json("""{"data":null}""");
    });

    [Fact]
    public async Task ALongFolderNameWithNoHits_IsRetriedWithItsPartsAndPicksTheCloseSeries()
    {
        var handler = HandlerWithQueryHits();
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Stephen Curry - MasterClass - Shooting, Ball-Handling, and Scoring", null, season: null));

        Assert.Contains(handler.Lines, line => line.Contains("/series/343791/extended", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Lines, line => line.Contains("/series/7/extended", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APartThatReturnsOneUnrelatedHit_IsNotAccepted()
    {
        var handler = new RecordingHandler(request =>
        {
            var path = request.Uri.AbsolutePath;
            if (path.EndsWith("/login", StringComparison.Ordinal))
            {
                return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
            }

            if (path.EndsWith("/search", StringComparison.Ordinal))
            {
                var query = Uri.UnescapeDataString(request.Uri.Query).Replace('+', ' ');
                return query.Contains("Alpha - Beta", StringComparison.Ordinal)
                    ? RecordingHandler.Json("""{"data":[]}""")
                    : RecordingHandler.Json("""{"data":[{"tvdb_id":"7","name":"Alpha Centauri Chronicles"}]}""");
            }

            return RecordingHandler.Json("""{"data":null}""");
        });
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        await rig.Provider.FetchAsync(Request("Alpha - Beta", null));

        Assert.DoesNotContain(handler.Lines, line => line.Contains("/extended", StringComparison.Ordinal));
    }

    private static RecordingHandler EpisodeHandler(bool englishListFound) => new(request =>
    {
        var path = request.Uri.AbsolutePath;
        if (path.EndsWith("/login", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"token":"bearer-token"}}""");
        }

        if (path.EndsWith("/search", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":[{"tvdb_id":"300","name":"Shimmer and Shine"}]}""");
        }

        if (path.Contains("/series/300/episodes/default", StringComparison.Ordinal))
        {
            var withLanguage = path.EndsWith("/eng", StringComparison.Ordinal);
            return withLanguage && !englishListFound
                ? new HttpResponseMessage(System.Net.HttpStatusCode.NotFound)
                : RecordingHandler.Json("""{"data":{"episodes":[{"id":901,"seasonNumber":1,"number":1},{"id":902,"seasonNumber":1,"number":2}]},"links":{"next":null}}""");
        }

        if (path.Contains("/series/300/translations/eng", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"name":"Shimmer and Shine"}}""");
        }

        if (path.Contains("/episodes/902/translations/eng", StringComparison.Ordinal))
        {
            return RecordingHandler.Json("""{"data":{"name":"Pet Palooza","overview":"A genie story."}}""");
        }

        return RecordingHandler.Json("""{"data":{"id":300}}""");
    });

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnEpisodeInTheShowsList_IsPairedEvenWhenTheEnglishListIsNotFound(bool englishListFound)
    {
        var handler = EpisodeHandler(englishListFound);
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        var claims = await rig.Provider.FetchAsync(Request("Shimmer and Shine", null, season: "1", episode: "2"));

        Assert.Contains(claims, claim => claim.Key == "tvdb_episode_id" && claim.Value == "902");
        Assert.Contains(claims, claim => claim.Key == "episode_title" && claim.Value == "Pet Palooza");
    }

    [Fact]
    public async Task AnEpisodeTheShowDoesNotList_StillPairsNothing()
    {
        var handler = EpisodeHandler(englishListFound: false);
        using var rig = new ShippedProviderHarness.TvdbRig(handler);

        var claims = await rig.Provider.FetchAsync(Request("Shimmer and Shine", null, season: "1", episode: "9"));

        Assert.Empty(claims);
    }
}
