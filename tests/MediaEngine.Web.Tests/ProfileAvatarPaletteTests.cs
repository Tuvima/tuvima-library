using MediaEngine.Web.Components.Profiles;
using MediaEngine.Web.Models.ViewDTOs;

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
    public void EverySwatch_IsADifferentHue_SoNoTwoPeopleReadAsTheSameColor()
    {
        // Slate is the one grey; every other swatch must sit at least 15 degrees from every other one around the colour wheel.
        var hues = ProfileAvatarPalette.Swatches.Where(swatch => swatch.Name != "Slate").Select(swatch => (swatch.Name, Hue: HueOf(swatch.Hex))).ToList();

        foreach (var (first, second) in hues.SelectMany((a, i) => hues.Skip(i + 1), (a, b) => (a, b)))
        {
            var gap = Math.Abs(first.Hue - second.Hue);
            gap = Math.Min(gap, 360 - gap);
            Assert.True(gap >= 15, $"{first.Name} and {second.Name} are only {gap:0} degrees apart.");
        }
    }

    [Fact]
    public void Distinct_GivesFirstThreePeopleWhoAllSavedPurple_ColorsFarApart()
    {
        var colors = ProfileAvatarPalette.Distinct(["#8852FC", "#8852FC", "#8852FC"]);
        var hues = colors.Select(HueOf).ToList();

        for (var i = 0; i < hues.Count; i++)
        {
            for (var j = i + 1; j < hues.Count; j++)
            {
                var gap = Math.Abs(hues[i] - hues[j]);
                Assert.True(Math.Min(gap, 360 - gap) >= 60, $"{colors[i]} and {colors[j]} are too close.");
            }
        }
    }

    [Fact]
    public void ColorFor_GivesAPersonTheSameColorEverywhereTheHouseholdListIsUsed()
    {
        var ada = Person("Ada", "#8852FC");
        var ben = Person("Ben", "#8852FC");
        var kit = Person("Kit", "#8852FC");
        IReadOnlyList<ProfileViewModel> household = [ada, ben, kit];

        Assert.Equal(["#8852FC", ProfileAvatarPalette.Swatches[1].Hex, ProfileAvatarPalette.Swatches[2].Hex],
            household.Select(person => ProfileAvatarPalette.ColorFor(household, person)));
        Assert.Equal(kit.AvatarColor, ProfileAvatarPalette.ColorFor([ada], kit));
    }

    private static ProfileViewModel Person(string name, string color) =>
        new(Guid.NewGuid(), name, color, "Profile", DateTimeOffset.UtcNow);

    private static double HueOf(string hex)
    {
        var rgb = Convert.ToInt32(hex[1..], 16);
        double red = ((rgb >> 16) & 0xFF) / 255d, green = ((rgb >> 8) & 0xFF) / 255d, blue = (rgb & 0xFF) / 255d;
        var max = Math.Max(red, Math.Max(green, blue));
        var delta = max - Math.Min(red, Math.Min(green, blue));
        var hue = max == red ? ((green - blue) / delta) % 6 : max == green ? ((blue - red) / delta) + 2 : ((red - green) / delta) + 4;
        return (hue * 60 + 360) % 360;
    }

    [Fact]
    public void InkFor_PicksDarkLetteringOnLightColorsAndLightLetteringOnDarkOnes()
    {
        Assert.Equal("#1b1b1f", ProfileAvatarPalette.InkFor("#FBBF24"));
        Assert.Equal("#ffffff", ProfileAvatarPalette.InkFor("#3B82F6"));
        Assert.Equal("#ffffff", ProfileAvatarPalette.InkFor("not a color"));
    }
}
