using System.Text.Json;
using MediaEngine.Domain.Configuration;
using MediaEngine.Domain.Services;

namespace MediaEngine.Domain.Tests;

public sealed class OutsideLinksTests
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    private const string Mbid = "5b11f4ce-a62d-471e-81fc-a69a8278c7da";

    [Fact]
    public void TheTvdb_UsesTheRedirectByNumberAddress_ForEveryKind()
    {
        Assert.Equal("https://thetvdb.com/dereferrer/series/123", OutsideLinks.TvdbSeries("123"));
        Assert.Equal("https://thetvdb.com/dereferrer/season/45", OutsideLinks.TvdbSeason("45"));
        Assert.Equal("https://thetvdb.com/dereferrer/episode/6", OutsideLinks.TvdbEpisode("6"));
        Assert.Equal("https://thetvdb.com/dereferrer/people/7", OutsideLinks.TvdbPerson("7"));
        Assert.Equal(OutsideLinks.TvdbSeason("45"), OutsideLinks.TvdbForScope("season", "45"));
        Assert.Equal(OutsideLinks.TvdbSeries("45"), OutsideLinks.TvdbForScope("show", "45"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("the-continental")]
    [InlineData("12 3")]
    [InlineData("12/../3")]
    public void AMissingOrMalformedIdentifier_GivesNoLink(string? id)
    {
        Assert.Null(OutsideLinks.TvdbSeries(id));
        Assert.Null(OutsideLinks.TvdbSeason(id));
        Assert.Null(OutsideLinks.TvdbEpisode(id));
        Assert.Null(OutsideLinks.TvdbPerson(id));
        Assert.Null(OutsideLinks.Tmdb(id, "TV"));
        Assert.Null(OutsideLinks.MusicBrainz("artist", id));
        Assert.Null(OutsideLinks.Wikidata(id));
        Assert.Null(OutsideLinks.ComicVineIssue(id));
    }

    [Fact]
    public void OtherProviders_BuildTheirKnownAddresses()
    {
        Assert.Equal("https://www.themoviedb.org/tv/1399", OutsideLinks.Tmdb("1399", "TV"));
        Assert.Equal("https://www.themoviedb.org/movie/603", OutsideLinks.Tmdb("603", null));
        Assert.Equal($"https://musicbrainz.org/artist/{Mbid}", OutsideLinks.MusicBrainz("artist", Mbid));
        Assert.Null(OutsideLinks.MusicBrainz("nonsense", Mbid));
        Assert.Null(OutsideLinks.MusicBrainz("release", "not-a-guid"));
        Assert.Equal("https://www.wikidata.org/wiki/Q42", OutsideLinks.Wikidata("q42"));
        Assert.Null(OutsideLinks.Wikidata("Q"));
        Assert.Equal("https://comicvine.gamespot.com/issue/4000-9/", OutsideLinks.ComicVineIssue("9::Title"));
        Assert.Equal("https://comicvine.gamespot.com/volume/4050-2/", OutsideLinks.ComicVineVolume("2"));
        Assert.Equal("https://www.imdb.com/title/tt0133093", OutsideLinks.Imdb("tt0133093"));
        Assert.Null(OutsideLinks.WebPage("javascript:alert(1)"));
    }

    // Every link pattern in the shipped provider config must expand to a well-formed web address for a sample identifier, and must
    // refuse a malformed one. A pattern that cannot open is caught here rather than on a user's screen.
    [Fact]
    public void EveryShippedProviderLinkTemplate_ExpandsToAWellFormedAddress()
    {
        var checkedLinks = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "config", "providers"), "*.json"))
        {
            var provider = JsonSerializer.Deserialize<ProviderConfiguration>(File.ReadAllText(file), s_json);
            foreach (var (key, link) in provider?.UiMetadata?.ExternalLinks ?? [])
            {
                var url = OutsideLinks.Expand(link.UrlTemplate, key, SampleFor(key), "TV");
                Assert.True(
                    url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps,
                    $"{Path.GetFileName(file)} link '{key}' did not expand to a web address: {url}");
                Assert.DoesNotContain("{", url, StringComparison.Ordinal);
                if (key is "tvdb_id" or "tvdb_season_id" or "tvdb_episode_id" or "wikidata_qid" or "musicbrainz_release_id")
                {
                    Assert.Null(OutsideLinks.Expand(link.UrlTemplate, key, "not valid", "TV"));
                }

                checkedLinks++;
            }
        }

        Assert.True(checkedLinks >= 10, $"Expected the shipped config to define at least 10 links, found {checkedLinks}.");
    }

    [Fact]
    public void TheTvdbTemplatesInConfig_MatchTheSharedBuilder()
    {
        var provider = JsonSerializer.Deserialize<ProviderConfiguration>(
            File.ReadAllText(Path.Combine(FindRepoRoot(), "config", "providers", "tvdb.json")), s_json)!;
        var links = provider.UiMetadata!.ExternalLinks;

        Assert.Equal(OutsideLinks.TvdbSeries("81"), OutsideLinks.Expand(links["tvdb_id"].UrlTemplate, "tvdb_id", "81"));
        Assert.Equal(OutsideLinks.TvdbSeason("81"), OutsideLinks.Expand(links["tvdb_season_id"].UrlTemplate, "tvdb_season_id", "81"));
        Assert.Equal(OutsideLinks.TvdbEpisode("81"), OutsideLinks.Expand(links["tvdb_episode_id"].UrlTemplate, "tvdb_episode_id", "81"));
    }

    // No code builds a TheTVDB page address on its own: it all goes through OutsideLinks.
    [Fact]
    public void NoSourceFileBuildsATheTvdbPageAddressOutsideTheSharedBuilder()
    {
        var root = FindRepoRoot();
        var offenders = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(file => file.EndsWith(".cs", StringComparison.Ordinal) || file.EndsWith(".razor", StringComparison.Ordinal))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                && !file.EndsWith("OutsideLinks.cs", StringComparison.Ordinal))
            .Where(file => System.Text.RegularExpressions.Regex.IsMatch(
                File.ReadAllText(file),
                @"thetvdb\.com/(series|seasons?|episodes?|people|dereferrer)"))
            .Select(file => Path.GetRelativePath(root, file))
            .ToList();

        Assert.Empty(offenders);
    }

    private static string SampleFor(string key) => key switch
    {
        _ when key.StartsWith("musicbrainz_", StringComparison.Ordinal) => Mbid,
        "wikidata_qid" => "Q42",
        "imdb_id" => "tt0133093",
        "wikipedia_url" or "issue_source_url" => "https://example.org/page",
        _ when key.StartsWith("tvdb_", StringComparison.Ordinal) || key.StartsWith("tmdb_", StringComparison.Ordinal) || key == "comicvine_id" => "123",
        _ => "123",
    };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MediaEngine.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
    }
}
