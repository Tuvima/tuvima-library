using System.Security.Claims;
using System.Text.Json;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Listen;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Services.Theming;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;

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
        Services.AddNativeUiServices();
    }

    [Fact]
    public void QueueShowsTheCurrentItemBeforeUpcomingItemsInTheSharedRowLayout()
    {
        var current = CreateItem("Current song", "Artist");
        var upcoming = CreateItem("Up next", "Second artist");
        var playback = CreatePlayback(current, upcoming);
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());

        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue"));

        var rows = cut.FindAll("[data-playback-list-row]");
        Assert.Equal(2, rows.Count);
        Assert.Equal("Current song", rows[0].QuerySelector(".plr__title")?.TextContent);
        Assert.Contains("is-current", rows[0].ClassName);
        Assert.Equal("true", rows[0].GetAttribute("aria-current"));
        Assert.Equal("Up next", rows[1].QuerySelector(".plr__title")?.TextContent);
    }

    [Fact]
    public async Task QueueRemovalTargetsTheRenderedOccurrenceAfterAnotherRemovalAndNeverStartsIt()
    {
        var current = CreateItem("Current song", "Artist");
        var repeated = CreateItem("Repeated song", "Artist");
        var playback = CreatePlayback(current, repeated, repeated);
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());
        var cut = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue"));
        await cut.InvokeAsync(() => cut.FindComponents<PlaybackPanel>().Last(p => p.Instance.Title == "Queue actions for Repeated song").Instance.OpenAsync(true));
        var remove = cut.FindComponents<AppNativeButton>().Where(button => button.Instance.AdditionalAttributes?.GetValueOrDefault("data-playback-row-action") as string == "remove").Last().Instance.OnClick;
        var target = playback.Queue[2].QueueEntryId;
        await cut.InvokeAsync(() => playback.RemoveUpcomingAt(1));
        await cut.InvokeAsync(() => remove.InvokeAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        Assert.Single(playback.Queue);
        Assert.DoesNotContain(playback.Queue, item => item.QueueEntryId == target);
        Assert.Equal(current.WorkId, playback.CurrentItem?.WorkId);
        Assert.True(playback.IsPlaying);
    }

    [Fact]
    public void TheBarsHistoryControlOpensTheQueueOnItsHistoryTab()
    {
        var playback = CreatePlayback(CreateItem("Current song", "Artist"), CreateItem("Next song", "Artist"));
        AddWorkspaceServices(playback, EngineApiClientStub.CreateDefault());

        var history = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "history").Add(component => component.ShowTabs, true));
        var queue = Render<ListenContextSidebar>(parameters => parameters.Add(component => component.ActivePanelKey, "queue").Add(component => component.ShowTabs, true));

        Assert.Equal("true", history.FindAll("[role='tab']")[1].GetAttribute("aria-selected"));
        Assert.Equal("true", queue.FindAll("[role='tab']")[0].GetAttribute("aria-selected"));
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
    public async Task BarRendersAndSettlesWithNoActiveSession()
    {
        // A full reload of a deep /watch or /read URL mounts the bar before any playback state exists.
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        await Services.GetRequiredService<DeviceContextService>().SwitchDeviceAsync("mobile");

        var cut = Render<ListenNowPlayingBar>();

        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, i => i.Identifier == "listenPlayback.hasState"));
        Assert.False(playback.HasQueue);
        Assert.Empty(cut.FindAll(".listen-player"));
        Assert.Empty(cut.FindAll(".listen-player__error"));
    }

    [Fact]
    public async Task BarRemovedWhileItsScriptsAreLoadingRegistersNothingAndReleasesWhatItHad()
    {
        // A reload that swaps the shell can remove the bar while its first render still waits on the browser.
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var gate = new GatedJsRuntime(JSInterop.JSRuntime, (identifier, args) =>
            identifier == "import" && args?.FirstOrDefault() as string == "./js/playback-audio-presentation.js");
        Services.AddSingleton<IJSRuntime>(gate);

        var host = Render<BarToggle>(parameters => parameters.Add(p => p.Show, true));
        await gate.Reached.WaitAsync(TimeSpan.FromSeconds(5));

        host.Render(parameters => parameters.Add(p => p.Show, false));
        gate.Release();
        await Task.Delay(150);

        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "listenPlayback.registerCommandHandler");
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier == "listenPlayback.registerAudioStateObserver");
        Assert.Empty(host.FindAll(".listen-player"));
    }

    [Fact]
    public async Task BarRendersAndDisposesImmediatelyWithoutThrowing()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));

        var host = Render<BarToggle>(parameters => parameters.Add(p => p.Show, true));
        host.Render(parameters => parameters.Add(p => p.Show, false));
        host.Render(parameters => parameters.Add(p => p.Show, true));
        await host.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        host.Render(parameters => parameters.Add(p => p.Show, false));
        await Task.Delay(100);

        Assert.Empty(host.FindAll(".listen-player"));
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
        Assert.Single(cut.FindAll("button[aria-label='Close']"));
        Assert.Single(cut.FindAll("button[aria-label='Add song to Favorites']"));
        Assert.Single(cut.FindAll("button[aria-label='More song actions']"));
        await cut.Find("button[aria-label='Open Now Playing']").ClickAsync();
        // A desktop-width bar opens the Now playing page (unified player v2 D24); the phone's expanded player is covered by the 720 px boundary test.
        Assert.Equal(request, playback.PlaybackRequestVersion);
        Assert.Equal(NowPlayingRoute.Path, new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath);
        Assert.Equal(PlaybackPresentationSurface.Docked, playback.PresentationSurface);
        Assert.Single(cut.FindAll("button[aria-label='Close']"));
    }

    [Theory]
    [InlineData(840, false)]
    [InlineData(835, false)]
    [InlineData(721, false)]
    [InlineData(720, true)]
    [InlineData(390, true)]
    public async Task AudioPlayerUses720BoundaryEvenWhenNavigationClassifiesTheTabletAsMobile(int width, bool phone)
    {
        var api = EngineApiClientStub.CreateDefault();
        var bookAsset = Guid.NewGuid();
        var playback = CreatePlayback(CreateItem("Book", "Author") with { MediaType = "Audiobook", Chapters = [new() { Index = 0, AssetId = bookAsset, Title = "One", StartSeconds = 0, EndSeconds = 30 }, new() { Index = 1, AssetId = bookAsset, Title = "Two", StartSeconds = 30, EndSeconds = 90 }] });
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync("mobile");
        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        var requestVersion = playback.PlaybackRequestVersion;
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(width));
        // A phone opens the expanded player over the page; a desktop opens the Now playing page (unified player v2 D24).
        if (phone)
        {
            await cut.InvokeAsync(() => playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying));
        }
        else
        {
            await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo(NowPlayingRoute.Path));
        }

        Assert.True(device.IsMobile);
        Assert.Equal(phone ? 1 : 0, cut.FindAll(".playback-full--phone").Count);
        // The phone's collapsed bar has no tools and its expanded player holds Chapters in its one tool row, as a sheet over the view; the desktop bar hosts
        // Chapters, and it follows the open page inline.
        var chapterTools = cut.FindComponents<ListenDockTool>().Where(tool => tool.Instance.PanelKey == "chapters").ToList();
        Assert.Equal(!phone, Assert.Single(chapterTools).Instance.Inline);
        Assert.Equal(phone ? 1 : 0, cut.FindAll("[data-playback-tool-row] [data-playback-tool='Chapters']").Count);
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
        await cut.Find("button[aria-label='Close']").ClickAsync();
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
        var close = cut.Find("button[aria-label='Close']").ClickAsync();
        cut.WaitForAssertion(() => Assert.NotNull(heartbeat));
        Assert.False(heartbeat!.IsPlaying);
        Assert.False(heartbeat.HasPlaybackEnded);
        Assert.Equal(stoppedPosition, heartbeat.PositionSeconds);
        Assert.Equal(current.AssetId, heartbeat.AssetId);
        Assert.False(playback.IsPlaying);
        Assert.True(playback.HasQueue);
        if (startSuccessor)
        {
            await cut.InvokeAsync(() => playback.PlayIndexAsync(1));
        }
        persisted.SetResult(null);
        await close;
        if (startSuccessor)
        {
            Assert.Equal(successor.WorkId, playback.CurrentItem?.WorkId);
            Assert.DoesNotContain(JSInterop.Invocations, invocation => invocation.Identifier == "listenPlayback.finalizeAudioClose");
        }
        else
        {
            Assert.False(playback.HasQueue);
        }
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
        var close = cut.Find("button[aria-label='Close']").ClickAsync();
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
        Render<AppPopoverHost>();
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
            Assert.Contains("Looking for lyrics", cut.Markup);
        });

        pending[secondAsset].SetResult("New song lyrics");
        cut.WaitForAssertion(() => Assert.Contains("New song lyrics", cut.Markup));

        Assert.Contains("New song lyrics", cut.Markup);
        Assert.DoesNotContain("Old song lyrics", cut.Markup);
        Assert.DoesNotContain("Looking for lyrics", cut.Markup);
    }

    [Fact]
    public async Task QueueAndLyricsToolsOpenAnchoredPanelsWithoutUsingTheShellSidebar()
    {
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Song with lyrics", "Artist") with { AssetId = Guid.NewGuid() });
        var workspace = new ListenContextSidebarState(new MusicQueueOnlySidebarPreferences());
        await workspace.ReloadAsync();
        AddBarServices(playback, api, workspace);
        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        var queue = cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "queue");
        await cut.InvokeAsync(() => queue.Instance.OpenAsync());
        cut.WaitForAssertion(() =>
        {
            Assert.True(workspace.For(playback).Open);
            Assert.Equal("queue", workspace.For(playback).ActivePanelKey);
            Assert.NotEmpty(cut.FindAll(".plt"));
            Assert.Empty(cut.FindAll(".listen-player-panel-backdrop"));
        });
        var lyrics = cut.FindComponent<PlaybackLyricsPanel>();
        await cut.InvokeAsync(() => lyrics.Instance.OpenAsync());
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

        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
        Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
        Assert.Empty(cut.FindAll(".listen-player-panel"));
        var dockTools = cut.Find("[data-playback-zone='tools']");
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Chapters']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Bookmarks']"));
        // Unified player v2 D4: the audiobook bar has a speed chip and a sleep timer button (both open today's settings menu until the tool panels are rebuilt).
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label^='Playback speed']"));
        Assert.NotEmpty(dockTools.QuerySelectorAll("button[aria-label='Sleep timer']"));
        Assert.NotEmpty(cut.FindAll("input[aria-label='Volume']"));
        Assert.Single(cut.FindAll("button[aria-label='Close']"));
        var chapterTool = cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "chapters");
        await cut.InvokeAsync(() => chapterTool.Instance.OpenAsync());
        // Unified player v2 D20: the Chapters panel is the one list (identity, "Chapter N of M", rows, Timeline shows); it has no History tab.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-chapters-identity]")));
        Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);

        // On a desktop Now playing is the page: the bar's Chapters tool then selects the page's tab and the Now playing tool shows as selected.
        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo(NowPlayingRoute.Path));
        cut.WaitForAssertion(() =>
        {
            Assert.True(cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "chapters").Instance.Inline);
            Assert.Contains("is-selected", cut.Find("[data-playback-zone='tools'] button[aria-label='Now playing']").ClassName);
        });
        playback.SetPresentationSurface(PlaybackPresentationSurface.NowPlaying);

        var device = Services.GetRequiredService<DeviceContextService>();
        await device.SwitchDeviceAsync("mobile");
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(390));
        cut.WaitForAssertion(() =>
        {
            Assert.NotEmpty(cut.FindAll(".playback-full--phone [data-playback-tool-row]"));
            Assert.Empty(cut.FindAll(".playback-full--phone input[aria-label='Volume']"));
            Assert.DoesNotContain("Desktop only synopsis for responsive playback.", cut.Markup);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        // The phone full player has the moon that opens the Sleep timer panel, and opening it uses the shared temporary-tool owner.
        var phoneFull = cut.FindComponent<PlaybackFullPlayer>();
        var gear = phoneFull.FindComponent<SleepTimerPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => gear.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.Equal("true", gear.Find("button").GetAttribute("aria-expanded")));
        Assert.NotEqual("audio-chapters", transientTools.OpenToolId);
        cut.WaitForAssertion(() =>
        {
            // Unified player v2 D15 and D16: speed has its own panel, opened from the speed value; the moon opens the Sleep timer panel itself.
            Assert.Empty(phoneFull.FindAll("[data-settings-row='speed']"));
            Assert.NotEmpty(phoneFull.FindAll("[data-sleep-timer-view]"));
            Assert.Empty(phoneFull.FindAll(".player-settings-menu__switch"));
        });
        await cut.InvokeAsync(() => gear.Instance.CloseAsync());
        cut.WaitForAssertion(() => Assert.Equal("false", gear.Find("button").GetAttribute("aria-expanded")));
        var speed = phoneFull.FindComponent<PlaybackSpeedPanel>().FindComponent<PlaybackPanel>();
        Assert.Equal("Playback speed 1×", speed.Find("button").GetAttribute("aria-label"));
        await cut.InvokeAsync(() => speed.Instance.OpenAsync(true));
        cut.WaitForAssertion(() => Assert.NotEmpty(phoneFull.FindAll("[data-playback-speed-view]")));
        await cut.InvokeAsync(() => speed.Instance.CloseAsync());
        Assert.True(playback.IsPlaying);
        Assert.Equal(120, playback.CurrentTimeSeconds);
        var chapters = phoneFull.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "chapters");
        await cut.InvokeAsync(() => chapters.Instance.OpenAsync());
        cut.WaitForAssertion(() =>
        {
            Assert.Equal("chapters", workspace.For(playback).ActivePanelKey);
            Assert.Empty(cut.FindAll(".listen-player-panel"));
        });

        await device.SwitchDeviceAsync("web");
        await cut.InvokeAsync(() => cut.Instance.SetAudioPresentationViewport(1280));
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("listen-transport--phone-audiobook", cut.Markup);
            Assert.Empty(cut.FindAll(".playback-full--phone"));
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
            // A one-chapter book: the bar offers End of chapter exactly as the phone player does.
            Chapters = [new() { Index = 0, AssetId = Guid.NewGuid(), Title = "Only chapter", StartSeconds = 0, EndSeconds = 600 }],
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

        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));

        var firstStableOffCalls = jsRuntime.OffCalls;
        await OpenNowPlayingPageAsync(cut);
        await LeaveNowPlayingPageAsync(cut);
        Assert.Equal(firstStableOffCalls, jsRuntime.OffCalls);

        var stableOffCalls = jsRuntime.OffCalls;
        var sleepPanel = cut.FindComponent<SleepTimerPanel>().Instance;

        var selectionTask = cut.InvokeAsync(() => sleepPanel.SelectionChanged.InvokeAsync(
            new AudiobookSleepTimerSelectionDto { Mode = AudiobookSleepTimerModes.Timer, Minutes = 15 }));
        await jsRuntime.CandidateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Off, playback.SleepTimerState.Mode);
        Assert.Equal(1, jsRuntime.CandidateCalls);

        await OpenNowPlayingPageAsync(cut);
        Assert.Equal(stableOffCalls, jsRuntime.OffCalls);
        Assert.Equal(1, jsRuntime.CandidateCalls);

        jsRuntime.CandidateRegistration.TrySetResult(true);
        await selectionTask;

        Assert.False(playback.SleepTimerRegistrationInProgress);
        Assert.Equal(AudiobookSleepTimerModes.Timer, playback.SleepTimerState.Mode);
        Assert.Equal(15, playback.SleepTimerState.ChosenMinutes);
        Assert.Equal(stableOffCalls, jsRuntime.OffCalls);

        // The moon's accessible name says the timer is on while it runs (the bar no longer overrides the panel's own wording), and a one-chapter book still
        // hands its chapter to the panel.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-playback-zone='tools'] button[aria-label='Sleep timer, on']")));
        Assert.Empty(cut.FindAll("[data-playback-zone='tools'] button[aria-label='Sleep timer']"));
        Assert.Single(cut.FindComponent<SleepTimerPanel>().Instance.Chapters);

        // Unified player v2 A4 and D16: the timer lives in the session, so opening Now playing and closing it again neither resets nor moves the
        // deadline, and the bar's Sleep timer panel keeps showing the session's own state.
        var deadline = playback.SleepTimerState.DeadlineUtc;
        Assert.NotNull(deadline);
        await LeaveNowPlayingPageAsync(cut);
        Assert.Equal(deadline, playback.SleepTimerState.DeadlineUtc);
        Assert.Equal(deadline, cut.FindComponent<SleepTimerPanel>().Instance.State.DeadlineUtc);
        Assert.Equal(15, cut.FindComponent<SleepTimerPanel>().Instance.State.ChosenMinutes);
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

        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));
        var initialOffCalls = jsRuntime.OffCalls;
        var sleepPanel = cut.FindComponent<SleepTimerPanel>().Instance;
        var selectionTask = cut.InvokeAsync(() => sleepPanel.SelectionChanged.InvokeAsync(
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

        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        cut.WaitForAssertion(() => Assert.True(jsRuntime.OffCalls > 0));
        Assert.Empty(jsRuntime.SeekPositions);

        await cut.InvokeAsync(() => playback.PlayAudiobookChapterAsync(item, item.Chapters[0]));
        cut.WaitForAssertion(() => Assert.Contains(0d, jsRuntime.SeekPositions));
    }

    [Fact]
    public async Task TheChapterOrWholeBookChoiceMadeInThePanelChangesTheBarsTimelineLabel()
    {
        var api = EngineApiClientStub.CreateDefault();
        var asset = Guid.NewGuid();
        var book = CreateItem("The Way Beyond", "Maya Ellis") with
        {
            MediaType = "Audiobook",
            AssetId = asset,
            Chapters = [new() { Index = 0, AssetId = asset, Title = "One", StartSeconds = 0, EndSeconds = 1000 }, new() { Index = 1, AssetId = asset, Title = "Two", StartSeconds = 1000, EndSeconds = 4000 }],
        };
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
        playback.RestoreState(new ListenPlaybackSnapshot
        {
            Queue = [book],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Audiobook,
            DurationSeconds = 4000,
            CurrentTimeSeconds = 1500,
            IsPlaying = true,
        });
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        Services.AddSingleton(provider => new PlaybackTimelineScope(provider.GetRequiredService<IJSRuntime>(), playback));
        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        var scope = Services.GetRequiredService<PlaybackTimelineScope>();

        cut.WaitForAssertion(() => Assert.Equal("Chapter 2", cut.Find(".playback-seek-rail__label").TextContent.Trim()));
        await cut.InvokeAsync(() => scope.SetWholeBookAsync(true));
        cut.WaitForAssertion(() => Assert.Equal("Book \u00b7 37%", cut.Find(".playback-seek-rail__label").TextContent.Trim()));
        await cut.InvokeAsync(() => scope.SetWholeBookAsync(false));
        cut.WaitForAssertion(() => Assert.Equal("Chapter 2", cut.Find(".playback-seek-rail__label").TextContent.Trim()));
    }

    [Fact]
    public async Task AVeryLongQueueAndATitleThatDoesNotFitNeverChangeTheBarsLayout()
    {
        // Acceptance A7: the bar's zones and their classes are the same with three songs and with 200 songs and a very long title.
        var api = EngineApiClientStub.CreateDefault();
        var playback = CreatePlayback(CreateItem("Short", "Artist"), CreateItem("Next", "Artist"), CreateItem("Later", "Artist"));
        AddBarServices(playback, api, new ListenContextSidebarState(new MemorySidebarPreferences()));
        Render<AppPopoverHost>();
        var cut = Render<ListenNowPlayingBar>();
        string Structure() => string.Join("|", cut.FindAll("[data-playback-zone]").Select(zone =>
            $"{zone.GetAttribute("data-playback-zone")}:{zone.ClassName}:{zone.Children.Length}")) + "#" + cut.Find(".playback-shell").ClassName;
        var before = Structure();

        var longTitle = string.Concat(Enumerable.Repeat("An Extremely Long Song Title That Cannot Possibly Fit Beside The Controls ", 8)).Trim();
        var queue = new List<ListenQueueItem> { CreateItem(longTitle, string.Concat(Enumerable.Repeat("A very long artist name ", 12)).Trim()) };
        queue.AddRange(Enumerable.Range(0, 200).Select(index => CreateItem($"Song {index}", "Artist")));
        await cut.InvokeAsync(() => playback.RestoreState(new ListenPlaybackSnapshot { Queue = queue, CurrentIndex = 0, IsPlaying = true }));
        cut.WaitForAssertion(() => Assert.Contains(longTitle, cut.Markup));
        var queueTool = cut.FindComponents<ListenDockTool>().Single(tool => tool.Instance.PanelKey == "queue");
        await cut.InvokeAsync(() => queueTool.Instance.OpenAsync());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".plt")));

        Assert.Equal(before, Structure());
        Assert.True(cut.FindAll("[data-playback-list-row='queue']").Count < 200, "The 200 song queue must be virtualised.");
        Assert.Equal(longTitle, cut.Find("[data-playback-zone='identity'] .playback-identity__title").TextContent.Trim());
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

    // ---- Playback settings and Speed on the real bar (unified player v2 D14, D15) ----

    private sealed class SavedPreferences(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        private UserPlaybackSettingsDto _current = settings;
        public Guid? ActiveProfileId { get; } = settings.ProfileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(_current);
        public void UpdateCache(UserPlaybackSettingsDto next) => _current = next;
        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }

    private IRenderedComponent<ListenNowPlayingBar> RenderBarWithSavedPreferences(PlaybackSessionController playback, List<UserPlaybackSettingsDto> saved)
    {
        var profile = playback.ActiveProfileId ?? Guid.NewGuid();
        var preferences = new SavedPreferences(UserPlaybackSettingsDto.CreateDefaults(profile));
        var api = EngineApiClientStub.CreateDefault();
        var stub = (EngineApiClientStub)(object)api;
        stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
            { new(profile, "Listener", "#000000", "RestrictedProfile", DateTimeOffset.UtcNow) }));
        stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => Task.FromResult<UserPlaybackSettingsDto?>(saved.Count > 0 ? saved[^1] : UserPlaybackSettingsDto.CreateDefaults(profile)));
        stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
        {
            saved.Add((UserPlaybackSettingsDto)args![1]!);
            return Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto)args[1]!);
        });
        var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(profile));
        var orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationBuilder().Build(), NullLogger<UIOrchestratorService>.Instance);
        var preferenceService = new PlayerPreferenceService(preferences, orchestrator);
        var workspace = new ListenContextSidebarState(new MemorySidebarPreferences());
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton<IEngineApiClient>(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(workspace);
        Services.AddSingleton(preferenceService);
        Services.AddSingleton(new PlaybackSettingsActions(preferenceService, playback));
        Services.AddSingleton(new PlaybackLyricsPresenter(api));
        Services.AddSingleton(new PlaybackLyricsSelectionOwner(playback, api));
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences);
        Services.AddSingleton<IPlaybackCommandSink>(provider => new DirectPlaybackCommandSink(new ListenPlaybackCommandOwner(provider, playback)));
        Services.AddSingleton(new DeviceContextService(api));
        Services.AddSingleton<ListenAudioDragService>();
        Services.AddSingleton(new MediaReactionService(api));
        var actions = new AudiobookBookmarkActionService(api);
        Services.AddSingleton(actions);
        Services.AddSingleton<IAudiobookBookmarkActions>(actions);
        Services.AddSingleton<IAudiobookBookmarkLeaseInvalidator>(actions);
        Services.AddSingleton(profiles);
        Services.AddSingleton(orchestrator);
        Services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        Render<AppPopoverHost>();
        return Render<ListenNowPlayingBar>();
    }

    private static PlaybackSessionController CreateAudiobookPlayback(string experience)
    {
        var playback = new PlaybackSessionController(null!, null!, preferences: new TestPlaybackPreferencesAccessor(Guid.NewGuid()));
        var item = CreateItem(experience == PlayerExperienceModes.Audiobook ? "Test audiobook" : "Test song", "Narrator") with
        {
            MediaType = experience == PlayerExperienceModes.Audiobook ? "Audiobook" : "Music",
            Chapters = experience == PlayerExperienceModes.Audiobook
                ? [new PlaybackChapterDto { Index = 0, Title = "Chapter one", StartSeconds = 0, EndSeconds = 3600 }]
                : [],
        };
        playback.RestoreState(new ListenPlaybackSnapshot { Queue = [item], CurrentIndex = 0, Experience = experience, DurationSeconds = 3600, CurrentTimeSeconds = 120, IsPlaying = true });
        return playback;
    }

    private async Task OpenItemMenuRowAsync(IRenderedComponent<ListenNowPlayingBar> bar)
    {
        var menu = bar.FindComponent<PlaybackItemMenu>().FindComponent<PlaybackPanel>();
        await bar.InvokeAsync(() => menu.Instance.OpenAsync(true));
        bar.WaitForElement("[data-playback-item-action='PlaybackSettings']");
        await bar.Find("[data-playback-item-action='PlaybackSettings']").ClickAsync();
        bar.WaitForElement("[data-playback-settings]");
    }

    [Fact]
    public async Task AudiobookBar_TheSpeedChipOpensTheSpeedViewDirectlyWithNoSettingsRows()
    {
        var bar = RenderBarWithSavedPreferences(CreateAudiobookPlayback(PlayerExperienceModes.Audiobook), []);
        var chip = bar.Find("[data-playback-zone='tools'] button[aria-label^='Playback speed']");
        Assert.Equal("1×", chip.TextContent.Trim());

        await bar.InvokeAsync(() => bar.FindComponent<PlaybackSpeedPanel>().FindComponent<PlaybackPanel>().Instance.OpenAsync(true));

        bar.WaitForElement("[data-playback-speed-view]");
        Assert.Empty(bar.FindAll("[data-psp-row]"));
        Assert.DoesNotContain("Custom speed", bar.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AudiobookBar_TheItemMenusPlaybackSettingsRowOpensThePanelWithSpeedAndSkipsAndDoesNotNavigate()
    {
        var bar = RenderBarWithSavedPreferences(CreateAudiobookPlayback(PlayerExperienceModes.Audiobook), []);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        await OpenItemMenuRowAsync(bar);

        Assert.Equal(["speed", "skip-back", "skip-forward", "information"], bar.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
        Assert.DoesNotContain("/settings/playback", navigation.Uri, StringComparison.Ordinal);
        Assert.Equal(before, navigation.Uri);
    }

    [Fact]
    public async Task MusicBar_TheItemMenusPlaybackSettingsRowOpensThePanelWithPlaybackInformationOnlyAndNoResetFooter()
    {
        var bar = RenderBarWithSavedPreferences(CreateAudiobookPlayback(PlayerExperienceModes.Music), []);
        var navigation = Services.GetRequiredService<NavigationManager>();
        var before = navigation.Uri;

        await OpenItemMenuRowAsync(bar);

        Assert.Equal(["information"], bar.FindAll("[data-psp-row]").Select(row => row.GetAttribute("data-psp-row")));
        Assert.Empty(bar.FindAll("[data-psp-reset]"));
        Assert.Equal(before, navigation.Uri);
    }

    [Fact]
    public async Task AudiobookBar_ChoosingASkipLengthWritesTheListeningPairAndTheControllerTakesItAtOnce()
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var playback = CreateAudiobookPlayback(PlayerExperienceModes.Audiobook);
        var bar = RenderBarWithSavedPreferences(playback, saved);
        await OpenItemMenuRowAsync(bar);

        await bar.Find("[data-psp-row='skip-forward'] select").ChangeAsync(new ChangeEventArgs { Value = "60" });

        bar.WaitForAssertion(() => Assert.NotEmpty(saved));
        Assert.Equal(60, saved[^1].Listening.SkipForwardSeconds);
        Assert.Equal(30, saved[^1].Watching.SkipForwardSeconds);
        Assert.Equal(60, playback.SkipForwardSeconds);
    }

    [Fact]
    public async Task AudiobookBar_ResetPlaybackPreferencesResetsOnlyTheListeningPairAndSpeed()
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var playback = CreateAudiobookPlayback(PlayerExperienceModes.Audiobook);
        var bar = RenderBarWithSavedPreferences(playback, saved);
        await OpenItemMenuRowAsync(bar);
        await bar.Find("[data-psp-row='skip-back'] select").ChangeAsync(new ChangeEventArgs { Value = "30" });
        bar.WaitForAssertion(() => Assert.Single(saved));
        saved[0].Watching.SkipBackSeconds = 5;

        await bar.Find("[data-psp-reset]").ClickAsync();

        bar.WaitForAssertion(() => Assert.Equal(2, saved.Count));
        Assert.Equal(10, saved[^1].Listening.SkipBackSeconds);
        Assert.Equal(5, saved[^1].Watching.SkipBackSeconds);
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

    // Now playing on a desktop is the routed page (unified player v2 D24): the bar knows it is open from the address and shows its Now playing tool as selected.
    private async Task OpenNowPlayingPageAsync(IRenderedComponent<ListenNowPlayingBar> bar)
    {
        await bar.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo(NowPlayingRoute.Path));
        bar.WaitForAssertion(() => Assert.Contains("is-selected", bar.Find("[data-playback-zone='tools'] button[aria-label='Now playing']").ClassName));
    }

    private async Task LeaveNowPlayingPageAsync(IRenderedComponent<ListenNowPlayingBar> bar)
    {
        await bar.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/listen"));
        bar.WaitForAssertion(() => Assert.DoesNotContain("is-selected", bar.Find("[data-playback-zone='tools'] button[aria-label='Now playing']").ClassName));
    }

    private sealed class BarToggle : ComponentBase
    {
        [Parameter] public bool Show { get; set; }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            if (!Show)
            {
                return;
            }

            builder.OpenComponent<ListenNowPlayingBar>(0);
            builder.CloseComponent();
        }
    }

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
            {
                return ValueTask.FromResult((TValue)(object)new NoopJsObjectReference());
            }

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
