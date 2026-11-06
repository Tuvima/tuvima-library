using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

// Unified player D10: the audiobook player has one Settings gear (Playback speed, Sleep timer, Show bookmarks on the bar) in place of the
// separate Speed and Sleep buttons, and the bar order is Chapters, Settings, Bookmarks (player-controls-v3 D7).
public sealed class AudiobookSettingsGearTests : AsyncBunitContext
{
    public AudiobookSettingsGearTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    [Fact]
    public void ControlStrip_DrawsTheSettingsGearBetweenChaptersAndBookmarksWithNoSpeedOrSleepButton()
    {
        var controls = PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState(HasChapters: true, PlaybackRate: 1.5d));

        var cut = Render<PlaybackControlStrip>(parameters => parameters
            .Add(component => component.Controls, controls)
            .Add(component => component.SettingsContent, control => builder =>
            {
                builder.OpenElement(0, "span");
                builder.AddAttribute(1, "data-gear", control.IsActive ? "on" : "off");
                builder.CloseElement();
            }));

        Assert.Equal("on", cut.Find("[data-gear]").GetAttribute("data-gear"));
        var order = cut.FindAll("[data-gear], button").Select(element => element.HasAttribute("data-gear") ? "gear" : element.GetAttribute("aria-label")).ToList();
        Assert.True(order.IndexOf("gear") > order.IndexOf("Chapters"), "Settings should follow Chapters");
        Assert.True(order.IndexOf("gear") < order.IndexOf("Bookmark"), "Settings should come before Bookmarks");
        Assert.Empty(cut.FindAll("[aria-label^='Playback speed'], .playback-sleep-timer, [aria-label='Speed'], [aria-label='Sleep timer']"));
    }

    [Fact]
    public void Gear_UnderlinesOnlyWhileTheSpeedIsNotNormalOrASleepTimerIsRunning()
    {
        var normal = RenderGear(rate: 1d, sleep: new AudiobookSleepTimerStateDto(), isOn: false);
        var faster = RenderGear(rate: 1.25d, sleep: new AudiobookSleepTimerStateDto(), isOn: true);

        Assert.Empty(normal.FindAll(".playback-popover-trigger.is-on"));
        Assert.Single(faster.FindAll(".playback-popover-trigger.is-on"));
        Assert.Equal("Settings", normal.Find(".playback-popover-trigger").GetAttribute("aria-label"));
    }

    [Fact]
    public async Task OpeningTheGear_ShowsTheAudiobookMenuAndAChosenSleepLengthReachesTheHost()
    {
        Render<AppPopoverHost>();
        var picked = new List<AudiobookSleepTimerSelectionDto>();
        var cut = Render<AudiobookSettingsPopover>(parameters => parameters
            .Add(component => component.SleepTimerState, new AudiobookSleepTimerStateDto())
            .Add(component => component.SleepTimerOptionsMinutes, [15])
            .Add(component => component.SleepTimerChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, value => picked.Add(value)))
            .Add(component => component.PlaybackRateChanged, EventCallback.Factory.Create<double>(this, _ => { }))
            .Add(component => component.ShowBookmarksOnBarChanged, EventCallback.Factory.Create<bool>(this, _ => { })));

        var popover = cut.FindComponent<PlaybackPopover>();
        await cut.InvokeAsync(() => popover.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-settings-row='sleep']")));

        await cut.Find("[data-settings-row='sleep']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find("[data-sleep-choice='minutes:15']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(15, Assert.Single(picked).Minutes);
    }

    [Fact]
    public void BarMarkers_AreHiddenUnlessTheProfileTurnedThemOn()
    {
        var saved = new[] { Bookmark(100), Bookmark(500, isGlobal: true) };

        Assert.Empty(AudiobookBarMarkers.ForBar(saved, show: false, duration: 1000, scope: null));
        var shown = AudiobookBarMarkers.ForBar(saved, show: true, duration: 1000, scope: null);
        Assert.Equal([100d, 500d], shown.Select(marker => marker.Seconds));
        Assert.True(shown[1].IsGlobal);
    }

    [Fact]
    public void BarMarkers_InTheChapterViewKeepOnlyThatChaptersMarkersAndMapThemToTimeIntoTheChapter()
    {
        var saved = new[] { Bookmark(100), Bookmark(450), Bookmark(900) };
        var chapter = new AudiobookChapterWindow(2, 3, "Two", Start: 400, End: 800);

        var shown = AudiobookBarMarkers.ForBar(saved, show: true, duration: 1000, scope: chapter);

        var marker = Assert.Single(shown);
        Assert.Equal(50d, marker.Seconds);
        Assert.Equal(450d, AudiobookBarMarkers.ToAbsolute(marker, chapter));
    }

    [Fact]
    public void SleepChoices_RowValueSaysOffTheChosenLengthOrTheChapterBoundary()
    {
        Assert.Equal("Off", PlaybackSleepTimerChoices.RowValue(new AudiobookSleepTimerStateDto()));
        Assert.Equal("30 min", PlaybackSleepTimerChoices.RowValue(new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.Timer, ChosenMinutes = 30 }));
        Assert.Equal("End of chapter", PlaybackSleepTimerChoices.RowValue(new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.EndCurrent }));
        Assert.Null(PlaybackSleepTimerChoices.SelectionFor(AudiobookSleepTimerModes.EndNext, new AudiobookSleepTimerAvailabilityDto { CanEndNext = false }));
        Assert.Equal(AudiobookSleepTimerModes.Off, PlaybackSleepTimerChoices.SelectionFor(AudiobookSleepTimerModes.Off, new AudiobookSleepTimerAvailabilityDto())!.Mode);
    }

    private IRenderedComponent<AudiobookSettingsPopover> RenderGear(double rate, AudiobookSleepTimerStateDto sleep, bool isOn) =>
        Render<AudiobookSettingsPopover>(parameters => parameters
            .Add(component => component.PlaybackRate, rate)
            .Add(component => component.SleepTimerState, sleep)
            .Add(component => component.IsOn, isOn));

    private static AudiobookBookmarkDto Bookmark(double seconds, bool isGlobal = false) => new()
    {
        Id = Guid.NewGuid(),
        PositionSeconds = seconds,
        IsGlobal = isGlobal,
    };
}
