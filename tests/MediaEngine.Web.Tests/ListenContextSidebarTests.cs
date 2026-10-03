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
using Microsoft.JSInterop;
using MudBlazor.Services;

namespace MediaEngine.Web.Tests;

public sealed class ListenContextSidebarTests : AsyncBunitContext
{
    public ListenContextSidebarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/context-sidebar.js");
        Services.AddLogging();
        Services.AddLocalization();
        Services.AddMudServices();
    }

    [Fact]
    public void PlaybackSpeedControlShowsExactFractionalSelectionAndBlocksMalformedRate()
    {
        Render<MudBlazor.MudPopoverProvider>();
        var initial = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.2345d)
            .Add(component => component.Surface, "dock"));

        Assert.Contains("1.2345x", initial.Markup);
        var choices = PlaybackRateOptions.BuildChoices(1.2345d);
        Assert.Contains(choices, option => option.Label == "0.75x");
        Assert.Contains(choices, option => option.Label == "1.25x");
        Assert.Contains(choices, option => option.Label == "1.75x");
        Assert.Contains(choices, option => option.Label == "1.2345x" && option.Rate == 1.2345d);

        var malformed = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, double.NaN));
        Assert.Contains("Playback speed unavailable", malformed.Markup);
        Assert.DoesNotContain("1.0x", malformed.Markup);
    }

    [Fact]
    public async Task PlaybackSpeedControlReportsOpenRequestsAndFollowsControlledState()
    {
        Render<MudBlazor.MudPopoverProvider>();
        bool? requestedOpen = null;
        var closed = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.25d)
            .Add(component => component.Open, false)
            .Add(component => component.MenuOpenChanged, EventCallback.Factory.Create<bool>(this, value => requestedOpen = value)));

        await closed.InvokeAsync(() => closed.FindComponent<AppSelect>().Instance.OpenChanged.InvokeAsync(true));

        Assert.True(requestedOpen);
        Assert.Equal(false, closed.FindComponent<AppSelect>().Instance.Open);
        var opened = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.25d)
            .Add(component => component.Open, true));
        Assert.Equal(true, opened.FindComponent<AppSelect>().Instance.Open);
    }

    [Fact]
    public void QueueShowsTheCurrentItemBeforeUpcomingItemsInTheSharedRowLayout()
    {
        var current = CreateItem("Current song", "Artist");
        var upcoming = CreateItem("Up next", "Second artist");
        var playback = CreatePlayback(current, upcoming);
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());

        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue"));

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
        await Services.GetRequiredService<ListenContextSidebarState>().TogglePanelAsync(playback, "history");

        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "history"));

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
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "lyrics"));
        await entered[firstAsset].Task.WaitAsync(TimeSpan.FromSeconds(5));

        await playback.PlayQueueItemAsync(CreateItem("Second song", "Second artist") with
        {
            AssetId = secondAsset,
            StreamUrl = $"/stream/{secondAsset:D}",
        });
        await entered[secondAsset].Task.WaitAsync(TimeSpan.FromSeconds(5));
        pending[firstAsset].SetResult("Old song lyrics");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("Old song lyrics", cut.Markup);
            Assert.Contains("Loading lyrics", cut.Markup);
        });

        pending[secondAsset].SetResult("New song lyrics");
        cut.WaitForAssertion(() => Assert.Contains("New song lyrics", cut.Markup));

        Assert.Contains("New song lyrics", cut.Markup);
        Assert.DoesNotContain("Old song lyrics", cut.Markup);
        Assert.DoesNotContain("Loading lyrics", cut.Markup);
    }

    [Fact]
    public async Task MusicDockMoreOpensRealQueueAndLyricsSidebarPanels()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Song with lyrics", "Artist") with { AssetId = Guid.NewGuid() });
        var workspace = new ListenContextSidebarState(new MusicQueueOnlySidebarPreferences());
        await workspace.ReloadAsync();

        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        AddBookmarkBarServices(api);
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
            Assert.True(workspace.For(playback).Open);
            Assert.Equal("queue", workspace.For(playback).ActivePanelKey);
        });

        await cut.Find("button.listen-player__music-more").ClickAsync();
        await cut.Find(".listen-player__music-more-tools button[aria-label='Lyrics']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("lyrics", workspace.For(playback).ActivePanelKey);
        });
    }

    [Fact]
    public async Task ExpandedPlayerRerendersAcrossDeviceChangesAndKeepsPhonePanelsInCanonicalSidebarState()
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
        var preferences = new MemorySidebarPreferences();
        var workspace = new ListenContextSidebarState(preferences);
        await workspace.ReloadAsync();
        await workspace.TogglePanelAsync(playback, "chapters");
        Assert.True(workspace.For(playback).Open);

        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        AddBookmarkBarServices(api);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<Microsoft.JSInterop.IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
        Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
        Assert.Empty(cut.FindAll(".listen-player-panel"));
        var dockTools = cut.Find(".listen-player__audiobook-actions");
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Chapters']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Bookmark']"));
        Assert.Contains("1.0x", dockTools.TextContent);
        Assert.NotEmpty(dockTools.QuerySelectorAll(".playback-sleep-timer [aria-label^='Sleep timer:']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='History']"));
        Assert.NotEmpty(cut.FindAll(".listen-player__audiobook-volume input[aria-label='Volume']"));
        Assert.NotEmpty(cut.FindAll(".listen-player__audiobook-mute button[aria-label='Mute or unmute']"));

        await cut.Find(".listen-player__audiobook-actions button[aria-label='History']").ClickAsync();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".listen-player-panel")));
        Assert.Equal("history", workspace.For(playback).ActivePanelKey);
        Assert.True(workspace.For(playback).Open);

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
            Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);
            Assert.True(workspace.For(playback).Open);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        await device.SwitchDeviceAsync("web");
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
            Assert.Contains("Desktop only synopsis for responsive playback.", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });
        Assert.False(playback.IsPanelOpen);
        Assert.True(workspace.For(playback).Open);
        Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);

        await device.SwitchDeviceAsync("mobile");
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".listen-player-panel")));
    }

    [Fact]
    public async Task PendingSleepTimerChoiceSurvivesBarRendersWithoutPassiveOffBinds()
    {
        var profileId = Guid.NewGuid();
        var api = EngineApiClientStub.CreateDefault();
        var preferences = new TestPlaybackPreferencesAccessor(profileId);
        var jsRuntime = new ControlledSleepTimerJsRuntime();
        var item = CreateItem("Test audiobook", "Narrator") with
        {
            MediaType = "Audiobook",
            AssetId = Guid.NewGuid(),
            StreamUrl = "stream://test-audiobook",
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            SleepTimerOptionsMinutes = [15],
        });
        var workspace = new ListenContextSidebarState(new MemorySidebarPreferences());

        Services.AddSingleton<IJSRuntime>(jsRuntime);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        AddBookmarkBarServices(api, preferences);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));

        var firstStableOffCalls = jsRuntime.OffCalls;
        await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".listen-now-playing")));
        await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.Docked));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".listen-now-playing")));
        Assert.Equal(firstStableOffCalls, jsRuntime.OffCalls);

        var stableOffCalls = jsRuntime.OffCalls;
        var sleepControl = cut.FindComponent<PlaybackSleepTimerControl>().Instance;

        var selectionTask = cut.InvokeAsync(() => sleepControl.SelectionChanged.InvokeAsync(
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 }));
        await jsRuntime.CandidateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.Equal(1, jsRuntime.CandidateCalls);

        await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".listen-now-playing")));
        Assert.Equal(stableOffCalls, jsRuntime.OffCalls);
        Assert.Equal(1, jsRuntime.CandidateCalls);

        jsRuntime.CandidateRegistration.TrySetResult(true);
        await selectionTask;

        Assert.False(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Timer, playback.SleepTimerState.Mode);
        Assert.Equal(15, playback.SleepTimerState.ChosenMinutes);
        Assert.Equal(stableOffCalls, jsRuntime.OffCalls);
    }

    [Fact]
    public async Task NewerOffInvalidationSupersedesPendingNativeSleepTimerRegistration()
    {
        var profileId = Guid.NewGuid();
        var api = EngineApiClientStub.CreateDefault();
        var preferences = new TestPlaybackPreferencesAccessor(profileId);
        var jsRuntime = new ControlledSleepTimerJsRuntime();
        var item = CreateItem("Test audiobook", "Narrator") with
        {
            MediaType = "Audiobook",
            AssetId = Guid.NewGuid(),
            StreamUrl = "stream://test-audiobook",
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: preferences);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            SleepTimerOptionsMinutes = [15],
        });

        Services.AddSingleton<IJSRuntime>(jsRuntime);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new ListenContextSidebarState(new MemorySidebarPreferences()));
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        AddBookmarkBarServices(api, preferences);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));
        var initialOffCalls = jsRuntime.OffCalls;
        var sleepControl = cut.FindComponent<PlaybackSleepTimerControl>().Instance;
        var selectionTask = cut.InvokeAsync(() => sleepControl.SelectionChanged.InvokeAsync(
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 }));
        await jsRuntime.CandidateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await cut.InvokeAsync(() => playback.InvalidateAudiobookSleepTimerAsync());
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.True(jsRuntime.OffCalls > initialOffCalls);

        jsRuntime.CandidateRegistration.TrySetResult(true);
        await selectionTask;
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
    }

    [Fact]
    public async Task AudioSyncDoesNotSeekInitialDefaultZeroButHonorsExplicitZeroStartVersion()
    {
        var api = EngineApiClientStub.CreateDefault();
        var jsRuntime = new ControlledSleepTimerJsRuntime();
        var assetId = Guid.NewGuid();
        var item = CreateItem("Test audiobook", "Narrator") with
        {
            MediaType = "Audiobook",
            AssetId = assetId,
            StreamUrl = "stream://test-audiobook",
            Chapters = [new PlaybackChapterDto
            {
                Index = 0,
                AssetId = assetId,
                Title = "Chapter one",
                StartSeconds = 0,
                EndSeconds = 120,
            }],
        };
        var playback = new PlaybackSessionController(null!, null!);
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [item],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            CurrentTimeSeconds = 0,
            PlaybackStartVersion = 5,
            IsPlaying = false,
        });

        Services.AddSingleton<IJSRuntime>(jsRuntime);
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new ListenContextSidebarState(new MemorySidebarPreferences()));
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        AddBookmarkBarServices(api);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api,
            new UniverseStateContainer(),
            provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(),
            NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());

        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));
        Assert.Empty(jsRuntime.SeekPositions);

        await cut.InvokeAsync(() => playback.PlayAudiobookChapterAsync(item, item.Chapters[0]));
        cut.WaitForAssertion(() => Assert.Contains(0d, jsRuntime.SeekPositions));
    }

    private void AddWorkspaceServices(PlaybackSessionController playback, IEngineApiClient api)
    {
        Services.AddSingleton(playback);
        Services.AddSingleton(api);
        Services.AddSingleton<ListenContextSidebarState>(new ListenContextSidebarState(new MemorySidebarPreferences()));
    }

    private void AddBookmarkBarServices(IEngineApiClient api, IUserPlaybackPreferencesAccessor? preferences = null)
    {
        var actions = new AudiobookBookmarkActionService(api);
        Services.AddSingleton(actions);
        Services.AddSingleton<IAudiobookBookmarkActions>(actions);
        Services.AddSingleton<IAudiobookBookmarkLeaseInvalidator>(actions);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences ?? new TestPlaybackPreferencesAccessor());
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

    private sealed class MemorySidebarPreferences : IContextSidebarPreferences
    {
        public Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(ContextSidebarPreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class MusicQueueOnlySidebarPreferences : IContextSidebarPreferences
    {
        public Task<ContextSidebarLayoutDto> GetAsync(string context, CancellationToken ct = default) =>
            Task.FromResult(context == "desktop:music"
                ? new ContextSidebarLayoutDto { Open = false, Width = 340, ActivePanelKey = "queue" }
                : ContextSidebarPreferences.Default(context));

        public Task<bool> SaveAsync(string context, ContextSidebarLayoutDto layout, CancellationToken ct = default) =>
            Task.FromResult(true);
    }

    private sealed class TestPlaybackPreferencesAccessor(Guid? profileId = null) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId => profileId;

        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) =>
            Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(profileId ?? Guid.NewGuid()));

        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }

    private sealed class ControlledSleepTimerJsRuntime : IJSRuntime
    {
        public TaskCompletionSource<bool> CandidateRegistration { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CandidateStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OffCalls { get; private set; }
        public int CandidateCalls { get; private set; }
        public List<double> SeekPositions { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import")
                return ValueTask.FromResult((TValue)(object)new NoopJsObjectReference());

            if (identifier == "listenPlayback.setAudiobookSleepTimer"
                && args is { Length: >= 2 }
                && args[1] is AudiobookSleepTimerStateDto state)
            {
                if (state.Mode == AudiobookSleepTimerModes.Off)
                {
                    OffCalls++;
                    return ValueTask.FromResult((TValue)(object)true);
                }

                CandidateCalls++;
                CandidateStarted.TrySetResult();
                return AwaitCandidateRegistrationAsync<TValue>(cancellationToken);
            }

            if (identifier == "listenPlayback.seekAudio" && args is { Length: >= 2 } && args[1] is double position)
            {
                SeekPositions.Add(position);
            }

            return ValueTask.FromResult(default(TValue)!);
        }

        private async ValueTask<TValue> AwaitCandidateRegistrationAsync<TValue>(CancellationToken cancellationToken)
        {
            var registered = await CandidateRegistration.Task.WaitAsync(cancellationToken);
            return (TValue)(object)registered;
        }
    }

    private sealed class NoopJsObjectReference : IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
