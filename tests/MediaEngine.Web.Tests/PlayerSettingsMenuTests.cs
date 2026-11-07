using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Tests;

// Unified player D9, D10: the audiobook Settings menu (music and video have none). Playback speed has its own panel (unified player v2 D15).
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
    public void ControlBar_NoLongerHasAStandalonePlaybackSpeedButton()
    {
        var host = LocalizedSource.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));

        Assert.DoesNotContain("<PlaybackSpeedPanel", host, StringComparison.Ordinal);
        // Unified player v2 D12: the Audio & subtitles button shows its on-state only while subtitles are on (speed belongs to Playback settings).
        Assert.Contains("TriggerClass=\"@(_captionTracks.Any(track => track.Selected) ? \"is-on\" : null)\"", host, StringComparison.Ordinal);
    }

    [Fact]
    public void VideoMenu_HasNoRows_SpeedHasItsOwnPanelAndAudioSubtitlesAndStylingTheirs()
    {
        var cut = Render<PlayerSettingsMenu>();

        Assert.Empty(cut.FindAll("[data-settings-row]"));
        Assert.Empty(cut.FindAll(".player-settings-menu__switch"));
    }

    [Fact]
    public void AudiobookMenu_ListsTheSleepTimerAndTheBookmarkSwitchOnly_NeverPlaybackSpeedOrCustomSpeed()
    {
        var cut = RenderAudiobookMenu();

        Assert.Equal(["sleep"], cut.FindAll("[data-settings-row]").Select(row => row.GetAttribute("data-settings-row")));
        Assert.Single(cut.FindAll(".player-settings-menu__switch"));
        Assert.Contains("Show bookmarks on the bar", cut.Find(".player-settings-menu__switch").TextContent);
        Assert.DoesNotContain("Custom speed", cut.Markup);
        Assert.DoesNotContain("Playback speed", cut.Markup);
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
    public async Task SleepTimerPage_ListsTheChoicesAndPickingALengthSendsItAndReturnsToSettings()
    {
        var picked = new List<AudiobookSleepTimerSelectionDto>();
        var cut = RenderAudiobookMenu(picks: picked);

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
        var cut = RenderAudiobookMenu(picks: picked, sleep: running);

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
        var cut = RenderAudiobookMenu(picks: picked, availability: new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = false, CanEndNext = false });

        await cut.Find("[data-settings-row='sleep']").ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.True(cut.Find($"[data-sleep-choice='{AudiobookSleepTimerModes.EndCurrent}']").HasAttribute("disabled"));
        Assert.Empty(picked);
    }

    [Fact]
    public void BookmarkSwitch_ReportsAChangeToTheHost()
    {
        var changes = new List<bool>();
        var cut = RenderAudiobookMenu(showBookmarks: false, bookmarkChanges: changes);
        var toggle = cut.Find(".player-settings-menu__switch input[role='switch']");
        Assert.False(toggle.HasAttribute("checked"));

        toggle.Change(true);

        Assert.Equal([true], changes);
    }

    [Fact]
    public void BookmarkSwitch_IsOnWhenTheProfileTurnedTheBarMarkersOn()
    {
        var cut = RenderAudiobookMenu(showBookmarks: true);

        Assert.True(cut.Find(".player-settings-menu__switch input[role='switch']").HasAttribute("checked"));
    }

    private IRenderedComponent<PlayerSettingsMenu> RenderAudiobookMenu(
        List<AudiobookSleepTimerSelectionDto>? picks = null,
        AudiobookSleepTimerStateDto? sleep = null,
        AudiobookSleepTimerAvailabilityDto? availability = null,
        bool showBookmarks = false,
        List<bool>? bookmarkChanges = null) =>
        Render<PlayerSettingsMenu>(parameters => parameters
            .Add(component => component.Experience, PlaybackExperience.Audiobook)
            .Add(component => component.SleepTimerState, sleep ?? new AudiobookSleepTimerStateDto())
            .Add(component => component.SleepTimerAvailability, availability ?? new AudiobookSleepTimerAvailabilityDto { CanEndCurrent = true, CanEndNext = true })
            .Add(component => component.SleepTimerOptionsMinutes, [15, 30, 45])
            .Add(component => component.SleepTimerChanged, EventCallback.Factory.Create<AudiobookSleepTimerSelectionDto>(this, value => picks?.Add(value)))
            .Add(component => component.ShowBookmarksOnBar, showBookmarks)
            .Add(component => component.ShowBookmarksOnBarChanged, EventCallback.Factory.Create<bool>(this, value => bookmarkChanges?.Add(value))));

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
