using Bunit;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Contracts.Details;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

public sealed class VideoPresentationTests
{
    [Fact]
    public void EpisodeRowsKeepTheNumberSeparateAndReserveTheActivityMarker()
    {
        using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        var episode = new VideoOwnedEpisode(Guid.NewGuid(), "A Message Across the Water", null, null,
            "3:00", "2", "Season 2", 2, 5);
        var cut = ctx.Render<VideoContextPanel>(parameters => parameters
            .Add(p => p.Episodes, [episode]).Add(p => p.CurrentWorkId, episode.WorkId));
        Assert.Equal(episode.Title, cut.Find(".playback-sheet-row__copy strong").TextContent);
        Assert.Equal("E5", cut.Find(".playback-sheet-row__copy > span").TextContent);
        Assert.Single(cut.FindAll(".video-context-current"));
        Assert.Equal("Paused", cut.Find(".video-context-current [role='img']").GetAttribute("aria-label"));
        cut.Render(parameters => parameters.Add(p => p.IsPlaying, true));
        Assert.Equal("Playing", cut.Find(".video-context-current [role='img']").GetAttribute("aria-label"));
        cut.Render(parameters => parameters.Add(p => p.NextEpisode, episode));
        cut.Find("button[aria-label='Up Next']").Click();
        Assert.Equal("S2 E5 · A Message Across the Water", cut.Find(".video-context-panel__featured strong").TextContent);
    }

    [Theory]
    [InlineData(true, true, 2, 3, 2, "episodes")]
    [InlineData(true, true, 0, 3, 2, "queue")]
    [InlineData(true, true, 0, 3, 0, null)]
    [InlineData(true, false, 0, 3, 2, "queue")]
    [InlineData(true, false, 0, 3, 0, null)]
    [InlineData(true, false, 1, 0, 2, "episodes")]
    [InlineData(false, true, 0, 2, 2, "chapters")]
    [InlineData(false, true, 0, 1, 2, "queue")]
    [InlineData(false, false, 0, 3, 0, null)]
    [InlineData(false, false, 0, 3, 2, "queue")]
    [InlineData(false, true, 0, 1, 0, null)]
    public void ContextPriorityUsesOnlyRealApplicableChoices(bool tv, bool movie, int episodes, int chapters, int queue, string? expected)
        => Assert.Equal(expected, VideoPresentationResolver.ContextKind(tv, movie, episodes, chapters, queue));

    [Fact]
    public void OwnedEpisodeProjectionIncludesUnnumberedTvAndDoesNotUseFallbackShowArtwork()
    {
        var owned = Guid.NewGuid();
        var sequence = new SequencePlacementViewModel { OrderedItems = [
            new() { Id = owned.ToString(), EntityType = DetailEntityType.TvEpisode, Title = "Unnumbered episode", IsOwned = true, ArtworkUrl = "/show-cover" },
            new() { Id = Guid.NewGuid().ToString(), EntityType = DetailEntityType.TvEpisode, IsOwned = false },
            new() { Id = Guid.NewGuid().ToString(), EntityType = DetailEntityType.Movie, IsOwned = true }] };
        var episode = Assert.Single(VideoPresentationResolver.OwnedEpisodes(sequence));
        Assert.Equal(owned, episode.WorkId); Assert.Null(episode.StillUrl);
        Assert.True(VideoPresentationResolver.IsTvEpisode(Video() with { EpisodeNumber = null }));
        var real = episode with { StillUrl = "/stream/artwork/actual", StillWidthPx = 1920, StillHeightPx = 1080 };
        Assert.Contains("320w", VideoContextPanel.EpisodeArtworkSrcSet(real));
        Assert.Contains("960w", VideoContextPanel.EpisodeArtworkSrcSet(real));
        Assert.Null(VideoContextPanel.EpisodeArtworkSrcSet(real with { StillWidthPx = null }));
    }

    [Theory]
    [InlineData("request")]
    [InlineData("profile")]
    [InlineData("asset")]
    public async Task MetadataResponseForReplacedIdentityCannotBecomeCurrent(string replacement)
    {
        var response = new TaskCompletionSource<DetailPageViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => response.Task));
        var preferences = new Preferences();
        var playback = CreatePlayback(preferences, api);
        var resolver = new VideoPresentationResolver(api, null!, playback);
        var identity = VideoPlaybackIdentity.Capture(playback)!;
        var loading = resolver.ResolveAsync(identity, default);
        if (replacement == "request")
        {
            playback.ReservePlaybackRequest();
        }
        else if (replacement == "profile")
        {
            preferences.ActiveProfileId = Guid.NewGuid();
        }
        else
        {
            playback.RestoreState(new() { Queue = [Video()], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        }
        response.SetResult(new() { SequencePlacement = new() { OrderedItems = [new() { Id = identity.WorkId.ToString(), EntityType = DetailEntityType.TvEpisode, IsOwned = true }] } });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
    }

    [Fact]
    public async Task EpisodeSelectionRechecksOwnershipAndPlayableAssetBeforeStarting()
    {
        var requested = Guid.NewGuid(); var resolves = 0;
        var page = new DetailPageViewModel { SequencePlacement = new() { OrderedItems = [new() { Id = requested.ToString(), IsOwned = true, EntityType = DetailEntityType.TvEpisode }] } };
        var api = EngineApiClientStub.Create(stub => {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => Task.FromResult<DetailPageViewModel?>(page));
            stub.SetHandler(nameof(IEngineApiClient.ResolveWorkToAssetAsync), _ => { resolves++; return Task.FromResult<Guid?>(null); });
        });
        await using var orchestrator = new UIOrchestratorService(api, null!, null!, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var playback = CreatePlayback(new(), api); var identity = VideoPlaybackIdentity.Capture(playback)!;
        var resolver = new VideoPresentationResolver(api, orchestrator, playback);
        Assert.False(await resolver.PlayEpisodeAsync(identity, Guid.NewGuid(), default));
        Assert.Equal(0, resolves);
        Assert.False(await resolver.PlayEpisodeAsync(identity, requested, default));
        Assert.Equal(1, resolves); Assert.True(identity.IsCurrent(playback));
    }

    [Fact]
    public async Task OwnedEpisodeHandoffReachesNewNativeStartWhenRequestChangeCancelsOldMetadata()
    {
        var work = Guid.NewGuid(); var asset = Guid.NewGuid();
        var page = new DetailPageViewModel { SequencePlacement = new() { OrderedItems =
            [new() { Id = work.ToString(), EntityType = DetailEntityType.TvEpisode, IsOwned = true }] } };
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => Task.FromResult<DetailPageViewModel?>(page));
            stub.SetHandler(nameof(IEngineApiClient.ResolveWorkToAssetAsync), _ => Task.FromResult<Guid?>(asset));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDetailAsync), _ => Task.FromResult<LibraryItemDetailViewModel?>(new() { Title = "Episode five", MediaType = "TV" }));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), _ => Task.FromResult<PlaybackManifestDto?>(new()
                { AssetId = asset, MediaType = "TV", DirectPlaySupported = true, DirectStreamUrl = $"/stream/{asset:D}", DurationSeconds = 180 }));
        });
        await using var orchestrator = new UIOrchestratorService(api, null!, null!, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var playback = new PlaybackSessionController(orchestrator, api, preferences: new Preferences());
        playback.RestoreState(new() { Queue = [Video()], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        var identity = VideoPlaybackIdentity.Capture(playback)!;
        using var oldMetadata = new CancellationTokenSource();
        playback.PlaybackRequestChanged += oldMetadata.Cancel;
        var starts = new List<PlaybackTransportCommand>();
        playback.TransportCommandRequested += command => { starts.Add(command); playback.MarkPlaybackStarted(); return Task.CompletedTask; };
        await playback.SetTransportHostReadyAsync();
        var resolver = new VideoPresentationResolver(api, orchestrator, playback);
        Assert.True(await resolver.PlayEpisodeAsync(identity, work, oldMetadata.Token));
        Assert.True(oldMetadata.IsCancellationRequested);
        Assert.Equal(identity.RequestVersion + 1, playback.PlaybackRequestVersion);
        Assert.Equal(work, playback.CurrentItem!.WorkId); Assert.Equal(asset, playback.CurrentItem.AssetId);
        var start = Assert.Single(starts);
        Assert.Equal("start", start.Action); Assert.Equal($"/engine-stream/{asset:D}", start.StreamUrl);
        Assert.Equal(PlaybackPhase.Playing, playback.Phase);
    }

    [Theory]
    [InlineData("metadata")]
    [InlineData("profile")]
    [InlineData("request")]
    [InlineData("disposal")]
    public async Task OwnedEpisodeStaleOrCancelledPreflightNeverHandsOffAStart(string change)
    {
        var work = Guid.NewGuid(); var asset = Guid.NewGuid();
        var detail = new TaskCompletionSource<LibraryItemDetailViewModel?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetDetailPageAsync), _ => Task.FromResult<DetailPageViewModel?>(new()
                { SequencePlacement = new() { OrderedItems = [new() { Id = work.ToString(), EntityType = DetailEntityType.TvEpisode, IsOwned = true }] } }));
            stub.SetHandler(nameof(IEngineApiClient.ResolveWorkToAssetAsync), _ => Task.FromResult<Guid?>(asset));
            stub.SetHandler(nameof(IEngineApiClient.GetLibraryItemDetailAsync), _ => { entered.TrySetResult(); return detail.Task; });
        });
        var preferences = new Preferences();
        await using var orchestrator = new UIOrchestratorService(api, null!, null!, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var playback = new PlaybackSessionController(orchestrator, api, preferences: preferences);
        var original = Video(); playback.RestoreState(new() { Queue = [original], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        var identity = VideoPlaybackIdentity.Capture(playback)!;
        var starts = new List<PlaybackTransportCommand>();
        playback.TransportCommandRequested += command => { starts.Add(command); return Task.CompletedTask; };
        await playback.SetTransportHostReadyAsync();
        using var metadata = new CancellationTokenSource(); using var lifetime = new CancellationTokenSource();
        var loading = new VideoPresentationResolver(api, orchestrator, playback).PlayEpisodeAsync(identity, work, metadata.Token, lifetime.Token);
        await entered.Task;
        if (change == "metadata")
        {
            metadata.Cancel();
        }
        else if (change == "profile")
        {
            preferences.ActiveProfileId = Guid.NewGuid();
        }
        else if (change == "request")
        {
            playback.ReservePlaybackRequest();
        }
        else
        {
            lifetime.Cancel();
        }
        detail.SetResult(new() { Title = "Episode five", MediaType = "TV" });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => loading);
        Assert.Equal(original.AssetId, playback.CurrentItem!.AssetId);
        Assert.Empty(starts);
        Assert.Equal(identity.RequestVersion + (change == "request" ? 1 : 0), playback.PlaybackRequestVersion);
    }

    [Fact]
    public void EndCardClockHoldsResetsOnSeekAndRejectsAnOldInstanceTick()
    {
        var identity = new VideoPlaybackIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var next = Guid.NewGuid(); var card = new VideoEndCardState();
        card.Observe(identity, next, 90, 100, []); Assert.True(card.Visible);
        card.Tick(identity, 1, true); Assert.Equal(9, card.RemainingSeconds);
        card.Tick(identity, 1, false); Assert.Equal(9, card.RemainingSeconds);
        card.Observe(identity, next, 30, 100, []); Assert.False(card.Visible); Assert.Equal(10, card.RemainingSeconds);
        card.Observe(identity, next, 90, 100, []); card.Dismiss();
        card.Observe(identity, next, 90, 100, []); Assert.False(card.Visible);
        var reopened = identity with { RequestVersion = 2 };
        card.Observe(reopened, next, 90, 100, []); Assert.True(card.Visible);
        Assert.False(card.Tick(identity, 1, true)); Assert.Equal(10, card.RemainingSeconds);
        for (var tick = 0; tick < 9; tick++)
        {
            Assert.False(card.Tick(reopened, 1, true));
        }
        Assert.True(card.Tick(reopened, 1, true));
        card.Observe(reopened, null, 90, 100, []); Assert.False(card.Visible);
    }

    [Fact]
    public void CreditsTriggerRequiresVerifiedValidDataAndUnknownDurationHasNoCard()
    {
        var identity = new VideoPlaybackIdentity(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var next = Guid.NewGuid(); var card = new VideoEndCardState();
        var credits = new PlaybackSegmentDto { Kind = "credits", StartSeconds = 60, EndSeconds = 100, ReviewStatus = "verified" };
        card.Observe(identity, next, 65, 100, [credits]); Assert.True(card.Visible);
        card.Observe(identity, next, 65, 100, [credits with { ReviewStatus = "detected" }]); Assert.False(card.Visible);
        card.Observe(identity, next, 65, 100, [credits with { EndSeconds = 150 }]); Assert.False(card.Visible);
        card.Observe(identity, next, 65, 100, [credits with { EndSeconds = null }]); Assert.True(card.Visible);
        card.Observe(identity, next, 100, 0, []); Assert.False(card.Visible);
    }

    [Fact]
    public async Task ConcurrentCountdownAndNativeEndedAdvanceOnceAndDelayedOldEndedCannotAdvanceAgain()
    {
        var playback = CreatePlayback(new());
        var second = Video(); var third = Video();
        playback.RestoreState(new() { Queue = [playback.CurrentItem!, second, third], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        var identity = VideoPlaybackIdentity.Capture(playback)!;
        await Task.WhenAll(playback.CompleteVideoOnceAsync(identity), playback.CompleteVideoOnceAsync(identity));
        Assert.Equal(second.WorkId, playback.CurrentItem!.WorkId); Assert.Equal(1, playback.CurrentIndex);
        await playback.CompleteVideoOnceAsync(identity);
        Assert.Equal(1, playback.CurrentIndex);
    }

    [Fact]
    public async Task ProfileChangeDuringNextAssetRevalidationPreventsAnAutomaticStart()
    {
        var response = new TaskCompletionSource<Guid?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.ResolveWorkToAssetAsync), _ => response.Task));
        var preferences = new Preferences();
        await using var orchestrator = new UIOrchestratorService(api, null!, null!, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var playback = new PlaybackSessionController(orchestrator, api, preferences: preferences);
        var next = Video(); playback.RestoreState(new() { Queue = [Video(), next], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        var identity = VideoPlaybackIdentity.Capture(playback)!;
        var completion = playback.CompleteVideoOnceAsync(identity);
        preferences.ActiveProfileId = Guid.NewGuid(); response.SetResult(next.AssetId);
        Assert.False(await completion); Assert.Equal(0, playback.CurrentIndex);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("active")]
    [InlineData("resolved")]
    public async Task HeartbeatChecksCapturedProfileAfterAwaitingProfileResolution(string replacement)
    {
        var preferences = new Preferences();
        var capturedProfile = preferences.ActiveProfileId!.Value;
        var resolvedProfile = replacement == "resolved" ? Guid.NewGuid() : capturedProfile;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<List<ProfileViewModel>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var heartbeats = new List<PlayerHeartbeatDto>();
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => { entered.TrySetResult(); return response.Task; });
            stub.SetHandler(nameof(IEngineApiClient.PostPlayerHeartbeatAsync), args =>
            {
                heartbeats.Add((PlayerHeartbeatDto)args![0]!);
                return Task.FromResult<PlayerStateDto?>(null);
            });
        });
        using var profiles = new ActiveProfileSessionService(null!, api,
            authenticationStateProvider: new ProfileAuthentication(resolvedProfile));
        await using var orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles,
            new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
        var playback = new PlaybackSessionController(orchestrator, api, preferences: preferences);
        var item = Video();
        playback.RestoreState(new() { Queue = [item], CurrentIndex = 0, Experience = PlayerExperienceModes.Video,
            CurrentTimeSeconds = 37, DurationSeconds = 100, IsPlaying = false });
        var heartbeat = playback.ReportHeartbeatAsync(force: true);
        await entered.Task;
        if (replacement == "active")
        {
            preferences.ActiveProfileId = Guid.NewGuid();
        }
        response.SetResult([new ProfileViewModel(resolvedProfile, "Viewer", "#000000", "RestrictedProfile", DateTimeOffset.UtcNow)]);
        await heartbeat;
        if (replacement == "unchanged")
        {
            var sent = Assert.Single(heartbeats);
            Assert.Equal(capturedProfile, sent.ProfileId);
            Assert.Equal(item.AssetId, sent.AssetId);
            Assert.Equal(37, sent.PositionSeconds);
            Assert.False(sent.IsPlaying);
            Assert.False(sent.HasPlaybackEnded);
        }
        else
        {
            Assert.Empty(heartbeats);
        }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }

    [Fact]
    public void NativeVideoMetricsAfterAutoplayRefusalKeepPlayActionAvailableUntilGestureStartsPlayback()
    {
        var playback = CreatePlayback(new Preferences());
        playback.MarkNeedsUserGestureToStart();
        playback.UpdateTransportState(currentTimeSeconds: 99, durationSeconds: 180, isPlaying: false);
        Assert.Equal(PlaybackPhase.NeedsGesture, playback.Phase);
        Assert.True(playback.NeedsUserGestureToStart);
        Assert.False(playback.IsPlaying);
        playback.MarkPlaybackStarted();
        Assert.Equal(PlaybackPhase.Playing, playback.Phase);
        Assert.False(playback.NeedsUserGestureToStart);
        Assert.Null(playback.CurrentError);
    }

    private static PlaybackSessionController CreatePlayback(Preferences preferences, IEngineApiClient? api = null)
    {
        var playback = new PlaybackSessionController(null!, api!, preferences: preferences);
        playback.RestoreState(new() { Queue = [Video()], CurrentIndex = 0, Experience = PlayerExperienceModes.Video });
        return playback;
    }
    private static ListenQueueItem Video() => new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Episode", MediaType = "TvEpisode", StreamUrl = "stream://video", Duration = "1:40" };
    private sealed class Preferences : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; set; } = Guid.NewGuid();
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}

public sealed class VideoPresentationComponentTests : AsyncBunitContext
{
    public VideoPresentationComponentTests() { JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddNativeUiServices(); Services.AddSingleton(new PlaybackTransientToolCoordinator()); Render<AppPopoverHost>(); }

    [Fact]
    public void ChapterTickLabelsDoNotEraseKeyboardSeekValueAndUnknownDurationDisablesSeek()
    {
        var cut = Render<AppRangeSlider>(p => p.Add(x => x.Value, 20).Add(x => x.Max, 100).Add(x => x.TickValues, new double[] { 0, 50 })
            .Add(x => x.TickFormatter, _ => string.Empty).Add(x => x.ValueFormatter, value => $"{value} seconds"));
        Assert.Equal("20 seconds", cut.Find("input").GetAttribute("aria-valuetext"));
        cut.Render(p => p.AddUnmatched("disabled", true)); Assert.True(cut.Find("input").HasAttribute("disabled"));
    }

    [Theory]
    [InlineData("standard")]
    [InlineData("playback-flat")]
    public async Task NestedSeasonSelectorKeepsItsOwningPlaybackPanelOpen(string appearance)
    {
        var tools = Services.GetRequiredService<PlaybackTransientToolCoordinator>();
        var cut = Render<PlaybackPopover>(p => p.Add(x => x.ToolId, "episodes").Add(x => x.Title, "Episodes")
            .AddChildContent<AppSelect>(select => select.Add(x => x.Options, new[] { new AppSelectOption("1", "Season 1"), new AppSelectOption("2", "Season 2") }).Add(x => x.Appearance, appearance)));
        await cut.InvokeAsync(() => cut.Instance.OpenAsync(true));
        var select = cut.FindComponent<AppSelect>();
        Assert.NotNull(select.Instance.PlaybackPopoverOwner);
        await select.Find(".tl-select-trigger").ClickAsync();
        Assert.True(tools.IsOpen("episodes")); Assert.Contains("data-playback-parent-panel", select.Markup);
    }
}

public sealed class VideoSubtitleCloseRegressionTests : AsyncBunitContext
{
    private readonly PlaybackSessionController _playback;
    private int _trackLoads;
    public VideoSubtitleCloseRegressionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose; Services.AddLogging(); Services.AddNativeUiServices();
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => {
            _trackLoads++; return Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = Guid.NewGuid(), Kind = "Subtitle", Language = "en" }]);
        }));
        _playback = new PlaybackSessionController(null!, api, preferences: new Preferences());
        _playback.RestoreState(new() { Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Movie", MediaType = "Movie" }],
            CurrentIndex = 0, Experience = PlayerExperienceModes.Video, IsVideoExpanded = true });
        Services.AddSingleton(api); Services.AddSingleton(_playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, _playback));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(_playback));
        Services.AddSingleton(new UniverseStateContainer()); Services.AddSingleton<ShellActivityState>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(new Preferences());
        Services.AddSingleton<Microsoft.JSInterop.IJSRuntime>(new ViewerFixtureJsRuntime());
        Render<AppPopoverHost>();
    }
    [Fact]
    public async Task ClosingVideoWithLoadedManagedSubtitlesRendersWithoutAnAssetDereferenceAndKeepsVideoMounted()
    {
        var cut = Render<VideoPlaybackHost>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("track")));
        await cut.InvokeAsync(() => _playback.ClosePlayer());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("track")));
        Assert.Single(cut.FindAll("video"));
    }
    [Fact]
    public async Task ReopeningTheSameExpandedVideoRefreshesIdentityMetadataWithoutReplacingMedia()
    {
        var cut = Render<VideoPlaybackHost>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("track")));
        var asset = _playback.CurrentItem!.AssetId;
        var generation = _playback.PlaybackRequestVersion;
        await cut.InvokeAsync(() => { _playback.ReservePlaybackRequest(); _playback.SetVideoExpanded(true); });
        cut.WaitForAssertion(() => Assert.Equal(2, _trackLoads));
        Assert.Equal(asset, _playback.CurrentItem!.AssetId);
        Assert.Equal(generation + 1, _playback.PlaybackRequestVersion); Assert.Single(cut.FindAll("video"));
    }
    [Fact]
    public async Task PipHidesRestoreUntilEitherNativeExitAndRejectsStaleCallbacks()
    {
        var cut = Render<VideoPlaybackHost>();
        var identity = VideoPlaybackIdentity.Capture(_playback)!;
        Assert.Single(cut.FindAll("button[aria-label='Close video']"));
        Assert.Empty(cut.FindAll("button[aria-label='Back to details']"));
        await cut.InvokeAsync(() => cut.Instance.HandlePictureInPictureChanged(identity.ProfileId, identity.WorkId, identity.AssetId, identity.RequestVersion, true));
        Assert.False(_playback.IsVideoExpanded);
        Assert.Empty(cut.FindAll(".video-playback-restore"));
        await cut.InvokeAsync(() => cut.Instance.HandlePictureInPictureChanged(identity.ProfileId, identity.WorkId, identity.AssetId, identity.RequestVersion - 1, false));
        Assert.Empty(cut.FindAll(".video-playback-restore"));
        await cut.InvokeAsync(() => cut.Instance.HandlePictureInPictureChanged(identity.ProfileId, identity.WorkId, identity.AssetId, identity.RequestVersion, false));
        Assert.Single(cut.FindAll(".video-playback-restore"));
        Assert.True(_playback.HasQueue);
    }

    [Theory]
    [InlineData(MediaViewerKind.Image)]
    [InlineData(MediaViewerKind.Video)]
    public async Task ViewInfoTogglesInTheHeaderAndItsOnlyCloseInvokesTheHost(MediaViewerKind kind)
    {
        var closes = 0;
        var cut = Render<MediaViewerShell>(p => p.Add(x => x.Item, new MediaViewerItem("fixture", kind, "Fixture", null, "/preview", "/original"))
            .Add(x => x.InfoInitiallyOpen, true).Add(x => x.OnClose, () => closes++));
        Assert.Single(cut.FindAll(".media-viewer__info"));
        Assert.Empty(cut.FindAll(".media-viewer__info button"));
        Assert.Single(cut.FindAll("button[aria-label='Close viewer']"));
        var info = cut.Find("button[aria-label='Toggle information panel']");
        Assert.Equal("true", info.GetAttribute("aria-pressed"));
        await info.ClickAsync();
        Assert.Empty(cut.FindAll(".media-viewer__info"));
        Assert.Equal("false", cut.Find("button[aria-label='Toggle information panel']").GetAttribute("aria-pressed"));
        await cut.Find("button[aria-label='Close viewer']").ClickAsync();
        Assert.Equal(1, closes);
    }

    private sealed class ViewerFixtureJsRuntime : Microsoft.JSInterop.IJSRuntime, Microsoft.JSInterop.IJSObjectReference
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => InvokeAsync<TValue>(identifier, default, args);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellation, object?[]? args)
        {
            if (identifier == "import")
            {
                return ValueTask.FromResult((TValue)(object)this);
            }
            if (identifier == "readVideoState")
            {
                return ValueTask.FromResult(System.Text.Json.JsonSerializer.Deserialize<TValue>("{\"Position\":0,\"Duration\":180,\"Paused\":true,\"Muted\":false,\"Volume\":1,\"Speed\":1,\"TextTracks\":[],\"AudioTracks\":[]}")!);
            }
            return ValueTask.FromResult(default(TValue)!);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Preferences : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = Guid.NewGuid();
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(null);
        public void UpdateCache(UserPlaybackSettingsDto settings) { }
        public void Invalidate() { }
    }
}
