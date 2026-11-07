using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

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

    private PlaybackSessionController Arrange(UserPlaybackSettingsDto settings, Guid asset, Func<Guid, PlaybackManifestDto?>? adaptive = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var preferences = new Preferences(settings);
        var api = EngineApiClientStub.Create(stub =>
        {
            stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ => Task.FromResult<IReadOnlyList<TextTrackDto>>([]));
            stub.SetHandler(nameof(IEngineApiClient.GetPlaybackManifestAsync), _ => Task.FromResult(adaptive?.Invoke(asset)));
        });
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = asset, Title = "The Glass Harbor", MediaType = "Movie", StreamUrl = "/engine-stream/video", Manifest = Direct(asset) }],
            CurrentIndex = 0,
            Experience = PlayerExperienceModes.Video,
            IsVideoExpanded = true,
        });
        Services.AddSingleton(api);
        Services.AddSingleton(playback);
        Services.AddSingleton(new VideoPresentationResolver(api, null!, playback, preferences));
        Services.AddSingleton(new PlaybackTransientToolCoordinator(playback));
        Services.AddSingleton(new UniverseStateContainer());
        Services.AddSingleton<ShellActivityState>();
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
        await cut.Find(".sav__row:not(.sav__row--first) .app-segmented-control__item:first-child").ClickAsync();
        cut.WaitForAssertion(() => Assert.Equal("false", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-background")));
        Assert.Empty(cut.FindAll("[data-sav-preset][aria-checked='true']"));
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
}
