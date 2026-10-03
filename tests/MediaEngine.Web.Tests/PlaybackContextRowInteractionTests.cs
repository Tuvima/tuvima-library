using Bunit;
using MediaEngine.Web.Components.Shared;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackContextRowInteractionTests : BunitContext
{
    [Fact]
    public async Task ChapterVariantMakesNumberActivityTitleAndDurationOneActivationTarget()
    {
        var selectedChapter = -1;
        var cut = Render<PlaybackContextRow>(parameters => parameters
            .Add(component => component.Variant, "chapter")
            .Add(component => component.LeadingText, "12")
            .Add(component => component.Title, "A Long Journey")
            .Add(component => component.Duration, "42:17")
            .Add(component => component.IsCurrent, true)
            .Add(component => component.IsPlaying, true)
            .Add(component => component.AriaLabel, "Play chapter 12: A Long Journey")
            .Add(component => component.OnClick, () => selectedChapter = 12));

        var row = cut.Find("button.playback-context-row--chapter");
        Assert.Equal("true", row.GetAttribute("aria-current"));
        Assert.Equal("Play chapter 12: A Long Journey", row.GetAttribute("aria-label"));
        Assert.Equal("12", row.QuerySelector(".playback-context-row__leading-text")?.TextContent);
        Assert.Equal("42:17", row.QuerySelector(".playback-context-row__duration")?.TextContent);
        Assert.NotNull(row.QuerySelector(".playback-activity-mark"));
        Assert.Empty(row.QuerySelectorAll("button"));

        await row.ClickAsync(new());
        Assert.Equal(12, selectedChapter);
    }
}
