using System.Text.Json;
using MediaEngine.Domain.Services;
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
        JSInterop.SetupModule("./js/playback-lyrics.js");
        JSInterop.SetupModule("./js/playback-audio-presentation.js");
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

        await closed.InvokeAsync(() => closed.FindComponent<PlaybackPopover>().Instance.OpenAsync(true));

        Assert.True(requestedOpen);
        Assert.Equal("true", closed.FindComponent<PlaybackPopover>().Find("button").GetAttribute("aria-expanded"));
        var opened = Render<PlaybackSpeedControl>(parameters => parameters
            .Add(component => component.Value, 1.25d)
            .Add(component => component.Open, true));
        Assert.Equal("true", opened.FindComponent<PlaybackPopover>().Find("button").GetAttribute("aria-expanded"));
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
    public async Task QueueRemovalTargetsTheRenderedOccurrenceAfterAnotherRemovalAndNeverStartsIt()
    {
        var current = CreateItem("Current song", "Artist");
        var repeated = CreateItem("Repeated song", "Artist");
        var playback = CreatePlayback(current, repeated, repeated);
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue"));
        var remove = cut.FindComponents<AppNativeButton>().Where(button => button.Instance.AriaLabel == "Remove Repeated song from queue").Last().Instance.OnClick;
        var target = playback.Queue[2].QueueEntryId;
        await cut.InvokeAsync(() => playback.RemoveUpcomingAt(1));
        await cut.InvokeAsync(() => remove.InvokeAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        Assert.Single(playback.Queue);
        Assert.DoesNotContain(playback.Queue, item => item.QueueEntryId == target);
        Assert.Equal(current.WorkId, playback.CurrentItem?.WorkId);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public async Task StaleQueueClearCannotClearTheSuccessorSession()
    {
        var playback = CreatePlayback(
            CreateItem("Current", "Artist") with { StreamUrl = "/stream/current" },
            CreateItem("Next", "Artist") with { StreamUrl = "/stream/next" },
            CreateItem("Later", "Artist") with { StreamUrl = "/stream/later" });
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue"));
        var clear = cut.FindComponents<AppNativeButton>().Single(button => button.Instance.AriaLabel == "Clear upcoming queue").Instance.OnClick;
        await cut.InvokeAsync(() => playback.PlayIndexAsync(1));
        await cut.InvokeAsync(() => clear.InvokeAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        Assert.Equal(3, playback.Queue.Count);
        Assert.Equal("Next", playback.CurrentItem?.Title);
        Assert.Single(playback.UpcomingQueue);
    }

    [Fact]
    public async Task MobileClassKeepsTabletDockControlsAvailableAndExpandingKeepsTheCurrentRequest()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Song", "Artist"));
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync("mobile");
        var cut = Render<ListenNowPlayingBar>();
        var request = playback.PlaybackRequestVersion;
        Assert.Single(cut.FindAll("button[aria-label='Close player']"));
        Assert.Single(cut.FindAll("button[aria-label='More playback controls']"));
        Assert.Single(cut.FindAll("button[aria-label='Add song to Favorites']"));
        Assert.Single(cut.FindAll("button[aria-label='More song actions']"));
        await cut.Find("button[aria-label='Open Now Playing']").ClickAsync();
        Assert.Equal(request, playback.PlaybackRequestVersion);
        Assert.Equal(PlaybackPresentationSurface.NowPlaying, playback.PresentationSurface);
        Assert.Single(cut.FindAll("button[aria-label='Close player']"));
    }

    [Theory]
    [InlineData(840, false)]
    [InlineData(835, false)]
    [InlineData(721, false)]
    [InlineData(720, true)]
    [InlineData(390, true)]
    public async Task AudioSceneUses720BoundaryEvenWhenNavigationClassifiesTheTabletAsMobile(int width, bool phone)
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Book", "Author") with { MediaType = "Audiobook" });
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync("mobile");
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        var requestVersion = playback.PlaybackRequestVersion;
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(width));
        await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        Assert.True(device.IsMobile);
        Assert.Equal(phone ? 0 : 1, cut.FindAll(".playback-desktop--book").Count);
        Assert.Equal(phone ? 1 : 0, cut.FindAll(".playback-full--phone").Count);
        Assert.Equal(!phone, cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "chapters").Instance.Inline);
        Assert.Equal(requestVersion, playback.PlaybackRequestVersion);
    }

    [Fact]
    public async Task ClosingAnUnresolvedItemCancelsTheLogicalRequestWithoutFabricatingResume()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Resolving song", "Artist"));
        var request = playback.PlaybackRequestVersion;
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var cut = Render<ListenNowPlayingBar>();
        await cut.Find("button[aria-label='Close player']").ClickAsync();
        Assert.False(playback.HasQueue);
        Assert.True(playback.PlaybackRequestVersion > request);
        Assert.Contains(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.pauseAudioForClose");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DockCloseFlushesStoppedNativePositionAndNeverDismissesASuccessor(bool startSuccessor, bool restoredPaused)
    {
        PlayerHeartbeatDto? heartbeat = null;
        var persisted = new TaskCompletionSource<PlayerStateDto?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.PostPlayerHeartbeatAsync), args =>
        {
            heartbeat = (PlayerHeartbeatDto)args![0]!;
            return persisted.Task;
        }));
        using var profileSession = new ActiveProfileSessionService(JSInterop.JSRuntime, api);
        await using var orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profileSession,
            new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance);
        var playback = new PlaybackSessionController(orchestrator, api);
        var current = CreateItem("Current", "Artist") with { AssetId = Guid.NewGuid(), StreamUrl = "/stream/current" };
        var successor = CreateItem("Successor", "Artist") with { AssetId = Guid.NewGuid(), StreamUrl = "/stream/successor" };
        var saved = new ListenPlaybackSnapshot
        {
            Queue = restoredPaused ? [successor, current] : [current, successor],
            CurrentIndex = restoredPaused ? 1 : 0,
            PlaybackRequestVersion = restoredPaused ? 5 : 0,
            CurrentTimeSeconds = restoredPaused ? 91.7 : 0,
            IsPlaying = !restoredPaused,
        };
        var wire = JsonSerializer.Serialize(saved, MediaEngineJson.Web);
        using var json = JsonDocument.Parse(wire);
        Assert.True(json.RootElement.TryGetProperty("profile_id", out _));
        Assert.Equal(saved.CurrentIndex, json.RootElement.GetProperty("current_index").GetInt32());
        Assert.Equal(saved.PlaybackRequestVersion, json.RootElement.GetProperty("playback_request_version").GetInt64());
        Assert.Equal(current.AssetId, json.RootElement.GetProperty("queue")[saved.CurrentIndex].GetProperty("asset_id").GetGuid());
        playback.RestoreState(JsonSerializer.Deserialize<ListenPlaybackSnapshot>(wire, MediaEngineJson.Web)!);
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var stoppedPosition = restoredPaused ? 101.29 : 47.25;
        JSInterop.Setup<ListenNowPlayingBar.AudioClosePosition?>("listenPlayback.pauseAudioForClose", _ => true)
            .SetResult(new(current.AssetId!.Value, playback.PlaybackRequestVersion, true, stoppedPosition, 180));
        JSInterop.Setup<bool>("listenPlayback.finalizeAudioClose", _ => true).SetResult(true);
        var cut = Render<ListenNowPlayingBar>();
        var close = cut.Find("button[aria-label='Close player']").ClickAsync();
        cut.WaitForAssertion(() => Assert.NotNull(heartbeat));
        Assert.False(heartbeat!.IsPlaying);
        Assert.False(heartbeat.HasPlaybackEnded);
        Assert.Equal(stoppedPosition, heartbeat.PositionSeconds);
        Assert.Equal(current.AssetId, heartbeat.AssetId);
        Assert.False(playback.IsPlaying);
        Assert.True(playback.HasQueue);
        if (startSuccessor) await cut.InvokeAsync(() => playback.PlayIndexAsync(1));
        persisted.SetResult(null);
        await close;
        if (startSuccessor)
        {
            Assert.Equal(successor.WorkId, playback.CurrentItem?.WorkId);
            Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.finalizeAudioClose");
        }
        else Assert.False(playback.HasQueue);
    }

    [Fact]
    public async Task CloseWaitingForNativePositionCannotStopANewerSession()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(
            CreateItem("First", "Artist") with { AssetId = Guid.NewGuid(), StreamUrl = "/stream/first" },
            CreateItem("Next", "Artist") with { AssetId = Guid.NewGuid(), StreamUrl = "/stream/next" });
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var pause = JSInterop.Setup<ListenNowPlayingBar.AudioClosePosition?>("listenPlayback.pauseAudioForClose", _ => true);
        var cut = Render<ListenNowPlayingBar>();
        var oldAsset = playback.CurrentItem!.AssetId!.Value;
        var oldRequest = playback.PlaybackRequestVersion;
        var close = cut.Find("button[aria-label='Close player']").ClickAsync();
        await cut.InvokeAsync(() => playback.PlayIndexAsync(1));
        pause.SetResult(new(oldAsset, oldRequest, true, 45, 180));
        await close;
        Assert.Equal("Next", playback.CurrentItem?.Title);
        Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.finalizeAudioClose");
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
            nameof(IEngineApiClient.GetTextTrackContentAsync),
            args =>
            {
                var assetId = (Guid)args![0]!;
                entered[assetId].TrySetResult();
                return pending[assetId].Task;
            }));
        ((EngineApiClientStub)(object)api).SetHandler(nameof(IEngineApiClient.GetTextTracksAsync),
            args => Task.FromResult<IReadOnlyList<TextTrackDto>>(new List<TextTrackDto> { new() { Id = (Guid)args![0]!, Kind = "Lyrics", IsPreferred = true } }));
        var playback = CreatePlayback(CreateItem("First song", "First artist") with
        {
            AssetId = firstAsset,
            StreamUrl = $"/stream/{firstAsset:D}",
        });
        AddWorkspaceServices(playback, api);
        Render<MudBlazor.MudPopoverProvider>();
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
    public async Task UtilityMoreOpensAnchoredQueueAndLyricsWithoutUsingTheShellSidebar()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Song with lyrics", "Artist") with { AssetId = Guid.NewGuid() });
        var workspace = new ListenContextSidebarState(new MusicQueueOnlySidebarPreferences());
        await workspace.ReloadAsync();
        AddBarServices(playback, api, workspace);
        Render<MudBlazor.MudPopoverProvider>();
        var cut = Render<ListenNowPlayingBar>();
        var more = cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.Title == "More playback controls");
        await cut.InvokeAsync(() => more.Instance.OpenAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".listen-player__utility-menu")));
        await cut.Find(".listen-player__utility-menu button[aria-label='Queue']").ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.True(workspace.For(playback).Open);
            Assert.Equal("queue", workspace.For(playback).ActivePanelKey);
            Assert.NotEmpty(cut.FindAll(".listen-context-sidebar__tabs"));
            Assert.Empty(cut.FindAll(".listen-player-panel-backdrop"));
        });
        await cut.InvokeAsync(() => more.Instance.OpenAsync());
        await cut.Find(".listen-player__utility-menu button[aria-label='Lyrics']").ClickAsync();
        cut.WaitForAssertion(() => Assert.Equal("lyrics", workspace.For(playback).ActivePanelKey));
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
        var transientTools = new PlaybackTransientToolCoordinator(playback);
        Services.AddSingleton(transientTools);
        await workspace.ReloadAsync();
        await workspace.TogglePanelAsync(playback, "chapters");
        Assert.True(workspace.For(playback).Open);

        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        AddPanelServices(playback, api);
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
        var dockTools = cut.Find(".listen-player__desktop-tools");
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Chapters and history']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Bookmark']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("[aria-label='Playback speed 1.0x']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll(".playback-sleep-timer [aria-label^='Sleep timer:']"));
        Assert.NotEmpty(cut.FindAll("input[aria-label='Volume']"));
        Assert.Single(cut.FindAll("button[aria-label='Close player']"));
        var chapterTool = cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "chapters");
        await cut.InvokeAsync(() => chapterTool.Instance.OpenAsync());
        await cut.Find(".listen-context-sidebar__tabs button[aria-selected='false']").ClickAsync();
        Assert.Equal("history", workspace.For(playback).ActivePanelKey);

        playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".playback-desktop--book")));

        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync("mobile");
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(390));
        cut.WaitForAssertion(() =>
        {
            Assert.Contains("listen-transport--phone-audiobook", cut.Markup);
            Assert.Contains("playback-control-strip--surface-phone", cut.Markup);
            Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        // Slider speed and select sleep share the same exclusive temporary-tool owner.
        var phoneFull = cut.FindComponent<PlaybackFullPlayer>();
        var speed = phoneFull.FindComponent<PlaybackSpeedControl>().FindComponent<PlaybackPopover>();
        await cut.InvokeAsync(() => speed.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.Equal("true", speed.Find("button").GetAttribute("aria-expanded")));
        Assert.NotEqual("audio-chapters", transientTools.OpenToolId);
        var selector = phoneFull.FindComponent<PlaybackSleepTimerControl>().FindComponent<AppSelect>();
        await cut.InvokeAsync(() => selector.FindComponent<MudBlazor.MudSelect<string>>().Instance.OpenChanged.InvokeAsync(true));
        cut.WaitForAssertion(() => {
            Assert.True(selector.Instance.Open);
            Assert.StartsWith("app-select-", transientTools.OpenToolId);
            Assert.Equal("false", speed.Find("button").GetAttribute("aria-expanded"));
        });
        await cut.InvokeAsync(() => selector.Instance.ClosePlaybackMenuAsync());
        cut.WaitForAssertion(() => Assert.False(selector.Instance.Open));
        Assert.True(playback.IsPlaying);
        Assert.Equal(120, playback.CurrentTimeSeconds);
        await cut.Find(".playback-full__tools button[aria-label='Chapters and history']").ClickAsync();
        Assert.Equal("history", workspace.For(playback).ActivePanelKey);
        await cut.FindAll(".listen-context-sidebar__tabs button").Single(button => button.TextContent.Contains("Chapters")).ClickAsync();
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);
            Assert.True(workspace.For(playback).Open);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        await device.SwitchDeviceAsync("web");
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(1280));
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
            Assert.NotEmpty(cut.FindAll(".playback-desktop--book"));
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });
        Assert.False(playback.IsPanelOpen);
        Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);

        await device.SwitchDeviceAsync("mobile");
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(390));
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
        AddPanelServices(playback, api);
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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".playback-desktop")));
        await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.Docked));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".playback-desktop")));
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
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".playback-desktop")));
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

    private void AddBarServices(PlaybackSessionController playback, IEngineApiClient api, ListenContextSidebarState workspace)
    {
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        AddPanelServices(playback, api);
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        AddBookmarkBarServices(api);
        Services.AddSingleton<ActiveProfileSessionService>(provider => new ActiveProfileSessionService(
            provider.GetRequiredService<IJSRuntime>(), api));
        Services.AddSingleton<UIOrchestratorService>(provider => new UIOrchestratorService(
            api, new UniverseStateContainer(), provider.GetRequiredService<ActiveProfileSessionService>(),
            new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance));
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
    }

    private void AddWorkspaceServices(PlaybackSessionController playback, IEngineApiClient api)
    {
        Services.AddSingleton(playback);
        Services.AddSingleton(api);
        AddPanelServices(playback, api);
        Services.AddSingleton<ListenContextSidebarState>(new ListenContextSidebarState(new MemorySidebarPreferences()));
    }

    private void AddBookmarkBarServices(IEngineApiClient api, IUserPlaybackPreferencesAccessor? preferences = null)
    {
        Services.AddSingleton<PlaybackLyricsPresenter>();
        Services.AddSingleton<IPlaybackCommandSink>(provider => new DirectPlaybackCommandSink(
            new ListenPlaybackCommandOwner(provider, provider.GetRequiredService<PlaybackSessionController>())));
        var actions = new AudiobookBookmarkActionService(api);
        Services.AddSingleton(actions);
        Services.AddSingleton<IAudiobookBookmarkActions>(actions);
        Services.AddSingleton<IAudiobookBookmarkLeaseInvalidator>(actions);
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences ?? new TestPlaybackPreferencesAccessor());
    }

    private void AddPanelServices(PlaybackSessionController playback, IEngineApiClient api)
    {
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Services.AddSingleton(new PlaybackLyricsSelectionOwner(playback, api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new TestPlaybackPreferencesAccessor(playback.ActiveProfileId));
        Services.AddSingleton<IPlaybackCommandSink>(provider => new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(provider, playback)));
    }

    private static PlaybackSessionController CreatePlayback(params ListenQueueItem[] items)
    {
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
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
        AssetId = Guid.NewGuid(),
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
