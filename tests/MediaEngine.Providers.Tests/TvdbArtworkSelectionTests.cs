using System.Text.Json.Nodes;
using MediaEngine.Domain.Enums;
using MediaEngine.Providers.Services;

namespace MediaEngine.Providers.Tests;

public sealed class TvdbArtworkSelectionTests
{
    [Theory]
    [InlineData("series", "Poster", AssetType.CoverArt)]
    [InlineData("series", "Clear Logo", AssetType.Logo)]
    [InlineData("series", "Background", AssetType.Background)]
    [InlineData("series", "Season Poster", null)]
    [InlineData("series", "Season Background", null)]
    [InlineData("season", "Season Poster", AssetType.SeasonPoster)]
    [InlineData("season", "Season Background", AssetType.SeasonThumb)]
    public void Classify_KeepsSeasonPicturesOffTheShow(string scope, string typeName, AssetType? expected)
    {
        Assert.Equal(expected, TvdbArtworkSelection.Classify(scope, typeName));
    }

    [Fact]
    public void Rank_DropsLogosInAnotherLanguageAndPutsTheConfiguredLanguageFirst()
    {
        var logos = new[]
        {
            Art("fra", score: 9000),
            Art(null, score: 5000),
            Art("eng", score: 100),
        };

        var ranked = TvdbArtworkSelection.Rank(logos, AssetType.Logo, "en");

        Assert.Equal(2, ranked.Count);
        Assert.Equal("eng", ranked[0]["language"]?.ToString());
        Assert.Null(ranked[1]["language"]?.ToString());
    }

    [Fact]
    public void Rank_KeepsAForeignPosterOnlyWhenNothingElseExists()
    {
        var onlyForeign = new[] { Art("fra", 10) };
        Assert.Single(TvdbArtworkSelection.Rank(onlyForeign, AssetType.CoverArt, "en"));

        var mixed = new[] { Art("fra", 10), Art("eng", 1) };
        var ranked = TvdbArtworkSelection.Rank(mixed, AssetType.CoverArt, "en");
        Assert.Single(ranked);
        Assert.Equal("eng", ranked[0]["language"]?.ToString());
    }

    [Fact]
    public void Rank_OrdersSameLanguageArtworkByScore()
    {
        var ranked = TvdbArtworkSelection.Rank([Art("eng", 5), Art("eng", 50), Art("eng", 20)], AssetType.CoverArt, "en");

        Assert.Equal(["50", "20", "5"], ranked.Select(node => node["score"]!.ToString()));
    }

    [Theory]
    [InlineData("en", "eng")]
    [InlineData("fr", "fra")]
    [InlineData("pt-BR", "por")]
    [InlineData(null, "eng")]
    public void ThreeLetterLanguage_MapsTheAppSettingToTheTvdbCode(string? setting, string expected)
    {
        Assert.Equal(expected, TvdbArtworkSelection.ThreeLetterLanguage(setting));
    }

    private static JsonNode Art(string? language, int score) =>
        new JsonObject { ["language"] = language, ["score"] = score, ["image"] = "https://artworks.thetvdb.com/x.jpg" };
}
