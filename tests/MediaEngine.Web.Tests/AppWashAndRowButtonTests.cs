using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

/// <summary>The two small shared primitives of the editor header: the row button (a dropdown trigger or one picker row) and the colour wash.</summary>
public sealed class AppWashAndRowButtonTests : AsyncBunitContext
{
    // -- AppWash ----------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheWash_BuildsItsGradientFromTheTwoColoursAndHowMuchOfEachToKeep()
    {
        var cut = Render<AppWash>(parameters => parameters
            .Add(wash => wash.PrimaryHex, "#5a3a8a")
            .Add(wash => wash.SecondaryHex, "#2a1858")
            .Add(wash => wash.PrimaryPercent, 55)
            .Add(wash => wash.SecondaryPercent, 70)
            .Add(wash => wash.Class, "header-wash"));

        var wash = cut.Find(".app-wash");
        Assert.Contains("header-wash", wash.ClassList);
        Assert.Equal("true", wash.GetAttribute("aria-hidden"));
        var style = wash.GetAttribute("style")!;
        Assert.Contains("color-mix(in srgb,#5a3a8a 55%,var(--tl-bg-surface))", style, StringComparison.Ordinal);
        Assert.Contains("color-mix(in srgb,#2a1858 70%,var(--tl-bg-surface))", style, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("red", "blue")]
    [InlineData("#abc", null)]
    [InlineData("#5a3a8a;background:url(x)", "#2a1858)")]
    public void WithNoUsableColour_TheWashDrawsNothing(string? primary, string? secondary)
    {
        var cut = Render<AppWash>(parameters => parameters
            .Add(wash => wash.PrimaryHex, primary)
            .Add(wash => wash.SecondaryHex, secondary)
            .Add(wash => wash.PrimaryPercent, 55)
            .Add(wash => wash.SecondaryPercent, 70));

        Assert.Empty(cut.FindAll(".app-wash"));
    }

    [Fact]
    public void WithOneUsableColour_ItStandsInForTheOther()
    {
        var cut = Render<AppWash>(parameters => parameters
            .Add(wash => wash.PrimaryHex, "not a colour")
            .Add(wash => wash.SecondaryHex, "#2a1858")
            .Add(wash => wash.PrimaryPercent, 40)
            .Add(wash => wash.SecondaryPercent, 60));

        var style = cut.Find(".app-wash").GetAttribute("style")!;
        Assert.Contains("#2a1858 40%", style, StringComparison.Ordinal);
        Assert.Contains("#2a1858 60%", style, StringComparison.Ordinal);
    }

    [Fact]
    public void TheStrengths_AreKeptBetweenZeroAndOneHundred()
    {
        var cut = Render<AppWash>(parameters => parameters
            .Add(wash => wash.PrimaryHex, "#5a3a8a")
            .Add(wash => wash.PrimaryPercent, 900)
            .Add(wash => wash.SecondaryPercent, -4));

        var style = cut.Find(".app-wash").GetAttribute("style")!;
        Assert.Contains("#5a3a8a 100%", style, StringComparison.Ordinal);
        Assert.Contains("#5a3a8a 0%", style, StringComparison.Ordinal);
    }

    // -- AppRowButton -----------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRowButton_IsAButtonThatWrapsTheCallersMarkup_AndPassesOtherAttributesThrough()
    {
        var cut = Render<AppRowButton>(parameters => parameters
            .Add(button => button.Class, "tl-row")
            .AddUnmatched("id", "row-1")
            .AddUnmatched("role", "option")
            .AddUnmatched("aria-selected", "true")
            .AddChildContent("<b>Title</b>"));

        var button = cut.Find("button");
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Contains("app-row-button", button.ClassList);
        Assert.Contains("tl-row", button.ClassList);
        Assert.Equal("row-1", button.Id);
        Assert.Equal("option", button.GetAttribute("role"));
        Assert.Equal("true", button.GetAttribute("aria-selected"));
        Assert.Equal("Title", button.QuerySelector("b")!.TextContent);
    }

    [Fact]
    public void TheRowButton_RaisesItsClick_UnlessItIsDisabled()
    {
        var clicks = 0;
        var cut = Render<AppRowButton>(parameters => parameters
            .Add(button => button.OnClick, () => clicks++)
            .AddChildContent("Pick"));

        cut.Find("button").Click();
        Assert.Equal(1, clicks);

        cut.Render(parameters => parameters.Add(button => button.Disabled, true));
        Assert.True(cut.Find("button").HasAttribute("disabled"));
    }
}
