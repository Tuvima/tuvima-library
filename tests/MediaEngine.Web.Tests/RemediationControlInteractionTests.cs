using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class RemediationControlInteractionTests : AsyncBunitContext
{
    public RemediationControlInteractionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging(); Services.AddNativeUiServices();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    [Theory]
    [InlineData("dock")]
    [InlineData("phone")]
    [InlineData("video")]
    public async Task SeekRailShowsTheTimeLeftWithARealMinusSignAndSeekCommitsOnRelease(string surface)
    {
        var changes = new List<double>();
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180).Add(c => c.Position, 30).Add(c => c.Surface, surface)
            .Add(c => c.ChapterTicks, new double[] { 0, 60, 120 }).Add(c => c.ChapterLabel, t => $"Chapter {t / 60 + 1}")
            .Add(c => c.ValueChanged, value => changes.Add(value)));
        Assert.Equal("\u22122:30", cut.Find(".playback-seek-rail__end").TextContent);
        Assert.Equal("0:30", cut.Find(".playback-seek-rail__start").TextContent);
        Assert.Equal("0:30 of 3:00", cut.Find("input").GetAttribute("aria-valuetext"));
        Assert.Empty(cut.FindAll(".playback-range-slider__labels"));
        Assert.Empty(cut.FindAll("button.playback-seek-rail__end"));
        Assert.Empty(changes);
        await cut.Find("input").InputAsync(new ChangeEventArgs { Value = "65" });
        Assert.Empty(changes);
        await cut.Find("input").ChangeAsync(new ChangeEventArgs { Value = "65" });
        Assert.Equal(new double[] { 65 }, changes);
        cut.Render(p => p.Add(c => c.Position, 65));
        Assert.Equal("1:05", cut.Find(".playback-seek-rail__start").TextContent);
        Assert.Equal("\u22121:55", cut.Find(".playback-seek-rail__end").TextContent);
    }

    [Fact]
    public async Task TouchRateOpensOnTapAndSelectionClosesWithoutFavoriting()
    {
        bool? choice = null;
        var cut = Render<MediaRateControl>(p => p.Add(c => c.Selected, selected => choice = selected));
        await cut.Find(".media-rate-control").TriggerEventAsync("onpointerenter", new PointerEventArgs { PointerType = "touch" });
        Assert.Equal("false", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
        await cut.Find("button[aria-label=Rate]").ClickAsync();
        Assert.Equal("true", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
        await cut.Find("button[aria-label='I like this']").ClickAsync();
        Assert.True(choice); Assert.Equal("false", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
        Assert.Empty(cut.FindAll("button[aria-label*=Favorites]"));
    }

    [Fact]
    public async Task RatingFocusWithinChoicesStaysOpenAndEscapeCloses()
    {
        JSInterop.Setup<bool>("tuvimaMenu.hasFocus", _ => true).SetResult(true);
        var cut = Render<MediaRateControl>();
        await cut.Find("button[aria-label=Rate]").ClickAsync();
        await cut.Find(".media-rate-control").TriggerEventAsync("onfocusout", new FocusEventArgs());
        Assert.Equal("true", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
        await cut.Find(".media-rate-control").KeyDownAsync(new KeyboardEventArgs { Key = "ArrowRight" });
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "tuvimaMenu.move");
        await cut.Find(".media-rate-control").KeyDownAsync(new KeyboardEventArgs { Key = "Escape" });
        Assert.Equal("false", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
    }
    [Fact]
    public async Task HoverAndFocusBeforeClickDoNotCloseTheRatingChoices()
    {
        var cut = Render<MediaRateControl>();
        await cut.Find(".media-rate-control").TriggerEventAsync("onpointerenter", new PointerEventArgs { PointerType = "mouse" });
        await cut.Find(".media-rate-control").TriggerEventAsync("onfocusin", new FocusEventArgs());
        await cut.Find("button[aria-label=Rate]").ClickAsync();
        Assert.Equal("true", cut.Find("button[aria-label=Rate]").GetAttribute("aria-expanded"));
    }

}
