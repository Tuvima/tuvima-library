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

    private static ProviderLookupRequest Request(string? showName, string? series, IReadOnlyDictionary<string, string>? hints = null) => new()
    {
        EntityType = EntityType.MediaAsset,
        MediaType = MediaType.TV,
        ShowName = showName,
        Series = series,
        SeasonNumber = "1",
        EpisodeNumber = "1",
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
}
