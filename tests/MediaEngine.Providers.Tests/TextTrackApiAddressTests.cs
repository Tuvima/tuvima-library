using MediaEngine.Domain.Aggregates;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Contracts;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Providers;
using MediaEngine.Providers.Tests.ProviderRecording;

namespace MediaEngine.Providers.Tests;

/// <summary>
/// A subtitle or lyrics provider sends its API key only to an HTTPS address on a host its file lists,
/// and never accepts an answer that came from anywhere else.
/// </summary>
public sealed class TextTrackApiAddressTests
{
    private const string Results =
        """{"status":true,"results":[{"type":"movie","tmdb_id":693134}],"subtitles":[{"n_id":"s1","name":"Dune.srt","language":"EN"}]}""";

    private static TextTrackLookup Lookup() => new(
        new MediaAsset { Id = Guid.NewGuid(), FilePathRoot = "Dune.Part.Two.2024.mkv" },
        MediaType.Movies, "Dune: Part Two", null, null, "2024", "en", null,
        new Dictionary<string, string>(), new SubtitleLookupContext(Movie: new MovieSubtitleIdentity(null, "693134")));

    private static RecordingHandler Handler(Uri? finalAddress = null) => new(_ =>
    {
        var response = RecordingHandler.Json(Results);
        if (finalAddress is not null)
        {
            // What the HTTP stack reports after following a redirect.
            response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalAddress);
        }

        return response;
    });

    [Fact]
    public async Task KeyedProviderWithoutAllowedHosts_SendsNothing()
    {
        var handler = Handler();
        var provider = ShippedProviderHarness.TextTrack("subdl", handler, "example-key", config => config.TextTrack!.Api.AllowedHosts.Clear());

        var failure = await Assert.ThrowsAsync<TextTrackLookupException>(() => provider.SearchAsync(Lookup()));

        Assert.Equal("ProviderUnavailable", failure.Status);
        Assert.Empty(handler.Lines);
    }

    [Fact]
    public async Task KeyedProviderOnAPlainHttpAddress_SendsNothing()
    {
        var handler = Handler();
        var provider = ShippedProviderHarness.TextTrack("subdl", handler, "example-key",
            config => config.Endpoints["api"] = "http://api.subdl.com");

        await Assert.ThrowsAsync<TextTrackLookupException>(() => provider.SearchAsync(Lookup()));

        Assert.Empty(handler.Lines);
    }

    [Fact]
    public async Task AnswerThatCameFromAnotherHost_IsRefused()
    {
        var handler = Handler(new Uri("https://evil.example/api/v1/subtitles"));
        var provider = ShippedProviderHarness.TextTrack("subdl", handler, "example-key");

        var failure = await Assert.ThrowsAsync<TextTrackLookupException>(() => provider.SearchAsync(Lookup()));

        Assert.Equal("ProviderUnavailable", failure.Status);
        Assert.All(handler.Lines, line => Assert.StartsWith("GET https://api.subdl.com/", line, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnswerFromAListedHost_IsAccepted()
    {
        var handler = Handler(new Uri("https://api.subdl.com/api/v1/subtitles"));
        var provider = ShippedProviderHarness.TextTrack("subdl", handler, "example-key");

        Assert.Single(await provider.SearchAsync(Lookup()));
    }
}
