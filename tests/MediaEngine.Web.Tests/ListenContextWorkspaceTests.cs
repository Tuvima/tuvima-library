using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class ListenContextWorkspaceTests : AsyncBunitContext
{
    public ListenContextWorkspaceTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/context-sidebar.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
    }

    [Fact]
    public async Task PlaybackSpeedControlPreservesFractionalExternalAndResetValues()
    {
        var initial = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.25d)
            .Add(component => component.ResetValue, 1d)
            .Add(component => component.Presets, new[] { 1.25d }));

        Assert.Contains("1.25x", initial.Markup);
        Assert.Contains("aria-pressed=\"true\"", initial.Markup);

        var resetValues = new List<double>();
        var reset = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.75d)
            .Add(component => component.ResetValue, 1.25d)
            .Add(component => component.ValueChanged, EventCallback.Factory.Create<double>(this, value => resetValues.Add(value))));

        await reset.Find(".playback-speed-control__reset").ClickAsync();

        Assert.Equal(1.25d, Assert.Single(resetValues));
        Assert.Contains("1.25x", reset.Markup);
    }

    [Fact]
    public void QueueShowsTheCurrentItemBeforeUpcomingItemsInTheSharedRowLayout()
    {
        var current = CreateItem("Current song", "Artist");
        var upcoming = CreateItem("Up next", "Second artist");
        var playback = CreatePlayback(current, upcoming);
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());

        var cut = Render<ListenContextWorkspace>();

        var rows = cut.FindAll(".playback-context-row");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Current song", rows[0].QuerySelector(".playback-context-row__identity strong")?.TextContent);
        Assert.Contains("is-current", rows[0].ClassName);
        Assert.Equal("true", rows[0].GetAttribute("aria-current"));
        Assert.Equal("Up next", rows[1].QuerySelector(".playback-context-row__identity strong")?.TextContent);
    }

    [Fact]
    public async Task AudiobookHistoryShowsListeningSegmentInsteadOfWholeAssetDuration()
    {
        var item = CreateItem("Audiobook", "Narrator") with { MediaType = "Audiobook", AssetId = Guid.NewGuid() };
        var playback = CreatePlayback(item);
        var endedAt = DateTimeOffset.Now;
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            AudiobookHistory =
            [
                new AudiobookListenHistoryItemDto
                {
                    Id = Guid.NewGuid(),
                    WorkId = item.WorkId,
                    AssetId = item.AssetId!.Value,
                    Title = "Long audiobook",
                    ChapterTitle = "Chapter 3",
                    PositionSeconds = 3600,
                    DurationSeconds = 16 * 60 * 60,
                    StartedAt = endedAt.AddMinutes(-18),
                    EndedAt = endedAt,
                },
            ],
        });
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());
        await Services.GetRequiredService<ListenContextWorkspaceState>().TogglePanelAsync(playback, "history");

        var cut = Render<ListenContextWorkspace>();

        Assert.Contains("Elapsed session · 18:00", cut.Markup);
        Assert.DoesNotContain("16:00:00", cut.Markup);
    }

    [Fact]
    public async Task OldLyricsCompletionCannotReplaceNewLyricsOrClearItsLoadingState()
    {
        var firstAsset = Guid.NewGuid();
        var secondAsset = Guid.NewGuid();
        var pending = new Dictionary<Guid, TaskCompletionSource<string?>>
        {
            [firstAsset] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [secondAsset] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var entered = new Dictionary<Guid, TaskCompletionSource>
        {
            [firstAsset] = new(TaskCreationOptions.RunContinuationsAsynchronously),
            [secondAsset] = new(TaskCreationOptions.RunContinuationsAsynchronously),
        };
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(
            nameof(IEngineApiClient.GetLyricsAsync),
            args =>
            {
                var assetId = (Guid)args![0]!;
                entered[assetId].TrySetResult();
                return pending[assetId].Task;
            }));
        var playback = CreatePlayback(CreateItem("First song", "First artist") with
        {
            AssetId = firstAsset,
            StreamUrl = $"/stream/{firstAsset:D}",
        });
        AddWorkspaceServices(playback, api);
        var cut = Render<ListenContextWorkspace>();
        await entered[firstAsset].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await playback.PlayQueueItemAsync(CreateItem("Second song", "Second artist") with
        {
            AssetId = secondAsset,
            StreamUrl = $"/stream/{secondAsset:D}",
        });
        await entered[secondAsset].Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending[secondAsset].SetResult("New song lyrics");
        cut.WaitForAssertion(() => Assert.Contains("New song lyrics", cut.Markup));

        pending[firstAsset].SetResult("Old song lyrics");
        await Task.Delay(50);
        Assert.Contains("New song lyrics", cut.Markup);
        Assert.DoesNotContain("Old song lyrics", cut.Markup);
        Assert.DoesNotContain("Loading lyrics", cut.Markup);
    }

    [Fact]
    public async Task MusicDockMoreOpensRealQueueAndLyricsWorkspacePanels()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Song with lyrics", "Artist") with { AssetId = Guid.NewGuid() });
        var workspace = new ListenContextWorkspaceState(new MusicQueueOnlyWorkspacePreferences());
        await workspace.ReloadAsync();

        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        var cut = Render<ListenNowPlayingBar>();
        await cut.Find("button.listen-player__music-more").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.Find("button.listen-player__music-more").GetAttribute("aria-expanded") == "true");
            var controls = cut.Find(".listen-player__music-more-tools").TextContent;
            Assert.Contains("Queue", controls);
            Assert.Contains("Lyrics", controls);
            Assert.Contains("History", controls);
            Assert.NotEmpty(cut.FindAll(".listen-player-panel--music-more input[aria-label='Volume']"));
        });

        await cut.Find(".listen-player__music-more-tools button[aria-label='Queue']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.True(workspace.For(playback).Visible);
            Assert.Contains(workspace.For(playback).Panels, panel => panel.Key == "queue");
        });

        await cut.Find("button.listen-player__music-more").ClickAsync();
        await cut.Find(".listen-player__music-more-tools button[aria-label='Lyrics']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            var panelKeys = workspace.For(playback).Panels.Select(panel => panel.Key).ToArray();
            Assert.Contains("queue", panelKeys);
            Assert.Contains("lyrics", panelKeys);
            Assert.DoesNotContain("history", panelKeys);
        });
    }

    [Fact]
    public async Task ExpandedPlayerRerendersAcrossDeviceChangesWithoutTurningDesktopWorkspaceIntoPhoneSheet()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!);
        var audiobook = CreateItem("Test audiobook", "Narrator") with
        {
            MediaType = "Audiobook",
            Synopsis = "Desktop only synopsis for responsive playback.",
            Chapters =
            [
                new PlaybackChapterDto { Index = 0, Title = "Chapter one", StartSeconds = 0, EndSeconds = 3600 },
                new PlaybackChapterDto { Index = 1, Title = "Chapter two", StartSeconds = 3600, EndSeconds = 7200 },
            ],
        };
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [audiobook],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            DurationSeconds = 3600,
            CurrentTimeSeconds = 120,
            IsPlaying = true,
        });
        var preferences = new MemoryWorkspacePreferences();
        var workspace = new ListenContextWorkspaceState(preferences);
        await workspace.ReloadAsync();
        await workspace.TogglePanelAsync(playback, "chapters");
        Assert.True(workspace.For(playback).Visible);

        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        var cut = Render<ListenNowPlayingBar>();
        Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
        Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
        Assert.Contains("More audiobook tools", cut.Find("button.listen-player__more").GetAttribute("aria-label"));
        Assert.DoesNotContain("History", cut.Find(".listen-player__audiobook-actions").TextContent);

        await cut.Find("button.listen-player__more").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            var moreTools = cut.Find(".listen-player__more-tools").TextContent;
            Assert.Contains("Speed", moreTools);
            Assert.Contains("Chapters", moreTools);
            Assert.Contains("History", moreTools);
            Assert.Contains("Bookmark", moreTools);
            Assert.Contains("Sleep", moreTools);
            Assert.NotEmpty(cut.FindAll(".listen-player__more-volume input[aria-label='Volume']"));
        });
        await cut.Find(".listen-player__more-tools button[aria-label='History']").ClickAsync();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".listen-player-panel")));
        Assert.Contains(workspace.For(playback).Panels, panel => panel.Key == "history");
        var desktopPanelKeys = workspace.For(playback).Panels.Select(panel => panel.Key).ToArray();

        playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
        cut.WaitForAssertion(() => Assert.Contains("Desktop only synopsis for responsive playback.", cut.Markup));

        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync("mobile");
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("listen-transport--phone-audiobook", cut.Markup);
            Assert.Contains("playback-control-strip--surface-phone", cut.Markup);
            Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        await cut.Find(".listen-now-playing__tools button[aria-label='Chapters']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal(2, cut.FindAll(".listen-player-panel__chapter-row").Count);
            Assert.Contains("Chapter one", cut.Find(".listen-player-panel__chapter-row[data-playback-active-chapter='true']").TextContent);
            Assert.Equal("1", cut.Find(".listen-player-panel__chapter-row[data-playback-active-chapter='true'] .playback-context-row__leading-text").TextContent);
        });

        await device.SwitchDeviceAsync("web");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
            Assert.Contains("Desktop only synopsis for responsive playback.", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });
        Assert.False(playback.IsPanelOpen);
        Assert.True(workspace.For(playback).Visible);
        Assert.Equal(desktopPanelKeys, workspace.For(playback).Panels.Select(panel => panel.Key));

        await device.SwitchDeviceAsync("mobile");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".listen-player-panel")));
    }

    private void AddWorkspaceServices(PlaybackSessionController playback, IEngineApiClient api)
    {
        Services.AddSingleton(playback);
        Services.AddSingleton(api);
        Services.AddSingleton<ListenContextWorkspaceState>(new ListenContextWorkspaceState(new MemoryWorkspacePreferences()));
    }

    private static PlaybackSessionController CreatePlayback(params ListenQueueItem[] items)
    {
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [.. items],
            CurrentIndex = 0,
            IsPlaying = true,
        });
        return playback;
    }

    private static ListenQueueItem CreateItem(string title, string? artist) => new()
    {
        WorkId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
        Subtitle = artist,
        Duration = "3:24",
    };

    private sealed class MemoryWorkspacePreferences : IContextWorkspacePreferences
    {
        public Task<ContextWorkspaceLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(ContextWorkspacePreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextWorkspaceLayoutDto layout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class MusicQueueOnlyWorkspacePreferences : IContextWorkspacePreferences
    {
        public Task<ContextWorkspaceLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(context == "desktop:music"
                ? new ContextWorkspaceLayoutDto { Visible = false, Width = 410, Panels = [new() { Key = "queue" }] }
                : ContextWorkspacePreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextWorkspaceLayoutDto layout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }
}
