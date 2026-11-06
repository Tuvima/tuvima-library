using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

// Unified player D9, D10: the one Settings menu shared by video and audiobook (music has none). Playback speed lives inside it, not as a button on the control bar.
public sealed class PlayerSettingsMenuTests : AsyncBunitContext
{
    public PlayerSettingsMenuTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddNativeUiServices();
    }

    [Fact]
    public void SettingsMainPage_ListsPlaybackSpeedWithTheCurrentRateOnTheRight()
    {
        var cut = RenderMenu(1.25d);

        var row = cut.Find("[data-settings-row='speed']");
        Assert.Contains("Playback speed", row.QuerySelector(".player-settings-menu__row-label")!.TextContent);
        Assert.Contains("1.25x", row.QuerySelector(".player-settings-menu__row-value")!.TextContent);
    }

    [Fact]
    public void SettingsMainPage_ShowsOneDotZeroForTheDefaultRate()
    {
        var cut = RenderMenu(1d);

        Assert.Contains("1.0x", cut.Find("[data-settings-row='speed'] .player-settings-menu__row-value").TextContent);
    }

    [Fact]
    public void SettingsMainPage_HidesPlaybackSpeedWhenTheHostDoesNotListen()
    {
        var cut = Render<PlayerSettingsMenu>();

        Assert.Empty(cut.FindAll("[data-settings-row='speed']"));
    }

    [Fact]
    public async Task PlaybackSpeedRow_OpensTheSpeedPageWithBackAndCloseAndBackReturnsToSettings()
    {
        var cut = RenderMenu(1.5d);

        await cut.Find("[data-settings-row='speed']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("main", cut.Find(".player-settings-menu").GetAttribute("data-settings-page"));
        Assert.Equal("Playback speed", cut.Find(".player-settings-menu__header h3").TextContent);
        Assert.Equal("1.5x", cut.Find(".player-settings-menu__speed output").TextContent);
        Assert.NotNull(cut.Find(".player-settings-menu__header button[aria-label='Close']"));

        await cut.Find(".player-settings-menu__header button[aria-label='Back']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("Settings", cut.Find(".player-settings-menu__header h3").TextContent);
        Assert.Single(cut.FindAll("[data-settings-row='speed']"));
    }

    [Fact]
    public async Task ResetToOneX_ReportsTheDefaultRateToTheHost()
    {
        var rates = new List<double>();
        var cut = RenderMenu(2d, rates);

        await cut.Find("[data-settings-row='speed']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        await cut.Find(".player-settings-menu__speed button[aria-label='Reset playback speed to 1×']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal([1d], rates);
    }

    [Fact]
    public void ControlBar_NoLongerHasAStandalonePlaybackSpeedButton()
    {
        var host = LocalizedSource.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));

        Assert.DoesNotContain("<PlaybackSpeedControl", host, StringComparison.Ordinal);
        Assert.Contains("PlaybackRateChanged=\"SelectPlaybackRateAsync\"", host, StringComparison.Ordinal);
        // The gear shows its on-state when speed is not 1x or captions are on.
        Assert.Contains("Math.Abs(Playback.PlaybackRate - 1d) > 0.001 ? \"is-on\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoMenu_ListsAudioSubtitlesPlaybackSpeedAndStylingButNoSleepTimerOrBookmarkSwitch()
    {
        var cut = RenderMenu(1d);

        Assert.Equal(["audio", "subtitles", "speed", "styling"], cut.FindAll("[data-settings-row]").Select(row => row.GetAttribute("data-settings-row")));
        Assert.Empty(cut.FindAll(".player-settings-menu__switch"));
    }

    [Fact]
    public void AudiobookMenu_ListsPlaybackSpeedSleepTimerAndTheBookmarkSwitchOnly()
    {
        var cut = RenderAudiobookMenu(1.5d);

        Assert.Equal(["speed", "sleep"], cut.FindAll("[data-settings-row]").Select(row => row.GetAttribute("data-settings-row")));
        Assert.Single(cut.FindAll(".player-settings-menu__switch"));
        Assert.Contains("Show bookmarks on the bar", cut.Find(".player-settings-menu__switch").TextContent);
        Assert.Contains("1.5x", cut.Find("[data-settings-row='speed'] .player-settings-menu__row-value").TextContent);
        Assert.Contains("Off", cut.Find("[data-settings-row='sleep'] .player-settings-menu__row-value").TextContent);
    }

    [Fact]
    public void MusicMenu_HasNoRows()
    {
        var cut = Render<PlayerSettingsMenu>(parameters => parameters.Add(component => component.Experience, PlaybackExperience.Music));

        Assert.Empty(cut.FindAll("[data-settings-row]"));
        Assert.Empty(cut.FindAll(".player-settings-menu__switch"));
    }

    [Fact]
    public async Task AudiobookSpeedPage_SharesTheVideoSliderAndResetReportsTheDefaultRate()
    {
        var rates = new List<double>();
        var cut = RenderAudiobookMenu(2d, rates: rates);

        await cut.Find("[data-settings-row='speed']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("Playback speed", cut.Find(".player-settings-menu__header h3").TextContent);
        Assert.Equal("2.0x", cut.Find(".player-settings-menu__speed output").TextContent);
        await cut.Find(".player-settings-menu__speed button[aria-label='Reset playback speed to 1×']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Equal([1d], rates);
    }

    [Fact]
    public async Task SleepTimerPage_ListsTheChoicesAndPickingALengthSendsItAndReturnsToSettings()
    {
        var picked = new List<AudiobookSleepTimerSelectionDto>();
        var cut = RenderAudiobookMenu(1d, picks: picked);

        await cut.Find("[data-settings-row='sleep']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal("Sleep timer", cut.Find(".player-settings-menu__header h3").TextContent);
        var choices = cut.FindAll("[data-sleep-choice]").Select(row => row.GetAttribute("data-sleep-choice")).ToList();
        Assert.Contains(AudiobookSleepTimerModes.Off, choices);
        Assert.Contains("minutes:15", choices);
        Assert.Contains(AudiobookSleepTimerModes.EndCurrent, choices);
        Assert.Contains(AudiobookSleepTimerModes.EndNext, choices);

        await cut.Find("[data-sleep-choice='minutes:30']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var selection = Assert.Single(picked);
        Assert.Equal(AudiobookSleepTimerModes.Timer, selection.Mode);
        Assert.Equal(30, selection.Minutes);
        Assert.Equal("Settings", cut.Find(".player-settings-menu__header h3").TextContent);
    }

    [Fact]
    public async Task SleepTimerPage_MarksTheRunningTimerAndOffCancelsIt()
    {
        var picked = new List<AudiobookSleepTimerSelectionDto>();
        var running = new AudiobookSleepTimerStateDto { Mode = AudiobookSleepTimerModes.Timer, ChosenMinutes = 45, DeadlineUtc = DateTimeOffset.UtcNow.AddMinutes(20) };
        var cut = RenderAudiobookMenu(1d, picks: picked, sleep: running);

        Assert.Contains("min left", cut.Find("[data-settings-row='sleep'] .player-settings-menu__row-value").TextContent);
        await cut.Find("[data-settings-row='sleep']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());
        Assert.Equal("true", cut.Find("[data-sleep-choice='minutes:45']").GetAttribute("aria-current"));

        await cut.Find($"[data-sleep-choice='{AudiobookSleepTimerModes.Off}']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(AudiobookSleepTimerModes.Off, Assert.Single(picked).Mode);
    }

    [Fact]
    public async Task SleepTimerPage_DisablesEndOfChapterWhenNoVerifiedBoundaryExistsAndSendsNothingForIt()
    {
        var picked = new List<AudiobookSleepTimerSelectionDto>();
        var cut = RenderAudiobookMenu(1d, picks: picked, availability: new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = false, CanEndNext = false });

        await cut.Find("[data-settings-row='sleep']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(cut.Find($"[data-sleep-choice='{AudiobookSleepTimerModes.EndCurrent}']").HasAttribute("disabled"));
        Assert.Empty(picked);
    }

    [Fact]
    public void BookmarkSwitch_ReportsAChangeToTheHost()
    {
        var changes = new List<bool>();
        var cut = RenderAudiobookMenu(1d, showBookmarks: false, bookmarkChanges: changes);
        var toggle = cut.Find(".player-settings-menu__switch input[role='switch']");
        Assert.False(toggle.HasAttribute("checked"));

        toggle.Change(true);

        Assert.Equal([true], changes);
    }

    [Fact]
    public void BookmarkSwitch_IsOnWhenTheProfileTurnedTheBarMarkersOn()
    {
        var cut = RenderAudiobookMenu(1d, showBookmarks: true);

        Assert.True(cut.Find(".player-settings-menu__switch input[role='switch']").HasAttribute("checked"));
    }

    private IRenderedComponent<PlayerSettingsMenu> RenderAudiobookMenu(
        double rate,
        List<double>? rates = null,
        List<AudiobookSleepTimerSelectionDto>? picks = null,
        AudiobookSleepTimerStateDto? sleep = null,
        AudiobookSleepTimerAvailabilityDto? availability = null,
        bool showBookmarks = false,
        List<bool>? bookmarkChanges = null) =>
        Render<PlayerSettingsMenu>(parameters => parameters
            .Add(component => component.Experience, PlaybackExperience.Audiobook)
            .Add(component => component.PlaybackRate, rate)
            .Add(component => component.PlaybackRateChanged, EventCallback.Factory.Create<double>(this, value => rates?.Add(value)))
            .Add(component => component.SleepTimerState, sleep ?? new AudiobookSleepTimerStateDto())
            .Add(component => component.SleepTimerAvailability, availability ?? new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = true, CanEndNext = true })
            .Add(component => component.SleepTimerOptionsMinutes, [15, 30, 45])
            .Add(component => component.SleepTimerChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, value => picks?.Add(value)))
            .Add(component => component.ShowBookmarksOnBar, showBookmarks)
            .Add(component => component.ShowBookmarksOnBarChanged, EventCallback.Factory.Create<bool>(this, value => bookmarkChanges?.Add(value))));

    private IRenderedComponent<PlayerSettingsMenu> RenderMenu(double rate, List<double>? rates = null) =>
        Render<PlayerSettingsMenu>(parameters => parameters
            .Add(component => component.PlaybackRate, rate)
            .Add(component => component.PlaybackRateChanged, EventCallback.Factory.Create<double>(this, value => rates?.Add(value))));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
