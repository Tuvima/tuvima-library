using MediaEngine.Web.Components.Profiles;

namespace MediaEngine.Web.Tests;

/// <summary>Each person's circle colour is worked out from the household's order when shown, never stored (profile and household D13).</summary>
public sealed class ProfileAvatarPaletteTests
{
    [Fact]
    public void Distinct_GivesEveryoneAColorTheEarlierPeopleDoNotHave_WhenTheySavedTheSameOne()
    {
        var colors = ProfileAvatarPalette.Distinct(["#8852FC", "#8852FC", "#8852FC", "#8852FC"]);

        Assert.Equal(4, colors.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("#8852FC", colors[0]);
        Assert.All(colors, color => Assert.NotNull(ProfileAvatarPalette.Find(color)));
    }

    [Fact]
    public void Distinct_KeepsTheColorsPeopleChose_WhenTheyAlreadyDiffer()
    {
        var colors = ProfileAvatarPalette.Distinct(["#3B82F6", "#ec4899", "#14B8A6"]);

        Assert.Equal(["#3B82F6", "#EC4899", "#14B8A6"], colors);
    }

    [Fact]
    public void Distinct_ReplacesAnOlderCustomColor_WithTheFirstSwatchNobodyBeforeThemHas()
    {
        var colors = ProfileAvatarPalette.Distinct(["#8852FC", "#123456", null]);

        Assert.Equal("#8852FC", colors[0]);
        Assert.Equal(ProfileAvatarPalette.Swatches[1].Hex, colors[1]);
        Assert.Equal(ProfileAvatarPalette.Swatches[2].Hex, colors[2]);
    }

    [Fact]
    public void Distinct_GivesTheSameAnswerEveryTime_SoNothingNeedsStoring()
    {
        string?[] saved = ["#8852FC", "#8852FC", "#3B82F6"];

        Assert.Equal(ProfileAvatarPalette.Distinct(saved), ProfileAvatarPalette.Distinct(saved));
        Assert.Empty(ProfileAvatarPalette.Distinct([]));
    }

    [Fact]
    public void InkFor_PicksDarkLetteringOnLightColorsAndLightLetteringOnDarkOnes()
    {
        Assert.Equal("#1b1b1f", ProfileAvatarPalette.InkFor("#FBBF24"));
        Assert.Equal("#ffffff", ProfileAvatarPalette.InkFor("#3B82F6"));
        Assert.Equal("#ffffff", ProfileAvatarPalette.InkFor("not a color"));
    }
}
