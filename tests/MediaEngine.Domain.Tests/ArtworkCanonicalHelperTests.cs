using MediaEngine.Domain.Entities;

namespace MediaEngine.Domain.Tests;

public sealed class ArtworkCanonicalHelperTests
{
    private static readonly string[] SharedKeys =
    [
        "artwork_primary_hex",
        "artwork_secondary_hex",
        "artwork_accent_hex",
        "dominant_color",
    ];

    [Fact]
    public void PreferredCoverArt_WritesTheSharedColourSetAndTheCoverSet()
    {
        var values = Canonicals("CoverArt", "#111111", "#222222", "#333333");

        Assert.Equal("#111111", values["artwork_primary_hex"]);
        Assert.Equal("#222222", values["artwork_secondary_hex"]);
        Assert.Equal("#333333", values["artwork_accent_hex"]);
        Assert.Equal("#333333", values["dominant_color"]);
        Assert.Equal("#333333", values["cover_accent_hex"]);
    }

    [Theory]
    [InlineData("EpisodeStill", "episode_still")]
    [InlineData("SeasonPoster", "season_poster")]
    [InlineData("Headshot", "artist_photo")]
    [InlineData("CharacterPortrait", "character_portrait")]
    public void PreferredMainPicture_WritesTheSharedColourSet(string assetType, string prefix)
    {
        var values = Canonicals(assetType, "#111111", "#222222", "#333333");

        Assert.Equal("#333333", values["artwork_accent_hex"]);
        Assert.Equal("#333333", values["dominant_color"]);
        Assert.Equal("#333333", values[$"{prefix}_accent_hex"]);
    }

    [Fact]
    public void PreferredBackground_WritesOnlyItsOwnColourSet()
    {
        var values = Canonicals("Background", "#111111", "#222222", "#333333");

        Assert.Equal("#111111", values["background_primary_hex"]);
        Assert.Equal("#222222", values["background_secondary_hex"]);
        Assert.Equal("#333333", values["background_accent_hex"]);
        Assert.All(SharedKeys, key => Assert.DoesNotContain(key, values.Keys));
    }

    [Theory]
    [InlineData("Logo")]
    [InlineData("NetworkLogo")]
    [InlineData("StudioLogo")]
    [InlineData("SeasonThumb")]
    public void PreferredLogoOrThumb_DoesNotTouchTheSharedColourSet(string assetType)
    {
        var values = Canonicals(assetType, "#111111", "#222222", "#333333");

        Assert.All(SharedKeys, key => Assert.DoesNotContain(key, values.Keys));
    }

    [Fact]
    public void RepairWalkOverACoverAndAnOlderBackground_LeavesTheCoverColoursOnTheTitle()
    {
        var entityId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var cover = Asset("CoverArt", "#111111", "#222222", "#333333");
        var background = Asset("Background", "#aaaaaa", "#bbbbbb", "#cccccc");

        // The start-up repair walks preferred assets newest-first, so the older background is written last.
        var stored = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in new[] { cover, background })
        {
            foreach (var value in ArtworkCanonicalHelper.CreatePreferredAssetCanonicals(entityId, asset, now))
            {
                stored[value.Key] = value.Value;
            }
        }

        Assert.Equal("#333333", stored["artwork_accent_hex"]);
        Assert.Equal("#333333", stored["dominant_color"]);
        Assert.Equal("#cccccc", stored["background_accent_hex"]);
    }

    private static Dictionary<string, string> Canonicals(string assetType, string primary, string secondary, string accent) =>
        ArtworkCanonicalHelper
            .CreatePreferredAssetCanonicals(Guid.NewGuid(), Asset(assetType, primary, secondary, accent), DateTimeOffset.UtcNow)
            .ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);

    private static EntityAsset Asset(string assetType, string primary, string secondary, string accent) => new()
    {
        AssetTypeValue = assetType,
        PrimaryHex = primary,
        SecondaryHex = secondary,
        AccentHex = accent,
    };
}
