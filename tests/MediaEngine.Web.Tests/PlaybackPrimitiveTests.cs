using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Formatting;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackPrimitiveTests
{
    [Theory]
    [InlineData(1d, "1.0x")]
    [InlineData(1.25d, "1.25x")]
    [InlineData(1.75d, "1.75x")]
    [InlineData(1.2345d, "1.2345x")]
    public void SpeedToolValuePreservesQuarterStepPrecision(double rate, string expected)
    {
        Assert.Equal(expected, DisplayFormat.FormatSpeedControl(rate));
    }

    [Theory]
    [InlineData("Music", PlaybackExperience.Music)]
    [InlineData("Audio", PlaybackExperience.Music)]
    [InlineData("Audiobooks", PlaybackExperience.Audiobook)]
    [InlineData("M4B", PlaybackExperience.Audiobook)]
    [InlineData("Movies", PlaybackExperience.Video)]
    [InlineData("TV", PlaybackExperience.Video)]
    [InlineData("MKV", PlaybackExperience.Video)]
    [InlineData("Unknown", PlaybackExperience.Music)]
    public void MediaKindClassifier_MapsKnownStringsToPlaybackExperience(string mediaType, PlaybackExperience expected)
    {
        Assert.Equal(expected, MediaKindClassifier.Classify(mediaType));
    }

    [Fact]
    public void PlaybackControlCatalog_BuildsCrossMediaControlMatrix()
    {
        var music = PlaybackControlCatalog.Build(
            PlaybackExperience.Music,
            PlaybackControlSurface.Popup,
            new PlaybackControlState(HasQueue: true, HasLyrics: true, CanPrevious: true, CanNext: true));
        var audiobook = PlaybackControlCatalog.Build(
            PlaybackExperience.Audiobook,
            PlaybackControlSurface.Popup,
            new PlaybackControlState(PlaybackRate: 1.5d, HasChapters: true, IsSleepTimerActive: true, SleepTimerValueText: "30m", CanPrevious: true, CanNext: true));
        var video = PlaybackControlCatalog.Build(
            PlaybackExperience.Video,
            PlaybackControlSurface.PictureInPicture,
            new PlaybackControlState(PlaybackRate: 1.25d, HasChapters: true, HasQueue: true, IsTvEpisode: true, HasCaptions: true, HasAudioTracks: true, HasQualityOptions: true, CanPrevious: true, CanNext: true, CanPictureInPicture: true, CanFullscreen: true, SkipBackSeconds: 30, SkipForwardSeconds: 10));

        AssertCommonControls(music);
        AssertCommonControls(audiobook);
        AssertContainsKeys(video, PlaybackControlKey.PlayPause, PlaybackControlKey.Timeline, PlaybackControlKey.SkipBack, PlaybackControlKey.SkipForward, PlaybackControlKey.Resume);
        Assert.DoesNotContain(video, control => control.Key is PlaybackControlKey.Queue or PlaybackControlKey.History or PlaybackControlKey.Captions or PlaybackControlKey.Volume or PlaybackControlKey.Quality or PlaybackControlKey.Fullscreen);
        Assert.Contains(video, control => control.Key == PlaybackControlKey.SkipBack && control.ValueText == "30" && control.AriaLabel == "Skip back 30 seconds");
        Assert.Contains(video, control => control.Key == PlaybackControlKey.SkipForward && control.ValueText == "10" && control.AriaLabel == "Skip forward 10 seconds");

        AssertContainsKeys(music, PlaybackControlKey.Queue, PlaybackControlKey.History, PlaybackControlKey.Lyrics, PlaybackControlKey.Shuffle, PlaybackControlKey.Repeat);
        Assert.DoesNotContain(music, control => control.Key == PlaybackControlKey.SleepTimer);

        AssertContainsKeys(audiobook, PlaybackControlKey.SkipBack, PlaybackControlKey.SkipForward, PlaybackControlKey.Speed, PlaybackControlKey.Chapters, PlaybackControlKey.History, PlaybackControlKey.Bookmarks, PlaybackControlKey.SleepTimer);
        Assert.DoesNotContain(audiobook, control => control.Key == PlaybackControlKey.Queue);
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.Speed && control.ValueText == "1.5x");
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.SleepTimer && control.IsActive && control.BadgeText == "30m");
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.Chapters && !control.IsDisabled);

        var sleepChoice = PlaybackControlCatalog.BuildToolStrip(
            PlaybackExperience.Audiobook,
            PlaybackControlSurface.Bottom,
            new PlaybackControlState(ActiveSheet: "chapters", IsSleepTimerActive: false, SleepTimerValueText: null))
            .Single(control => control.Key == PlaybackControlKey.SleepTimer);
        Assert.Null(sleepChoice.Sheet);
        Assert.False(sleepChoice.IsSelected);
        Assert.False(sleepChoice.IsActive);

        var fullscreenVideo = PlaybackControlCatalog.Build(PlaybackExperience.Video, PlaybackControlSurface.Fullscreen,
            new PlaybackControlState(HasChapters: true, HasQueue: true, IsTvEpisode: true, HasCaptions: true, HasAudioTracks: true, HasQualityOptions: true, CanFullscreen: true, CanPictureInPicture: true));
        AssertContainsKeys(fullscreenVideo, PlaybackControlKey.Queue, PlaybackControlKey.Speed, PlaybackControlKey.Chapters, PlaybackControlKey.Captions, PlaybackControlKey.AudioTrack, PlaybackControlKey.Quality, PlaybackControlKey.Fullscreen, PlaybackControlKey.PictureInPicture);
        Assert.DoesNotContain(fullscreenVideo, control => control.Key == PlaybackControlKey.History);
        Assert.DoesNotContain(video, control => control.Key is PlaybackControlKey.SkipIntro or PlaybackControlKey.SkipCredits);

        var movie = PlaybackControlCatalog.Build(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, new PlaybackControlState());
        Assert.DoesNotContain(movie, control => control.Key is PlaybackControlKey.Queue or PlaybackControlKey.Captions or PlaybackControlKey.AudioTrack or PlaybackControlKey.Quality or PlaybackControlKey.PictureInPicture);
    }

    [Fact]
    public void AudiobookDockTools_AreDirectlyOrderedAndKeepChapterAndSleepStateTruthful()
    {
        var controls = PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState(
            HasChapters: true,
            PlaybackRate: 1.25d,
            IsSleepTimerActive: true,
            SleepTimerValueText: "12m",
            HistoryCount: 2));

        Assert.Equal(
            new[] { PlaybackControlKey.Chapters, PlaybackControlKey.Bookmarks, PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer, PlaybackControlKey.History },
            controls.Select(control => control.Key));
        Assert.False(controls.Single(control => control.Key == PlaybackControlKey.Chapters).IsDisabled);
        var sleep = controls.Single(control => control.Key == PlaybackControlKey.SleepTimer);
        Assert.Equal("12m", sleep.BadgeText);
        Assert.Contains("12m remaining", sleep.AriaLabel, StringComparison.Ordinal);

        var noChapters = PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState());
        var unavailable = noChapters.Single(control => control.Key == PlaybackControlKey.Chapters);
        Assert.True(unavailable.IsDisabled);
        Assert.Contains("no timed chapters", unavailable.AriaLabel, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlaybackStateMachine_TracksTransportPhases()
    {
        var machine = new PlaybackStateMachine();

        Assert.Equal(PlaybackPhase.Playing, machine.Transition(PlaybackCommandKind.TogglePlay));
        machine.SetTransportState(isPlaying: false, needsUserGesture: false, error: null);
        Assert.Equal(PlaybackPhase.Paused, machine.Phase);
        machine.SetTransportState(isPlaying: null, needsUserGesture: true, error: null);
        Assert.Equal(PlaybackPhase.NeedsGesture, machine.Phase);
        machine.SetTransportState(isPlaying: null, needsUserGesture: false, error: "Failed");
        Assert.Equal(PlaybackPhase.Error, machine.Phase);
        machine.SetEnded();
        Assert.Equal(PlaybackPhase.Ended, machine.Phase);
    }

    [Fact]
    public void PlaybackClientContext_NormalizesWebDefaultsWithoutOldDashboardDeviceId()
    {
        var context = new PlaybackClientContext("", "", "", "", "").Normalize();

        Assert.Equal("web", context.DeviceId);
        Assert.Equal("web", context.Client);
        Assert.DoesNotContain("web-dashboard", context.DeviceId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ListenPlaybackClientSettings_NormalizeClampsUnsafeValues()
    {
        var settings = new ListenPlaybackClientSettings
        {
            PopupWidth = 10,
            PopupHeight = 10000,
            AudioObserverIntervalMilliseconds = 1,
            SeekToleranceSeconds = 100,
            VolumeStep = 2,
            HeartbeatIntervalSeconds = 0,
            PendingTransportCommandLimit = 999,
            DefaultVolume = 2,
        }.Normalize();

        Assert.Equal(280, settings.PopupWidth);
        Assert.Equal(1400, settings.PopupHeight);
        Assert.Equal(100, settings.AudioObserverIntervalMilliseconds);
        Assert.Equal(10, settings.SeekToleranceSeconds);
        Assert.Equal(0.5d, settings.VolumeStep);
        Assert.Equal(1, settings.HeartbeatIntervalSeconds);
        Assert.Equal(256, settings.PendingTransportCommandLimit);
        Assert.Equal(1d, settings.DefaultVolume);
    }

    [Fact]
    public void PlaybackJavaScript_UsesV2StorageKeysAndNoLegacyStateFallback()
    {
        var script = File.ReadAllText(Path.Combine(FindRepoRoot(), "src/MediaEngine.Web/wwwroot/app.js"));

        Assert.Contains("tuvima.playback.v2.state", script, StringComparison.Ordinal);
        Assert.Contains("tuvima.playback.v2.command", script, StringComparison.Ordinal);
        Assert.DoesNotContain("tuvima.playback.v2.device-id", script, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-playback-state", script, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-playback-command", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ListenSurfaces_UseSharedTransportControls()
    {
        var root = FindRepoRoot();
        var bar = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));
        var sidebar = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/PlaybackContextPanel.razor"));
        var popup = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor"));
        var full = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/PlaybackFullPlayer.razor"));
        var strip = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackControlStrip.razor"));
        var speed = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor"));
        var sleep = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor"));
        var bookmark = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/AudiobookBookmarkDialog.razor"));

        Assert.Contains("<PlaybackControlStrip", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackFullPlayer", popup, StringComparison.Ordinal);
        Assert.Contains("SpeedChanged=\"SetAudiobookSpeedAsync\"", bar, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackCommandActions.SetSpeed", full, StringComparison.Ordinal);
        Assert.Contains("SleepTimerState=\"@Playback.SleepTimerState\"", bar, StringComparison.Ordinal);
        Assert.Contains("SleepTimerAvailability=\"@Playback.SleepTimerAvailability\"", bar, StringComparison.Ordinal);
        Assert.Contains("SleepTimerChanged=\"SetAudiobookSleepTimerSelectionAsync\"", bar, StringComparison.Ordinal);
        Assert.Contains("SleepTimerState=\"Snapshot.SleepTimerState\"", full, StringComparison.Ordinal);
        Assert.Contains("SleepTimerChanged=", full, StringComparison.Ordinal);
        Assert.Contains("Playback.SetAudiobookSleepTimerAsync", bar, StringComparison.Ordinal);
        Assert.Contains("ListenPlaybackCommandActions.SetSleepTimer", full, StringComparison.Ordinal);
        Assert.Contains("control.Key == PlaybackControlKey.Speed", strip, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSpeedControl", strip, StringComparison.Ordinal);
        Assert.Contains("control.Key == PlaybackControlKey.SleepTimer", strip, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSleepTimerControl", strip, StringComparison.Ordinal);
        Assert.Contains("PlaybackRateOptions.BuildChoices", speed, StringComparison.Ordinal);
        Assert.DoesNotContain("<PlaybackRangeSlider", speed, StringComparison.Ordinal);
        Assert.Contains("Availability.CanEndCurrent", sleep, StringComparison.Ordinal);
        Assert.Contains("Availability.CanEndNext", sleep, StringComparison.Ordinal);
        Assert.Contains("SelectionChanged.InvokeAsync(selection)", sleep, StringComparison.Ordinal);
        Assert.DoesNotContain("<PlaybackRangeSlider", sleep, StringComparison.Ordinal);
        Assert.Contains("<AudiobookBookmarkDialog", bar, StringComparison.Ordinal);
        Assert.Contains("<AudiobookBookmarkDialog", popup, StringComparison.Ordinal);
        Assert.Contains("Actions.RequestDeleteAsync(context, bookmarkId, authorizedAssetIds)", bookmark, StringComparison.Ordinal);
        Assert.Contains("Actions.ConfirmDeleteAsync(context, authorizedAssetIds)", bookmark, StringComparison.Ordinal);
        Assert.Contains("OpenDockPanelAsync(control.Sheet)", bar, StringComparison.Ordinal);
        Assert.Contains("PlaybackContextRow Variant=\"chapter\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("PlaybackContextRow Variant=\"history\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Subtitle=\"@AudiobookHistorySubtitle(entry)\"", sidebar, StringComparison.Ordinal);
        Assert.Contains("Elapsed session \u00b7 {FormatTime(interval)}", sidebar, StringComparison.Ordinal);
        Assert.DoesNotContain("add-audiobook-bookmark", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void PhoneAudiobookMiniPlayer_UsesSinglePlayColumnAndExpandedPlayerAcceptsInput()
    {
        var root = FindRepoRoot();
        var transport = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor"));
        var transportStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor.css"));
        var barStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css"));

        Assert.Contains("listen-player__transport-buttons--audiobook", transport, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", transportStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 44px !important;", transportStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns:minmax(0,1fr) 44px 44px", barStyles, StringComparison.Ordinal);
        Assert.Contains("pointer-events:auto;", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/PlaybackFullPlayer.razor.css")), StringComparison.Ordinal);
    }

    [Fact]
    public void TabletAudioFrameKeepsTheDockBelowContentAndAllDockTransportTargetsAtLeast44Pixels()
    {
        var root = FindRepoRoot();
        var frame = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Shared/MainLayout.razor.css"));
        var dockRule = frame.Split(".playback-app-frame__audio-dock {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("grid-row: 3;", dockRule);
        var global = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.css"));
        var audioHideRule = global[..global.IndexOf(".playback-app-frame:has(.listen-player-shell--expanded) .layout-shell__appbar", StringComparison.Ordinal)];
        Assert.EndsWith("@media (max-width: 720px) {", audioHideRule.TrimEnd());
        Assert.Contains("@media (max-width: 840px)", frame); // Global navigation retains its own breakpoint.
        var transport = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor.css"));
        var barRule = transport.Split(".listen-transport--bar {", StringSplitOptions.None)[1].Split('}')[0];
        Assert.Contains("--listen-transport-secondary-size: 44px;", barRule);
        Assert.Contains("--listen-transport-primary-size: 60px;", barRule);
    }

    [Fact]
    public void PlaybackControlCatalogSeparatesOrderedPrimaryAndSecondaryTools()
    {
        var musicState = new PlaybackControlState(HasQueue: true, HasLyrics: true, HistoryCount: 3);
        var musicPrimary = PlaybackControlCatalog.BuildPrimaryToolStrip(PlaybackExperience.Music, PlaybackControlSurface.Phone, musicState);
        var musicSecondary = PlaybackControlCatalog.BuildSecondaryToolStrip(PlaybackExperience.Music, PlaybackControlSurface.Phone, musicState);

        Assert.Equal(new[] { PlaybackControlKey.Queue, PlaybackControlKey.Lyrics, PlaybackControlKey.Shuffle, PlaybackControlKey.Repeat }, musicPrimary.Select(control => control.Key));
        Assert.Equal(new[] { PlaybackControlKey.History }, musicSecondary.Select(control => control.Key));

        var videoState = new PlaybackControlState(HasChapters: true, HasQueue: true, IsTvEpisode: true, HasCaptions: true, HasAudioTracks: true, HasQualityOptions: true, CanFullscreen: true);
        var videoPrimary = PlaybackControlCatalog.BuildPrimaryToolStrip(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, videoState);
        var videoUtilities = PlaybackControlCatalog.BuildUtilityControls(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, videoState);
        var videoSecondary = PlaybackControlCatalog.BuildSecondaryToolStrip(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, videoState);
        Assert.Equal(new[] { PlaybackControlKey.Captions, PlaybackControlKey.AudioTrack, PlaybackControlKey.Speed, PlaybackControlKey.Queue }, videoPrimary.Select(control => control.Key));
        Assert.Equal(new[] { PlaybackControlKey.Chapters, PlaybackControlKey.Quality }, videoSecondary.Select(control => control.Key));
        Assert.Equal(PlaybackControlKey.Fullscreen, videoUtilities[^1].Key);
        Assert.DoesNotContain(videoPrimary, control => control.Key == PlaybackControlKey.Fullscreen);
    }

    [Fact]
    public void ContextRowsAndToolSheetsExposeAccessibleStateAndModalFocusContract()
    {
        var root = FindRepoRoot();
        var contextRow = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackContextRow.razor"));
        var contextRowStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackContextRow.razor.css"));
        var activityMark = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackActivityMark.razor"));
        var toolSheet = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor"));
        var focusModule = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/js/playback-tool-sheet.js"));

        Assert.Contains("aria-current=\"@(IsCurrent ? \"true\" : null)\"", contextRow, StringComparison.Ordinal);
        Assert.Contains("IsPlaying", contextRow, StringComparison.Ordinal);
        Assert.Contains("font-size:14px", contextRowStyles, StringComparison.Ordinal);
        Assert.Contains("font-weight:500", contextRowStyles, StringComparison.Ordinal);
        Assert.Contains("font-size:12px", contextRowStyles, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"@(IsPlaying ? \"Now playing\"", activityMark, StringComparison.Ordinal);
        Assert.Contains("role=\"@(Modal ? \"dialog\" : \"region\")\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("aria-modal=\"@(Modal ? \"true\" : null)\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("attachModal", focusModule, StringComparison.Ordinal);
        Assert.Contains("restoreFocus", focusModule, StringComparison.Ordinal);
        Assert.Contains("event.key === 'Escape'", focusModule, StringComparison.Ordinal);
    }

    [Fact]
    public void DockedPlayers_UseCenteredLargeControlsAndCoverOnlyThumbnails()
    {
        var root = FindRepoRoot();
        var listenBar = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));
        var listenStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css"));
        var utilityGlyph = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackUtilityGlyph.razor"));
        var utilityGlyphStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackUtilityGlyph.razor.css"));
        var videoHost = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var videoStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor.css"));
        var watchPlayer = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/WatchPlayerPage.razor"));

        Assert.Contains("PlaybackUtilityGlyphKind.Expand", listenBar, StringComparison.Ordinal);
        Assert.Contains("<svg", utilityGlyph, StringComparison.Ordinal);
        Assert.Contains("width: 22px;", utilityGlyphStyles, StringComparison.Ordinal);
        Assert.Contains("height: 22px;", utilityGlyphStyles, StringComparison.Ordinal);
        Assert.Contains("Open Now Playing", listenBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Playback device unavailable", listenBar, StringComparison.Ordinal);
        Assert.Contains("place-items:center;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit:contain;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("font-size:22px", videoStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-icon-size:38px", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackVideoChrome.razor.css")), StringComparison.Ordinal);
        Assert.Contains("object-fit:contain", videoStyles, StringComparison.Ordinal);
        Assert.True(
            listenBar.IndexOf("<div class=\"listen-player__progress\"", StringComparison.Ordinal)
            < listenBar.IndexOf("<div class=\"listen-player__actions\"", StringComparison.Ordinal));
        Assert.DoesNotContain("listen-player__chapter-context", listenBar, StringComparison.Ordinal);
        Assert.Contains("PlaybackRangeSlider", listenBar, StringComparison.Ordinal);
        Assert.Contains("playback-full__book-progress", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/PlaybackFullPlayer.razor")), StringComparison.Ordinal);
        Assert.Contains("video-playback-restore", videoHost, StringComparison.Ordinal);
        Assert.DoesNotContain("<section class=\"video-playback-dock\"", videoHost, StringComparison.Ordinal);
        Assert.Contains(".listen-player__progress {", listenStyles, StringComparison.Ordinal);
        Assert.Contains("border-radius:0;", listenStyles, StringComparison.Ordinal);
        Assert.Contains(".video-playback-restore {", videoStyles, StringComparison.Ordinal);
        Assert.Contains("GetDetailPageAsync", watchPlayer, StringComparison.Ordinal);
        Assert.Contains("OwnedEpisodeQueuePlanner.NextPlayableCandidates", watchPlayer, StringComparison.Ordinal);
        Assert.Contains("CoverUrl = detail.CoverUrl,", watchPlayer, StringComparison.Ordinal);
        Assert.DoesNotContain("CoverUrl = _detail.BackgroundUrl", watchPlayer, StringComparison.Ordinal);
    }

    [Fact]
    public void AudioDockResponsiveContractKeepsPhoneSeekOutsideTheSingleControlRow()
    {
        var root = FindRepoRoot();
        var globalStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/app.css"));
        var styles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css"));
        var dockStyles = styles;
        var phoneStyles = dockStyles[dockStyles.IndexOf("@media (max-width:720px)", StringComparison.Ordinal)..];

        Assert.DoesNotContain("min-height: 116px !important", globalStyles, StringComparison.Ordinal);
        Assert.Contains(".listen-player__utility-menu { display:flex; flex-direction:column;", dockStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-player-shell ::deep .listen-player__utility-menu", dockStyles, StringComparison.Ordinal);
        Assert.Contains(".listen-player__progress { position:absolute; inset:0 0 auto;", dockStyles, StringComparison.Ordinal);
        Assert.Contains("height:calc(72px + var(--tl-safe-area-bottom))", phoneStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows:1fr", phoneStyles, StringComparison.Ordinal);
        Assert.Contains(".listen-player__actions > :not(.listen-player__expand) { display:none; }", phoneStyles, StringComparison.Ordinal);
        Assert.Contains(".listen-transport--bar > :not(.playback-primary-button-shell) { display:none; }", phoneStyles, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSurfaces_UseNeutralControlsAndPurpleInteractionTokens()
    {
        var root = FindRepoRoot();
        var tokens = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/wwwroot/tuvima.tokens.css"));
        var toolSheet = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor"));
        var toolSheetCss = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor.css"));
        var listenBar = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));
        var videoHost = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var playbackStyles = Directory
            .EnumerateFiles(Path.Combine(root, "src/MediaEngine.Web/Components"), "*Playback*.razor.css", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Append(File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css")))
            .Append(File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css")))
            .ToList();

        Assert.Contains("--playback-accent: var(--tl-accent-primary);", tokens, StringComparison.Ordinal);
        Assert.Contains("--playback-tool-width: 400px;", tokens, StringComparison.Ordinal);
        Assert.Contains("role=\"@(Modal ? \"dialog\" : \"region\")\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("Icon=\"@Icon\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("aria-modal=\"@(Modal ? \"true\" : null)\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", toolSheetCss, StringComparison.Ordinal);
        Assert.Contains("max-height: 82svh;", toolSheetCss, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomPanelTitle", listenBar, StringComparison.Ordinal);
        Assert.Contains("SleepTimerState=\"@Playback.SleepTimerState\"", listenBar, StringComparison.Ordinal);
        Assert.Contains("Playback.SetAudiobookSleepTimerAsync", listenBar, StringComparison.Ordinal);
        Assert.Contains("<ListenDockTool", listenBar, StringComparison.Ordinal);
        Assert.Contains("Title=\"More playback controls\"", listenBar, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-player-panel-backdrop", listenBar, StringComparison.Ordinal);
        Assert.Contains("OpenDockPanelAsync(\"queue\")", listenBar, StringComparison.Ordinal);
        Assert.Contains("OpenDockPanelAsync(\"lyrics\")", listenBar, StringComparison.Ordinal);
        Assert.Contains("OpenDockPanelAsync(\"history\")", listenBar, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlCatalog.BuildToolStrip", File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/PlaybackFullPlayer.razor")), StringComparison.Ordinal);
        Assert.Contains("<PlaybackRelativeSkipButton", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPrimaryButton", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSpeedControl", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPopover", videoHost, StringComparison.Ordinal);

        foreach (var css in playbackStyles)
        {
            Assert.DoesNotContain("#9a62ff", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("#a974ff", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("#c084fc", css, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rgba(154, 98, 255", css, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static ListenQueueItem Item(string title) => new()
    {
        WorkId = Guid.NewGuid(),
        MediaType = "Music",
        Title = title,
    };

    private static void AssertCommonControls(IReadOnlyList<PlaybackControlDefinition> controls)
    {
        AssertContainsKeys(
            controls,
            PlaybackControlKey.PlayPause,
            PlaybackControlKey.Timeline,
            PlaybackControlKey.PreviousItem,
            PlaybackControlKey.NextItem,
            PlaybackControlKey.Volume,
            PlaybackControlKey.Mute);
    }

    private static void AssertContainsKeys(IReadOnlyList<PlaybackControlDefinition> controls, params PlaybackControlKey[] keys)
    {
        foreach (var key in keys)
        {
            Assert.Contains(controls, control => control.Key == key);
        }
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MediaEngine.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find repository root.");
    }
}
