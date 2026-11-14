using System.Security.Claims;
using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Player;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Models.ViewDTOs;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MediaEngine.Web.Tests;

/// <summary>
/// Unified player v2 D12, D13 and acceptance A9 against the real video host and session controller: the Audio &amp; subtitles panel changes the playing
/// video's subtitle look at once, and switching audio through the Engine resumes at the second the player is at.
/// </summary>
public sealed class VideoAudioSubtitlesHostTests : AsyncBunitContext
{
    private sealed class Preferences(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = settings.ProfileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(settings);
        public void UpdateCache(UserPlaybackSettingsDto next) { }
        public void Invalidate() { }
    }

    private sealed class ProfileAuthentication(Guid profileId) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(
            new ClaimsPrincipal(new ClaimsIdentity([new Claim("tuvima:active_profile_id", profileId.ToString("D"))], "test"))));
    }

    private static readonly IReadOnlyList<PlaybackTrackDto> TwoAudioTracks =
    [
        new() { Index = 0, Language = "eng", Codec = "ac3", Channels = 6, IsDefault = true },
        new() { Index = 1, Language = "spa", Codec = "aac", Channels = 2 },
    ];

    private static PlaybackManifestDto Direct(Guid asset) => new()
    {
        AssetId = asset,
        MediaType = "Movie",
        DirectPlaySupported = true,
        DirectStreamUrl = "/stream/video",
        DurationSeconds = 7200,
        AudioTracks = TwoAudioTracks,
        SubtitleTracks = [new() { Index = 0, Language = "eng", DisplayName = "Subtitles (eng)", DeliveryUrl = "/subs/0" }, new() { Index = 1, Language = "eng", DisplayName = "English SDH", DeliveryUrl = "/subs/1" }],
    };

    private PlaybackSessionController Arrange(UserPlaybackSettingsDto settings, Guid asset, Func<Guid, PlaybackManifestDto?>? adaptive = null, List<UserPlaybackSettingsDto>? saved = null, Action<EngineApiClientStub>? configureApi = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var preferences = new Preferences(settings);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), _ => Task.FromResult(adaptive?.Invoke(asset)));
            if (saved is not null)
            {
                // The save path of the profile's playback settings: the profile, the settings read, and the settings written back.
                stub.SetHandler(nameof(IEngineApiClient.GetProfilesAsync), _ => Task.FromResult(new List<ProfileViewModel>
                    { new(settings.ProfileId, "Viewer", "#000000", "RestrictedProfile", DateTimeOffset.UtcNow) }));
                stub.SetHandler(nameof(IEngineApiClient.GetPlaybackSettingsAsync), _ => Task.FromResult<UserPlaybackSettingsDto?>(UserPlaybackSettingsDto.CreateDefaults(settings.ProfileId)));
                stub.SetHandler(nameof(IEngineApiClient.UpdatePlaybackSettingsAsync), args =>
                {
                    saved.Add((UserPlaybackSettingsDto)args![1]!);
                    return Task.FromResult<UserPlaybackSettingsDto?>((UserPlaybackSettingsDto)args[1]!);
                });
            }
            configureApi?.Invoke(stub);
        });
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "The Glass Harbor", MediaType = "Movie", StreamUrl = "/engine-stream/video", Manifest = Direct(asset) }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
        });
        if (saved is not null)
        {
            var profiles = new ActiveProfileSessionService(null!, api, authenticationStateProvider: new ProfileAuthentication(settings.ProfileId));
            var orchestrator = new UIOrchestratorService(api, new UniverseStateContainer(), profiles, new ConfigurationManager(), NullLogger<UIOrchestratorService>.Instance);
            Services.AddSingleton(new PlayerPreferenceService(preferences, orchestrator));
        }

        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback, preferences));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>(); Services.AddSingleton<MediaEngine.Web.Services.Integration.PlayingNowState>();
        Services.AddSingleton<IUserPlaybackPreferencesAccessor>(preferences);
        Render<AppPopoverHost>();
        return playback;
    }

    [Fact]
    public void TheBarHasOneAudioAndSubtitlesButtonAndNoSeparateSubtitleStyleButton()
    {
        Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), Guid.NewGuid());

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForElement("button.playback-popover-trigger[aria-label='Audio & subtitles']");
        var labels = cut.FindAll("button[aria-label]").Select(button => button.GetAttribute("aria-label")!).ToList();
        Assert.Single(labels, label => label == "Audio & subtitles");
        Assert.DoesNotContain(labels, label => label.Contains("style", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(labels, label => label is "Subtitles" or "Audio");
    }

    [Fact]
    public async Task ChoosingAPresetInSubtitleAppearance_ChangesThePlayingVideosSubtitleLookAtOnce()
    {
        Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), Guid.NewGuid());
        var cut = Render<VideoPlaybackHost>();
        cut.WaitForAssertion(() => Assert.Equal("Medium", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-size")));
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.Find("[data-asa-action='appearance']").ClickAsync();

        await cut.Find("[data-sav-preset='large']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal("Large", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-size")));
        Assert.Equal("large", cut.Find("[data-sav-preset][aria-checked='true']").GetAttribute("data-sav-preset"));
        await cut.Find(".sav__row .app-segmented-control__item:first-child").ClickAsync();
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-background")));
        // Background is its own choice (follow-ups D8): the ticked style stays ticked.
        Assert.Equal("large", cut.Find("[data-sav-preset][aria-checked='true']").GetAttribute("data-sav-preset"));
    }

    [Fact]
    public async Task OpeningAudioAndSubtitles_AsksOnceWhetherASourceIsOn_AndSearchesNothing()
    {
        var asset = Guid.NewGuid();
        Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), asset);
        var cut = Render<VideoPlaybackHost>();
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();

        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(false));
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));

        var calls = ((EngineApiClientStub)(object)Services.GetRequiredService<IEngineApiClient>()).Calls;
        cut.WaitForAssertion(() => Assert.Single(calls, name => name == nameof(IEngineApiClient.GetTextTrackSourcesAsync)));
        Assert.DoesNotContain(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), calls);
        Assert.DoesNotContain(nameof(IEngineApiClient.RefreshTextTracksAsync), calls);
    }

    [Fact]
    public async Task UsingASubtitleResult_ReloadsTheTitlesTracks_AndSelectsTheNewOne()
    {
        var asset = Guid.NewGuid();
        var chosen = Guid.NewGuid();
        var reloads = 0;
        Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), asset, configureApi: stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ =>
            {
                reloads++;
                return Task.FromResult<IReadOnlyList<TextTrackDto>>(reloads < 2 ? [] : [new() { Id = chosen, Kind = "Subtitles", Language = "en", Provider = "subdl", IsPreferred = true }]);
            });
            stub.SetHandler(nameof(IEngineApiClient.GetTextTrackAlternativesAsync), _ => Task.FromResult<TextTrackAlternativesResponse?>(new()
            {
                status = "Found",
                source_name = "SubDL",
                items = [new() { provider = "subdl", provider_name = "SubDL", source_id = "b", language = "en", confidence = 0.93, release_name = "Release.B" }],
            }));
            stub.SetHandler(nameof(IEngineApiClient.ChooseTextTrackAlternativeAsync),
                _ => Task.FromResult<TextTrackChoiceResponse?>(new() { status = "Updated", track_id = chosen }));
        });
        JSInterop.Setup<bool>("listenPlayback.selectCaptionTrack", _ => true).SetResult(true);
        JSInterop.Setup<VideoPlaybackHost.VideoMetrics>("listenPlayback.readAudioState", _ => true).SetResult(new VideoPlaybackHost.VideoMetrics { RequestVersion = -1 });
        var cut = Render<VideoPlaybackHost>();
        cut.WaitForAssertion(() => Assert.Equal(1, reloads));
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.Find("[data-asa-action='find']").ClickAsync();
        cut.WaitForElement("[data-sfv-results]");

        await cut.Find("[data-sfv-action='use']").ClickAsync();

        cut.WaitForAssertion(() => Assert.Equal(2, reloads));
        cut.WaitForAssertion(() => Assert.Contains(JSInterop.Invocations, call =>
            call.Identifier == "listenPlayback.selectCaptionTrack" && call.Arguments.Contains($"managed:{chosen:D}")));
        Assert.NotNull(cut.Find($"track[data-playback-track-key='managed:{chosen:D}']"));
    }

    [Fact]
    public async Task TheAudioColumnListsTheFilesTracksWithTheirFormat()
    {
        Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), Guid.NewGuid());
        var cut = Render<VideoPlaybackHost>();
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");

        var rows = cut.FindAll("[data-asv-audio]");
        Assert.Equal(["English", "Spanish"], rows.Select(row => row.QuerySelector(".asv__label")!.TextContent));
        Assert.Equal("Dolby Digital · 5.1", rows[0].QuerySelector(".asv__sub")!.TextContent);
        Assert.Equal("Stereo · 2.0", rows[1].QuerySelector(".asv__sub")!.TextContent);
        Assert.False(rows[1].HasAttribute("disabled"));
    }

    [Fact]
    public async Task SwitchingAudioThroughTheEngine_ResumesWithinASecondOfWhereThePlayerWas()
    {
        var asset = Guid.NewGuid();
        var playback = Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), asset, id => Direct(id) with
        {
            RecommendedDelivery = PlaybackDeliveryModes.Hls,
            HlsUrl = "/hls/master.m3u8",
            HlsStatus = "ready",
            DirectStreamUrl = null,
            DirectPlaySupported = false,
        });
        playback.UpdateTransportState(currentTimeSeconds: 1694.4, durationSeconds: 7200, isPlaying: true);

        var switched = await playback.SwitchVideoToAdaptiveAsync();

        Assert.True(switched);
        Assert.True(Math.Abs(playback.CurrentItem!.InitialPositionSeconds!.Value - 1694.4) <= 1d, "A9: audio switching keeps the position within one second.");
        Assert.True(playback.CurrentItem.StartAtExactPosition);
    }

    [Fact]
    public async Task SwitchingAudioThroughTheEngine_ResumesAtTheSecondTheChoiceWasMadeAt()
    {
        var asset = Guid.NewGuid();
        var playback = Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), asset, id => Direct(id) with
        {
            RecommendedDelivery = PlaybackDeliveryModes.Hls,
            HlsUrl = "/hls/master.m3u8",
            HlsStatus = "ready",
            DirectStreamUrl = null,
            DirectPlaySupported = false,
        });
        playback.UpdateTransportState(currentTimeSeconds: 100, durationSeconds: 7200, isPlaying: true);

        // The request carries the second the choice was made at; the stream restarts there, not at whatever the clock reads when the Engine answers.
        var switched = await playback.SwitchVideoToAdaptiveAsync(resumeSeconds: 1694.4);

        Assert.True(switched);
        Assert.Equal(1694.4, playback.CurrentItem!.InitialPositionSeconds!.Value, 3);
        Assert.True(playback.CurrentItem.StartAtExactPosition);
    }

    [Fact]
    public async Task ChoosingAnotherAudioTrackInThePanel_RestartsTheStreamAtThePositionThePanelWasOpenedAt()
    {
        var asset = Guid.NewGuid();
        var playback = Arrange(UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid()), asset, id => Direct(id) with
        {
            RecommendedDelivery = PlaybackDeliveryModes.Hls,
            HlsUrl = "/hls/master.m3u8",
            HlsStatus = "ready",
            DirectStreamUrl = null,
            DirectPlaySupported = false,
        });
        playback.UpdateTransportState(currentTimeSeconds: 1694.4, durationSeconds: 7200, isPlaying: true);
        var cut = Render<VideoPlaybackHost>();
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");

        await cut.FindAll("[data-asv-audio]")[1].ClickAsync();

        cut.WaitForAssertion(() => Assert.True(playback.CurrentItem!.StartAtExactPosition));
        Assert.True(Math.Abs(playback.CurrentItem!.InitialPositionSeconds!.Value - 1694.4) <= 1d, "A9: the panel's choice keeps the position within one second.");
    }

    [Theory]
    [InlineData("large", PlaybackPreferenceValues.Large, true, 60)]
    [InlineData("contrast", PlaybackPreferenceValues.Medium, true, 100)]
    public async Task ChoosingAPreset_SavesThatPresetsValuesOnTheProfile(string preset, string size, bool background, int backgroundOpacity)
    {
        var saved = new List<UserPlaybackSettingsDto>();
        var profile = Guid.NewGuid();
        Arrange(UserPlaybackSettingsDto.CreateDefaults(profile), Guid.NewGuid(), saved: saved);
        var cut = Render<VideoPlaybackHost>();
        var panel = cut.FindComponent<AudioSubtitlesPanel>().FindComponent<PlaybackPanel>();
        await cut.InvokeAsync(() => panel.Instance.OpenAsync(true));
        cut.WaitForElement("[data-audio-subtitles]");
        await cut.Find("[data-asa-action='appearance']").ClickAsync();

        await cut.Find($"[data-sav-preset='{preset}']").ClickAsync();

        cut.WaitForAssertion(() => Assert.NotEmpty(saved));
        var look = saved[^1].Subtitles;
        Assert.Equal(profile, saved[^1].ProfileId);
        Assert.Equal(size, look.SubtitleSize);
        Assert.Equal(background, look.SubtitleBackground);
        Assert.Equal(backgroundOpacity, look.SubtitleBackgroundOpacity);
        Assert.Equal(SubtitleAppearance.Matching(look), preset == "large" ? SubtitlePreset.LargeText : SubtitlePreset.HighContrast);
    }
}
