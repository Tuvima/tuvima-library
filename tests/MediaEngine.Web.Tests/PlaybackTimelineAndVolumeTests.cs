using Bunit;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Resources;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

namespace MediaEngine.Web.Tests;

/// <summary>Unified player v2 D9 and D10 (work packet WP3): the timeline, the audiobook labels, the volume control and the shared slider style.</summary>
public sealed class PlaybackTimelineAndVolumeTests : AsyncBunitContext
{
    private const string Minus = "−";

    private IStringLocalizer<SharedStrings> L => Services.GetRequiredService<IStringLocalizer<SharedStrings>>();

    public PlaybackTimelineAndVolumeTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddSingleton(new PlaybackTransientToolCoordinator());
        Render<AppPopoverHost>();
    }

    // ---- Timeline (D9) ----

    [Fact]
    public void Timeline_ShowsElapsedOnTheLeftAndTimeLeftWithARealMinusSignOnTheRight()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 7860).Add(c => c.Position, 143).Add(c => c.Surface, "dock"));

        Assert.Equal("2:23", cut.Find(".playback-seek-rail__start").TextContent);
        Assert.Equal(Minus + "2:08:37", cut.Find(".playback-seek-rail__end").TextContent);
        Assert.StartsWith("−", cut.Find(".playback-seek-rail__end").TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("-", cut.Find(".playback-seek-rail__end").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_WithoutALoadedDuration_IsDisabledAndShowsADash()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 0));

        Assert.Equal("—", cut.Find(".playback-seek-rail__end").TextContent);
        Assert.NotNull(cut.Find("input[type=range]").GetAttribute("disabled"));
    }

    [Fact]
    public void Timeline_UsesThePlayerSliderStyle()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180).Add(c => c.Position, 30).Add(c => c.Surface, "video"));

        Assert.Contains("playback-range-slider--player", cut.Find(".playback-range-slider").ClassName, StringComparison.Ordinal);
        Assert.Single(cut.FindAll(".playback-range-slider__thumb"));
    }

    [Theory]
    [InlineData(-5, 180, 0)]
    [InlineData(0, 180, 0)]
    [InlineData(95, 180, 95)]
    [InlineData(180, 180, 180)]
    [InlineData(400, 180, 180)]
    [InlineData(double.NaN, 180, 0)]
    [InlineData(double.PositiveInfinity, 180, 0)]
    [InlineData(30, 0, 0)]
    public void Timeline_SeekIsClampedToTheLoadedDuration(double asked, double duration, double expected)
    {
        Assert.Equal(expected, PlaybackSeekRail.ClampSeek(asked, duration));
    }

    [Fact]
    public async Task Timeline_ReleasingTheHandlePastTheEndSeeksToTheEnd()
    {
        var seeks = new List<double>();
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180).Add(c => c.Position, 30).Add(c => c.ValueChanged, v => seeks.Add(v)));

        await cut.Find("input").ChangeAsync(new ChangeEventArgs { Value = "9999" });

        Assert.Equal(new double[] { 180 }, seeks);
    }

    [Fact]
    public void Timeline_ScrubbingHasATimeLabelAboveTheHandle()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180));

        // The label is filled in by playback-seek-rail.js while the handle is dragged; it sits in the track, above the slider.
        var track = cut.Find(".playback-seek-rail__track");
        Assert.NotNull(track.QuerySelector("[data-seek-time]"));
        var css = SourceText("Components/Shared/PlaybackSeekRail.razor.css");
        Assert.Contains(".playback-seek-rail__bubble { position:absolute; bottom:calc(100% + 2px);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_BookmarksAreTwoByTenPixelTicksUnderTheRailInTheMarkerColors()
    {
        var own = new PlaybackRailBookmark(Guid.NewGuid(), 60, false, "Mine", null);
        var shared = new PlaybackRailBookmark(Guid.NewGuid(), 120, true, "Everyone's", null);
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 240).Add(c => c.Bookmarks, [own, shared]));

        var ticks = cut.FindAll(".playback-seek-rail__bookmark");
        Assert.Equal(2, ticks.Count);
        Assert.DoesNotContain("is-global", ticks[0].ClassName, StringComparison.Ordinal);
        Assert.Contains("is-global", ticks[1].ClassName, StringComparison.Ordinal);
        Assert.Contains("left:25%", cut.FindAll(".playback-seek-rail__bookmark-wrap")[0].GetAttribute("style"), StringComparison.Ordinal);
        Assert.Contains("left:50%", cut.FindAll(".playback-seek-rail__bookmark-wrap")[1].GetAttribute("style"), StringComparison.Ordinal);

        var css = SourceText("Components/Shared/PlaybackSeekRail.razor.css");
        Assert.Contains("width:2px; height:10px;", css, StringComparison.Ordinal);
        Assert.Contains("background:var(--playback-marker)", css, StringComparison.Ordinal);
        Assert.Contains(".playback-seek-rail__bookmark.is-global { background:var(--playback-marker-shared); }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_BookmarkTicksHideWhileTheHandleIsDragged()
    {
        // playback-seek-rail.js puts data-dragging on the rail while the handle is held (see the JavaScript test); the stylesheet hides the ticks then.
        var css = SourceText("Components/Shared/PlaybackSeekRail.razor.css");

        Assert.Contains(".playback-seek-rail[data-dragging=\"true\"] .playback-seek-rail__bookmarks { visibility:hidden; }", css, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_RailIsFourPixelsWithATwelvePixelHandleAndA24PixelHitArea()
    {
        var tokens = SourceText("wwwroot/tuvima.tokens.css");
        var slider = SourceText("Components/Shared/AppRangeSlider.razor.css");

        Assert.Contains("--playback-slider-rail-height: 4px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--playback-slider-handle: 12px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--playback-slider-hit: 24px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--playback-rail: rgba(255, 255, 255, 0.22);", tokens, StringComparison.Ordinal);
        Assert.Contains(".playback-range-slider--player .playback-range-slider__thumb {", slider, StringComparison.Ordinal);
        Assert.Contains("box-shadow: none;", slider, StringComparison.Ordinal);
    }

    [Fact]
    public void Timeline_ShellNoLongerOffsetsTheRailWithInterimOverrides()
    {
        var shell = SourceText("Components/Shared/PlaybackShell.razor.css");

        Assert.DoesNotContain(".playback-seek-rail--dock", shell, StringComparison.Ordinal);
        Assert.Contains("height: 28px;", shell, StringComparison.Ordinal);
    }

    [Fact]
    public void PlayerSurface_OnlyRestylesTheSlidersThatAskForIt()
    {
        var plain = Render<AppRangeSlider>(p => p.Add(c => c.Min, 0).Add(c => c.Max, 1).Add(c => c.Value, 0.5).Add(c => c.ShowTicks, false));
        var player = Render<AppRangeSlider>(p => p.Add(c => c.Min, 0).Add(c => c.Max, 1).Add(c => c.Value, 0.5).Add(c => c.ShowTicks, false).Add(c => c.Surface, "player"));

        Assert.DoesNotContain("--player", plain.Find(".playback-range-slider").ClassName, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider--player", player.Find(".playback-range-slider").ClassName, StringComparison.Ordinal);
    }

    // ---- Audiobook labels (D9) ----

    [Fact]
    public void AudiobookLabel_InChapterMode_ReadsChapterElapsedAndTimeLeft()
    {
        Assert.Equal("Chapter 8", PlaybackTimelineMetaRow.ChapterLabel(L, 8));
        Assert.Equal($"Chapter 8 · 12:08 · {Minus}18:32", PlaybackTimelineMetaRow.ChapterSummary(L, 8, 12 * 60 + 8, 18 * 60 + 32));
    }

    [Fact]
    public void AudiobookLabel_InWholeBookMode_ReadsBookPercentAndTimeLeft()
    {
        const double book = 8 * 3600 + 30 * 60; // 8 h 30 m
        const double position = book * 0.32;
        Assert.Equal("Book · 32%", PlaybackTimelineMetaRow.BookLabel(L, position, book));
        Assert.Equal("Book · 32% · 5h 46m left", PlaybackTimelineMetaRow.BookSummary(L, position, book));
    }

    [Theory]
    [InlineData(22320, "6h 12m left")]
    [InlineData(3600, "1h 0m left")]
    [InlineData(2520, "42m left")]
    [InlineData(59, "Less than a minute left")]
    [InlineData(-5, "Less than a minute left")]
    public void AudiobookLabel_TimeLeftIsHoursAndMinutes(double seconds, string expected)
    {
        Assert.Equal(expected, PlaybackTimelineMetaRow.TimeLeft(L, seconds));
    }

    [Fact]
    public void AudiobookLabel_PercentRoundsDownSoItReadsOneHundredOnlyWhenTheBookIsDone()
    {
        Assert.Equal("Book · 99%", PlaybackTimelineMetaRow.BookLabel(L, 999, 1000));
        Assert.Equal("Book · 100%", PlaybackTimelineMetaRow.BookLabel(L, 1000, 1000));
        Assert.Equal("Book · 0%", PlaybackTimelineMetaRow.BookLabel(L, 5, 0));
    }

    [Fact]
    public void Timeline_ShowsTheAudiobookLabelBetweenElapsedAndTheRailAndSpeaksTheWholeLine()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 3000).Add(c => c.Position, 728)
            .Add(c => c.Label, PlaybackTimelineMetaRow.ChapterLabel(L, 8))
            .Add(c => c.Summary, PlaybackTimelineMetaRow.ChapterSummary(L, 8, 728, 2272)));

        var rail = cut.Find("[data-playback-seek-rail]");
        Assert.Equal("group", rail.GetAttribute("role"));
        Assert.Equal($"Chapter 8 · 12:08 · {Minus}37:52", rail.GetAttribute("aria-label"));
        var order = rail.Children.Select(child => child.ClassName).ToList();
        Assert.True(order.IndexOf("playback-seek-rail__start") < order.FindIndex(name => name!.Contains("playback-seek-rail__label", StringComparison.Ordinal)));
        Assert.True(order.FindIndex(name => name!.Contains("playback-seek-rail__label", StringComparison.Ordinal)) < order.FindIndex(name => name!.Contains("playback-seek-rail__track", StringComparison.Ordinal)));
        Assert.Equal("Chapter 8", cut.Find(".playback-seek-rail__label").TextContent.Trim());
    }

    [Fact]
    public void Timeline_WholeBookModeShowsItsOwnTextOnTheRight()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 30000).Add(c => c.Position, 9600)
            .Add(c => c.Label, "Book · 32%").Add(c => c.EndText, "5h 40m left"));

        Assert.Equal("5h 40m left", cut.Find(".playback-seek-rail__end").TextContent);
    }

    [Fact]
    public async Task Timeline_TheAudiobookLabelSwitchesChapterAndBookWhenTheHostListens()
    {
        var presses = 0;
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 3000).Add(c => c.Label, "Chapter 8")
            .Add(c => c.LabelClick, EventCallback.Factory.Create(this, () => presses++)));

        await cut.Find("button.playback-seek-rail__label").ClickAsync();

        Assert.Equal(1, presses);
    }

    [Fact]
    public void Timeline_WithoutALabelDrawsNoLabelOrGroup()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 3000));

        Assert.Empty(cut.FindAll(".playback-seek-rail__label"));
        Assert.Null(cut.Find("[data-playback-seek-rail]").GetAttribute("role"));
    }

    // ---- Volume (D10) ----

    [Theory]
    [InlineData(true, 0.8, "muted")]
    [InlineData(true, 0.0, "muted")]
    [InlineData(false, 0.0, "quiet")]
    [InlineData(false, 0.49, "quiet")]
    [InlineData(false, 0.5, "loud")]
    [InlineData(false, 1.0, "loud")]
    public void Volume_SpeakerHasThreeStates(bool muted, double volume, string expected)
    {
        Assert.Equal(expected, PlaybackVolumeControl.Level(muted, volume));
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, volume).Add(c => c.IsMuted, muted).Add(c => c.Surface, "dock").Add(c => c.Touch, false));
        var glyph = cut.Find("[data-playback-glyph]").GetAttribute("data-playback-glyph");
        Assert.Equal(expected switch { "muted" => "Mute", "quiet" => "VolumeLow", _ => "Volume" }, glyph);
        Assert.Equal(expected, cut.Find("[data-playback-volume]").GetAttribute("data-volume-level"));
    }

    [Fact]
    public async Task Volume_ClickingTheSpeakerTogglesMute_AndTheHostRestoresThePreviousLevel()
    {
        var toggles = 0;
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.6).Add(c => c.Surface, "dock").Add(c => c.Touch, false)
            .Add(c => c.OnToggleMute, EventCallback.Factory.Create(this, () => toggles++)));

        await cut.Find(".playback-volume__button button").ClickAsync();

        Assert.Equal(1, toggles);
    }

    [Fact]
    public void Volume_UnmutingRestoresTheLevelFromBeforeTheMute()
    {
        // The session remembers the last audible level (PlaybackSessionController.LastAudibleVolume): muting goes to zero, unmuting goes back to it.
        var session = new PlaybackSessionController(null!, null!);
        session.UpdateTransportState(volume: 0.37d, isMuted: false);
        Assert.Equal(0d, session.GetMuteToggleTargetVolume());

        session.UpdateTransportState(volume: 0d, isMuted: true);
        Assert.Equal(0.37d, session.GetMuteToggleTargetVolume());
    }

    [Fact]
    public void Volume_SliderIsHorizontalNinetySixPixelsAndSharesThePlayerSliderStyle()
    {
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.6).Add(c => c.Surface, "dock").Add(c => c.Touch, false));

        var input = cut.Find(".playback-volume__panel input[type=range]");
        Assert.Equal("horizontal", input.GetAttribute("aria-orientation"));
        Assert.Equal("60%", input.GetAttribute("aria-valuetext"));
        Assert.Contains("playback-range-slider--player", cut.Find(".playback-volume__panel .playback-range-slider").ClassName, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll("[aria-orientation='vertical']"));
        Assert.Contains("--playback-volume-slider-width: 96px;", SourceText("wwwroot/tuvima.tokens.css"), StringComparison.Ordinal);
    }

    [Fact]
    public void Volume_SliderOverlaysAZeroWidthSlotSoNothingElseInTheBarMoves()
    {
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.6).Add(c => c.Surface, "dock").Add(c => c.Touch, false));

        // The slot beside the speaker is always in the markup, closed (no data-open), and the slider lives inside it.
        var root = cut.Find("[data-playback-volume]");
        Assert.Null(root.GetAttribute("data-open"));
        Assert.Equal("hover", root.GetAttribute("data-volume-mode"));
        var slot = cut.Find("[data-volume-slot]");
        Assert.NotNull(slot.QuerySelector("input[type=range]"));
        Assert.Equal(root, slot.ParentElement);

        var css = SourceText("Components/Shared/PlaybackVolumeControl.razor.css");
        // The slot keeps no width, open or closed, and never clips; only the slider inside it is drawn over the bar.
        Assert.Contains("flex:none; width:0; height:0; visibility:hidden;", css, StringComparison.Ordinal);
        Assert.DoesNotContain("overflow:hidden", css, StringComparison.Ordinal);
        Assert.DoesNotContain("width:calc(var(--playback-volume-slider-width) + 20px)", css, StringComparison.Ordinal);
        var openSlot = css[css.IndexOf(".playback-volume[data-open=\"true\"] .playback-volume__slot", StringComparison.Ordinal)..];
        Assert.DoesNotContain("width:", openSlot[..openSlot.IndexOf('}', StringComparison.Ordinal)], StringComparison.Ordinal);
        var panelRule = css[css.IndexOf(".playback-volume__panel {", StringComparison.Ordinal)..];
        panelRule = panelRule[..panelRule.IndexOf('}', StringComparison.Ordinal)];
        Assert.Contains("position:absolute; left:0; top:50%", panelRule, StringComparison.Ordinal);
        Assert.Contains("z-index:", panelRule, StringComparison.Ordinal);
        Assert.Contains("background:var(--tl-bg-shell)", panelRule, StringComparison.Ordinal);
        Assert.DoesNotContain(":hover", css, StringComparison.Ordinal); // the reveal is the script's, so the 400 ms linger is real
    }

    [Fact]
    public void Tools_WhenTheyOverflowAreRightAlignedButScrollableFromTheirFirstTool()
    {
        var css = SourceText("Components/Shared/PlaybackShell.razor.css");
        var narrow = css[css.IndexOf("@media (max-width: 1180px)", StringComparison.Ordinal)..];
        narrow = narrow[..narrow.IndexOf("\n}", StringComparison.Ordinal)];
        Assert.Contains("overflow-x: auto", narrow, StringComparison.Ordinal);
        Assert.Contains("justify-content: flex-start", narrow, StringComparison.Ordinal);
        Assert.Contains("margin-inline-start: auto", narrow, StringComparison.Ordinal);
    }

    [Fact]
    public void Volume_OnADeviceWithNoSoftwareVolumeThereIsNoSlider()
    {
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Supported, false).Add(c => c.Surface, "dock").Add(c => c.Touch, true));

        Assert.Empty(cut.FindAll("input[type=range]"));
        Assert.Empty(cut.FindAll(".playback-panel"));
        Assert.Single(cut.FindAll(".playback-volume__button button"));
    }

    [Fact]
    public async Task Volume_OnTouchTheSpeakerOpensAVolumePanelInsteadOfMuting_AndMuteIsInsideIt()
    {
        var toggles = 0;
        var volumes = new List<double>();
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.4).Add(c => c.Surface, "dock").Add(c => c.Touch, true)
            .Add(c => c.OnToggleMute, EventCallback.Factory.Create(this, () => toggles++))
            .Add(c => c.OnVolumeChanged, EventCallback.Factory.Create<double>(this, volume => volumes.Add(volume))));

        // No hover slot on touch: the speaker is the Volume panel's trigger, and pressing it does not mute.
        Assert.Equal("panel", cut.Find("[data-playback-volume]").GetAttribute("data-volume-mode"));
        Assert.Empty(cut.FindAll("[data-volume-slot]"));
        var trigger = cut.Find("button.playback-volume__trigger");
        Assert.Equal("Volume", trigger.GetAttribute("aria-label"));
        Assert.Equal(0, toggles);

        var panel = cut.FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".playback-panel--menu")));

        Assert.Equal("Volume", panel.Instance.Title);
        var inside = cut.Find(".playback-volume-panel");
        await inside.QuerySelector("button")!.ClickAsync();
        Assert.Equal(1, toggles);
        Assert.Equal("40%", inside.QuerySelector("output")!.TextContent);
        await cut.Find(".playback-volume-panel input[type=range]").InputAsync(new ChangeEventArgs { Value = "0.75" });
        Assert.Equal(new[] { 0.75d }, volumes);
    }

    [Fact]
    public void Volume_DesktopHasNoVolumePanel()
    {
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.4).Add(c => c.Surface, "dock").Add(c => c.Touch, false));

        Assert.Empty(cut.FindAll("button.playback-volume__trigger"));
        Assert.Empty(cut.FindAll(".playback-volume-panel"));
    }

    [Fact]
    public void Volume_TheOldVerticalVolumeBoxIsGone()
    {
        var app = SourceText("wwwroot/app.css");
        var razor = SourceText("Components/Shared/PlaybackVolumeControl.razor");

        Assert.DoesNotContain("Vertical=\"true\"", razor, StringComparison.Ordinal);
        Assert.DoesNotContain("height: 168px", app, StringComparison.Ordinal);
        Assert.DoesNotContain(".playback-volume__panel .playback-range-slider", app, StringComparison.Ordinal);
    }

    private static string SourceText(string relativeToWeb)
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "MediaEngine.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, "src", "MediaEngine.Web", relativeToWeb));
    }

    [Fact]
    public void Volume_ArrowKeysOnTheSpeakerAreMarkedSoThePageDoesNotScrollUnderThem()
    {
        var cut = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.6).Add(c => c.Touch, false));
        Assert.Equal("true", cut.Find(".playback-volume__button").GetAttribute("data-volume-keys"));
        Assert.Contains("data-volume-keys", SourceText("wwwroot/js/playback-volume.js"), StringComparison.Ordinal);

        var fixedVolume = Render<PlaybackVolumeControl>(p => p.Add(c => c.Volume, 0.6).Add(c => c.Touch, false).Add(c => c.Supported, false));
        Assert.Null(fixedVolume.Find(".playback-volume__button").GetAttribute("data-volume-keys"));
    }

    [Fact]
    public void Timeline_TheClockReadoutsAreNotLiveRegionsSoAScreenReaderIsNotReadTheTimeEverySecond()
    {
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180).Add(c => c.Position, 30));

        Assert.Equal("off", cut.Find(".playback-seek-rail__start").GetAttribute("aria-live"));
        Assert.Equal("off", cut.Find(".playback-seek-rail__end").GetAttribute("aria-live"));
    }

    [Fact]
    public void AudiobookLabel_OnATouchScreenHasAFortyFourPixelHitArea()
    {
        var css = SourceText("Components/Shared/PlaybackSeekRail.razor.css");
        var coarse = css[css.IndexOf("@media (pointer: coarse)", StringComparison.Ordinal)..];

        Assert.Contains("playback-seek-rail__label--action", coarse, StringComparison.Ordinal);
        Assert.Contains("min-height:44px", coarse, StringComparison.Ordinal);
    }

    [Fact]
    public void AudiobookLabel_ReadsItsWordsFromTheStringTable()
    {
        Assert.Equal("Chapter 8", PlaybackTimelineMetaRow.ChapterLabel(L, 8));
        Assert.Equal("Less than a minute left", PlaybackTimelineMetaRow.TimeLeft(L, 30));
        var cut = Render<PlaybackSeekRail>(p => p.Add(c => c.Duration, 180).Add(c => c.Position, 30).Add(c => c.Label, "Chapter 8").Add(c => c.LabelClick, EventCallback.Factory.Create(this, () => { })));
        Assert.Equal("Chapter 8. Switch between the chapter and the whole book", cut.Find("button.playback-seek-rail__label").GetAttribute("aria-label"));
        Assert.Equal("Elapsed time 0:30", cut.Find(".playback-seek-rail__start").GetAttribute("aria-label"));
    }
}
