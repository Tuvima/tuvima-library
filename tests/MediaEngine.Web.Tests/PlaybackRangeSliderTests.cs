using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackRangeSliderTests : BunitContext
{
    [Fact]
    public void BareAppearancePropagatesThroughControlStripWithoutChangingSurfaceRecipe()
    {
        JSInterop.SetupModule("./js/playback-tooltip.js").Mode = JSRuntimeMode.Loose;
        var control = new PlaybackControlDefinition(
            PlaybackControlKey.Speed,
            "Speed",
            "Playback speed 1.25x",
            "speed",
            PlaybackControlPlacement.ToolStrip,
            "speed",
            ValueText: "1.25x");

        var cut = Render<PlaybackControlStrip>(parameters => parameters
            .Add(component => component.Controls, [control])
            .Add(component => component.Surface, "dock")
            .Add(component => component.Appearance, "bare"));

        Assert.Contains("playback-control-strip--surface-dock", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("playback-control-strip--appearance-bare", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("playback-icon-button-shell--surface-dock", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("playback-icon-button-shell--appearance-bare", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("Playback speed 1.25x", cut.Find("button").GetAttribute("aria-label"));
        Assert.Equal(string.Empty, cut.Find("button").TextContent.Trim());
        Assert.NotNull(cut.Find("button svg"));
    }

    [Fact]
    public void ReadOnlyBookProgressUsesSharedRailWithoutSeekInput()
    {
        var cut = Render<PlaybackRangeSlider>(parameters => parameters
            .Add(component => component.Min, 0)
            .Add(component => component.Max, 100)
            .Add(component => component.Value, 42)
            .Add(component => component.ReadOnly, true)
            .Add(component => component.ShowTicks, false)
            .Add(component => component.AriaLabel, "Book progress"));

        var progress = cut.Find("[role='progressbar']");
        Assert.Equal("Book progress", progress.GetAttribute("aria-label"));
        Assert.Equal("42", progress.GetAttribute("aria-valuenow"));
        Assert.Contains("42%", progress.GetAttribute("style"));
        Assert.Empty(cut.FindAll("input[type='range']"));
        Assert.Empty(cut.FindAll(".playback-range-slider__thumb"));
        Assert.Empty(cut.FindAll(".playback-range-slider__ticks"));
        Assert.Empty(cut.FindAll(".playback-range-slider__labels"));
    }

    [Theory]
    [InlineData("dock", "playback-range-slider--dock")]
    [InlineData("phone", "playback-range-slider--phone")]
    [InlineData("popup", "playback-range-slider--popup")]
    [InlineData("video", "playback-range-slider--video")]
    public void RangeSliderUsesExplicitSurfaceRecipe(string surface, string expectedClass)
    {
        var cut = Render<PlaybackRangeSlider>(parameters => parameters
            .Add(component => component.Min, 0)
            .Add(component => component.Max, 1)
            .Add(component => component.Value, 0.5)
            .Add(component => component.Surface, surface)
            .Add(component => component.ShowTicks, false));

        Assert.Contains(expectedClass, cut.Markup, StringComparison.Ordinal);
        Assert.NotNull(cut.Find("input[type='range']"));
    }

    [Fact]
    public void ContextRowKeepsCurrentArtworkAndIdentityActionSeparate()
    {
        var clicked = false;
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Title, "Current chapter")
            .Add(component => component.Subtitle, "A Better Problem to Have")
            .Add(component => component.ArtworkUrl, "/stream/artwork/item-1?role=cover")
            .Add(component => component.Duration, "42:17")
            .Add(component => component.IsCurrent, true)
            .Add(component => component.IsPlaying, false)
            .Add(component => component.OnClick, () => clicked = true));

        var row = cut.Find("[data-playback-current='true']");
        Assert.Equal("true", row.GetAttribute("aria-current"));
        Assert.Equal("/stream/artwork/item-1?role=cover&size=s", cut.Find("img").GetAttribute("src"));
        Assert.Null(cut.Find("img").GetAttribute("srcset")); // No native artwork dimensions were provided.
        Assert.Contains("playback-context-row__activity", row.InnerHtml);
        cut.Find(".playback-context-row__identity").Click();
        Assert.True(clicked);
        Assert.Contains("42:17", row.TextContent);
    }

    [Fact]
    public void ContextRowHistoryKeepsTimeAndActionsInFixedColumnsWithoutDuration()
    {
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Title, "Chapter 4")
            .Add(component => component.Variant, "history")
            .Add(component => component.LeadingText, "8:42 PM")
            .Add(component => component.Actions, builder => builder.AddMarkupContent(0, "<button>Replay</button>")));

        var row = cut.Find(".playback-context-row--history");
        Assert.Contains("--playback-context-leading-width:80px", row.GetAttribute("style"));
        Assert.Contains("8:42 PM", row.TextContent);
        Assert.Single(cut.FindAll(".playback-context-row__actions button"));
        Assert.DoesNotContain("playback-context-row__duration", row.InnerHtml);

        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/MediaEngine.Web/Components/Shared/PlaybackContextRow.razor.css"));
        var css = File.ReadAllText(root);
        Assert.Contains("var(--playback-context-duration-width, 56px)", css, StringComparison.Ordinal);
        Assert.Contains("grid-column:4; width:44px", css, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, "is-paused", "Current item paused")]
    [InlineData(true, "is-playing", "Now playing")]
    public void ChapterContextRowUsesInlineActivityAndOmitsEmptyActionColumn(bool isPlaying, string activityClass, string activityLabel)
    {
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Title, "Chapter Twelve")
            .Add(component => component.Variant, "chapter")
            .Add(component => component.LeadingText, "12")
            .Add(component => component.Duration, "43:20")
            .Add(component => component.IsCurrent, true)
            .Add(component => component.IsPlaying, isPlaying)
            .Add(component => component.Actions, builder => builder.AddMarkupContent(0, "<button>Unavailable</button>")));

        var row = cut.Find(".playback-context-row--chapter");
        Assert.Equal("true", row.GetAttribute("aria-current"));
        Assert.Contains("12", row.TextContent);
        Assert.Contains("43:20", row.TextContent);
        Assert.Contains("playback-context-row__activity--chapter", row.InnerHtml);
        Assert.Contains(activityClass, row.QuerySelector(".playback-activity-mark")!.GetAttribute("class"));
        Assert.Equal(activityLabel, row.QuerySelector(".playback-activity-mark")!.GetAttribute("aria-label"));
        Assert.Empty(row.QuerySelectorAll(".playback-context-row__actions"));
    }

    [Fact]
    public void InlinePlaybackSheetUsesRegionSemantics()
    {
        var cut = Render<PlaybackToolSheet>(parameters => parameters
            .Add(component => component.Title, "Queue")
            .Add(component => component.Modal, false));

        var region = cut.Find("section[role='region']");
        Assert.Equal("Queue", region.GetAttribute("aria-label"));
        Assert.Null(region.GetAttribute("aria-modal"));
    }
}
