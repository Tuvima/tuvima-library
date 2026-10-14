using Bunit;
using MediaEngine.Contracts.Playback;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Components.Watch;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.Playback;
using MediaEngine.Web.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace MediaEngine.Web.Tests;

public sealed class VideoTrackDefaultsResolverTests
{
    private static readonly IReadOnlyList<PlaybackTrackDto> English = [new() { Index = 0, Language = "en", IsDefault = true }];

    private static readonly IReadOnlyList<PlaybackTrackDto> EnglishAndJapanese =
    [
        new() { Index = 0, Language = "eng", DisplayName = "English 5.1", IsDefault = true },
        new() { Index = 1, Language = "jpn", DisplayName = "Japanese" },
    ];

    // A dual-audio file whose own default is Japanese.
    private static readonly IReadOnlyList<PlaybackTrackDto> JapaneseDefaultThenEnglish =
    [
        new() { Index = 0, Language = "jpn", IsDefault = true },
        new() { Index = 1, Language = "eng" },
    ];

    private static readonly IReadOnlyList<VideoSubtitleCandidate> EnglishSubtitle = [new("manifest:0", "en", "English")];

    private static SubtitleLanguageSettingsDto Settings(string audio, string subtitles, string mode) => new()
    {
        AudioLanguage = audio,
        DefaultSubtitleLanguage = subtitles,
        ForcedSubtitlesMode = mode,
    };

    private static VideoTrackDefaults Choose(SubtitleLanguageSettingsDto? settings, IReadOnlyList<PlaybackTrackDto> audio,
        IReadOnlyList<VideoSubtitleCandidate> subtitles, bool canSwitchAudio = true) =>
        VideoPresentationResolver.ChooseTrackDefaults(settings, audio, subtitles, canSwitchAudio);

    [Fact]
    public void Auto_SingleTrackInTheSubtitleLanguage_LeavesThePlayersOwnChoice()
    {
        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), English, EnglishSubtitle);

        // No override: a forced or default track the file marks still shows.
        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
        Assert.Null(result.CaptionAttribute);
    }

    [Fact]
    public void Auto_SingleJapaneseTrack_ShowsEnglishSubtitlesEvenThoughTheProfileAudioIsEnglish()
    {
        // The normal case: one audio track, taken from the file. The profile's Audio language is not what plays.
        IReadOnlyList<PlaybackTrackDto> japaneseOnly = [new() { Index = 0, Language = "jpn", IsDefault = true }];

        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), japaneseOnly, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
        Assert.Equal("manifest:0", result.CaptionKey);
        Assert.Null(result.AudioTrackIndex);
    }

    [Theory]
    [InlineData("und")]
    [InlineData("")]
    [InlineData(null)]
    public void Auto_SingleTrackWithUnknownLanguage_IsTakenToBeTheProfileAudioLanguage(string? language)
    {
        IReadOnlyList<PlaybackTrackDto> unknown = [new() { Index = 0, Language = language, IsDefault = true }];

        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), unknown, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
        // And when the profile's audio is not the subtitle language, the unknown track still gets subtitles.
        Assert.Equal(VideoCaptionDecision.Show, Choose(Settings("Japanese", "English", PlaybackPreferenceValues.Auto), unknown, EnglishSubtitle).Captions);
    }

    [Fact]
    public void Auto_NoAudioTracksAtAll_JudgesByTheProfileAudioLanguage()
    {
        Assert.Equal(VideoCaptionDecision.Unchanged, Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), [], EnglishSubtitle).Captions);
        Assert.Equal(VideoCaptionDecision.Show, Choose(Settings("Japanese", "English", PlaybackPreferenceValues.Auto), [], EnglishSubtitle).Captions);
    }

    [Fact]
    public void Auto_AudioNotInTheSubtitleLanguage_ShowsTheSubtitleLanguageAndPicksTheProfileAudio()
    {
        var result = Choose(Settings("Japanese", "English", PlaybackPreferenceValues.Auto), EnglishAndJapanese, EnglishSubtitle);

        Assert.Equal(1, result.AudioTrackIndex);
        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
        Assert.Equal("manifest:0", result.CaptionKey);
    }

    [Fact]
    public void Auto_SwitchableAudioInTheSubtitleLanguage_ChoosesThatTrackAndLeavesCaptionsAlone()
    {
        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), JapaneseDefaultThenEnglish, EnglishSubtitle);

        Assert.Equal(1, result.AudioTrackIndex);
        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
    }

    [Fact]
    public void Auto_WhenThePlayerCannotSwitchAudio_JudgesByTheTrackThatActuallyPlays()
    {
        // Dual-audio MKV (Japanese default, English second) direct-played in a browser that reports no audio tracks:
        // the profile's English audio cannot be selected, so Japanese plays and English subtitles are needed.
        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), JapaneseDefaultThenEnglish, EnglishSubtitle, canSwitchAudio: false);

        Assert.Null(result.AudioTrackIndex);
        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
        Assert.Equal("manifest:0", result.CaptionKey);
    }

    [Fact]
    public void Auto_WhenNoTrackIsInTheAudioLanguage_JudgesByTheTrackThatWillPlay()
    {
        var french = new List<PlaybackTrackDto>
        {
            new() { Index = 0, Language = "fr", IsDefault = true },
            new() { Index = 1, Language = "de" },
        };

        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), french, EnglishSubtitle);

        Assert.Null(result.AudioTrackIndex);
        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
    }

    [Fact]
    public void Auto_MultiTrackWhoseDefaultHasUnknownLanguage_IsTakenToBeTheProfileLanguage()
    {
        var tracks = new List<PlaybackTrackDto>
        {
            new() { Index = 0, Language = "und", IsDefault = true },
            new() { Index = 1, Language = "de" },
        };

        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), tracks, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
    }

    [Fact]
    public void Auto_WithNoMatchingSubtitle_LeavesThePlayersOwnChoice()
    {
        var result = Choose(Settings("Japanese", "Spanish", PlaybackPreferenceValues.Auto), EnglishAndJapanese, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
        Assert.Null(result.CaptionAttribute);
    }

    [Fact]
    public void Always_ShowsTheSubtitleLanguageEvenWhenTheAudioMatches()
    {
        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Always), English, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
        Assert.Equal("manifest:0", result.CaptionKey);
    }

    [Fact]
    public void Always_PrefersAFullTrackOverAForcedOne()
    {
        VideoSubtitleCandidate[] tracks =
        [
            new("manifest:0", "en", "English forced", IsForced: true),
            new("manifest:1", "en", "English"),
        ];

        var result = Choose(Settings("English", "English", PlaybackPreferenceValues.Always), English, tracks);

        Assert.Equal("manifest:1", result.CaptionKey);
    }

    [Fact]
    public void Always_WithNoMatchingSubtitle_LeavesThePlayersOwnChoice()
    {
        var result = Choose(Settings("English", "French", PlaybackPreferenceValues.Always), English, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Unchanged, result.Captions);
    }

    [Fact]
    public void Never_StartsWithSubtitlesOffEvenWhenTheAudioDiffers()
    {
        var result = Choose(Settings("Japanese", "English", PlaybackPreferenceValues.Never), EnglishAndJapanese, EnglishSubtitle);

        Assert.Equal(VideoCaptionDecision.Off, result.Captions);
        Assert.Equal(1, result.AudioTrackIndex);
    }

    [Theory]
    [InlineData(PlaybackPreferenceValues.Auto)]
    [InlineData(PlaybackPreferenceValues.Always)]
    [InlineData(PlaybackPreferenceValues.Never)]
    public void ThePerTitlePreferredSubtitle_WinsOverEveryMode(string mode)
    {
        VideoSubtitleCandidate[] tracks =
        [
            new("manifest:0", "en", "English"),
            new("managed:11111111-1111-1111-1111-111111111111", "fr", null, IsPreferred: true),
        ];

        var result = Choose(Settings("English", "English", mode), English, tracks);

        Assert.Equal(VideoCaptionDecision.Show, result.Captions);
        Assert.Equal("managed:11111111-1111-1111-1111-111111111111", result.CaptionKey);
    }

    [Fact]
    public void WithoutProfileSettings_NothingChanges()
    {
        Assert.Equal(VideoTrackDefaults.Unchanged, Choose(null, EnglishAndJapanese, EnglishSubtitle));
    }

    [Theory]
    [InlineData("English", "en", null, true)]
    [InlineData("English", "eng", null, true)]
    [InlineData("English", "en-US", null, true)]
    [InlineData("English", "und", "English 5.1", true)]
    [InlineData("French", "fre", null, true)]
    [InlineData("French", "en", "English", false)]
    [InlineData("Klingon", "Klingon", null, true)]
    [InlineData("Klingon", "en", null, false)]
    [InlineData("", "en", null, false)]
    [InlineData("Norwegian", "nob", null, true)]
    [InlineData("Norwegian", "nb", null, true)]
    [InlineData("Norwegian", "nno", null, true)]
    [InlineData("Norwegian", "nn", null, true)]
    [InlineData("Norwegian", "no", null, true)]
    [InlineData("Norwegian", "sv", null, false)]
    public void LanguageMatches_ComparesNamesCodesAndLabels(string wanted, string language, string? label, bool expected)
    {
        Assert.Equal(expected, VideoPresentationResolver.LanguageMatches(wanted, language, label));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("\u00b7")]
    [InlineData(" ( / , ) ")]
    public void ALabelOfOnlySeparators_IsNotALanguageAndDoesNotThrow(string label)
    {
        Assert.False(VideoPresentationResolver.LanguageMatches("English", null, label));
        IReadOnlyList<PlaybackTrackDto> odd = [new() { Index = 0, DisplayName = label, IsDefault = true }];
        Assert.Equal(VideoCaptionDecision.Unchanged, Choose(Settings("English", "English", PlaybackPreferenceValues.Auto), odd, EnglishSubtitle).Captions);
    }
}

public sealed class VideoSubtitleDefaultsHostTests : AsyncBunitContext
{
    private readonly Guid _managedId = Guid.NewGuid();

    private sealed class Preferences(UserPlaybackSettingsDto settings) : IUserPlaybackPreferencesAccessor
    {
        public Guid? ActiveProfileId { get; } = settings.ProfileId;
        public Task<UserPlaybackSettingsDto?> GetAsync(CancellationToken ct = default) => Task.FromResult<UserPlaybackSettingsDto?>(settings);
        public void UpdateCache(UserPlaybackSettingsDto next) { }
        public void Invalidate() { }
    }

    private void Arrange(Action<SubtitleLanguageSettingsDto> configure, bool preferredManagedTrack, PlaybackManifestDto? manifest = null)
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLogging();
        Services.AddNativeUiServices();
        var settings = UserPlaybackSettingsDto.CreateDefaults(Guid.NewGuid());
        configure(settings.Subtitles);
        var preferences = new Preferences(settings);
        var api = EngineApiClientStub.Create(stub => stub.SetHandler(nameof(IEngineApiClient.GetTextTracksAsync), _ =>
            Task.FromResult<IReadOnlyList<TextTrackDto>>([new() { Id = _managedId, Kind = "Subtitle", Language = "fr", IsPreferred = preferredManagedTrack }])));
        var playback = new PlaybackSessionController(null!, api, preferences: preferences);
        playback.RestoreState(new()
        {
            Queue = [new() { WorkId = Guid.NewGuid(), AssetId = Guid.NewGuid(), Title = "Movie", MediaType = "Movie", Manifest = manifest }],
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
    }

    [Fact]
    public void TheHostSetsTheSubtitleSizeAndBackgroundFromTheProfile()
    {
        Arrange(subtitles => { subtitles.SubtitleSize = PlaybackPreferenceValues.Large; subtitles.SubtitleBackground = false; }, preferredManagedTrack: false);

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() =>
        {
            var host = cut.Find("section.video-playback-host");
            Assert.Equal("Large", host.GetAttribute("data-subtitle-size"));
            Assert.Equal("false", host.GetAttribute("data-subtitle-background"));
        });
    }

    [Fact]
    public void WithoutSavedChoices_TheHostUsesMediumWithABackground()
    {
        Arrange(_ => { }, preferredManagedTrack: false);

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() =>
        {
            var host = cut.Find("section.video-playback-host");
            Assert.Equal("Medium", host.GetAttribute("data-subtitle-size"));
            Assert.Equal("true", host.GetAttribute("data-subtitle-background"));
        });
    }

    [Fact]
    public void ShowSubtitlesOff_TellsThePlayerToStartWithCaptionsOff()
    {
        Arrange(subtitles => subtitles.ForcedSubtitlesMode = PlaybackPreferenceValues.Never, preferredManagedTrack: false);

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() => Assert.Equal("off", cut.Find("video").GetAttribute("data-playback-caption-default")));
    }

    [Fact]
    public void ThePerTitlePreferredSubtitle_BeatsTheProfileDefault()
    {
        Arrange(subtitles => subtitles.ForcedSubtitlesMode = PlaybackPreferenceValues.Never, preferredManagedTrack: true);

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() => Assert.Equal($"managed:{_managedId:D}", cut.Find("video").GetAttribute("data-playback-caption-default")));
    }

    [Fact]
    public void DualAudioFileThePlayerCannotSwitch_ShowsSubtitlesBecauseTheDefaultTrackIsNotTheProfileLanguage()
    {
        // The browser reports no audioTracks here (direct play), so the profile's English audio cannot be selected: the
        // file's Japanese default plays, and Auto must therefore ask for the English subtitles.
        var manifest = new PlaybackManifestDto
        {
            AudioTracks = [new() { Index = 0, Language = "jpn", IsDefault = true }, new() { Index = 1, Language = "eng" }],
            SubtitleTracks = [new() { Index = 0, Language = "en", DeliveryUrl = "/subs/en.vtt" }],
        };
        Arrange(subtitles => { subtitles.AudioLanguage = "English"; subtitles.DefaultSubtitleLanguage = "English"; }, preferredManagedTrack: false, manifest);

        var cut = Render<VideoPlaybackHost>();

        cut.WaitForAssertion(() => Assert.Equal("manifest:0", cut.Find("video").GetAttribute("data-playback-caption-default")));
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "listenPlayback.selectAudioTrack");
    }

    [Fact]
    public void SingleTrackFileInTheSubtitleLanguage_AddsNoCaptionOverride()
    {
        var manifest = new PlaybackManifestDto
        {
            AudioTracks = [new() { Index = 0, Language = "eng", IsDefault = true }],
            SubtitleTracks = [new() { Index = 0, Language = "en", IsForced = true, IsDefault = true, DeliveryUrl = "/subs/en.vtt" }],
        };
        Arrange(subtitles => subtitles.SubtitleSize = PlaybackPreferenceValues.Small, preferredManagedTrack: false, manifest);

        var cut = Render<VideoPlaybackHost>();

        // Waiting for the saved size proves the profile has loaded before the absence of an override is checked.
        cut.WaitForAssertion(() => Assert.Equal("Small", cut.Find("section.video-playback-host").GetAttribute("data-subtitle-size")));
        Assert.False(cut.Find("video").HasAttribute("data-playback-caption-default"));
    }

    [Fact]
    public void TheCueRulesLiveInAppCssAndReadTheVariablesTheHostAttributesSet()
    {
        var root = RepoRoot();
        var css = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.css"));
        var scoped = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor.css"));
        var script = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.js"));

        Assert.Contains(".video-playback-host__media::cue { color: white; background: var(--tl-subtitle-bg); font-size: var(--tl-subtitle-size);", css, StringComparison.Ordinal);
        Assert.DoesNotContain("::cue", scoped, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host[data-subtitle-size=\"Small\"] { --tl-subtitle-size: max(14px, 2.2vh); }", css, StringComparison.Ordinal);
        Assert.Contains("--tl-subtitle-size: max(14px, 2.8vh)", css, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host[data-subtitle-size=\"Large\"] { --tl-subtitle-size: max(14px, 3.6vh); }", css, StringComparison.Ordinal);
        Assert.Contains(".video-playback-host[data-subtitle-background=\"false\"] { --tl-subtitle-bg: transparent;", css, StringComparison.Ordinal);
        Assert.Contains("--tl-subtitle-bg: rgb(0 0 0 / 60%)", css, StringComparison.Ordinal);
        // The profile default sits behind the title's own preferred subtitle and ahead of the browser's default track.
        Assert.Contains("element.dataset.playbackCaptionDefault", script, StringComparison.Ordinal);
        Assert.Contains("'data-playback-caption-default'", script, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
    }
}
