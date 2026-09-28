using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Playback;

namespace MediaEngine.Web.Tests;

public sealed class PlaybackPrimitiveTests
{
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
            new PlaybackControlState(PlaybackRate: 1.25d, HasChapters: true, HasQueue: true, IsTvEpisode: true, HasCaptions: true, HasAudioTracks: true, HasQualityOptions: true, CanPrevious: true, CanNext: true, CanPictureInPicture: true, CanFullscreen: true));

        AssertCommonControls(music);
        AssertCommonControls(audiobook);
        AssertCommonControls(video);

        AssertContainsKeys(music, PlaybackControlKey.Queue, PlaybackControlKey.History, PlaybackControlKey.Lyrics, PlaybackControlKey.Shuffle, PlaybackControlKey.Repeat);
        Assert.DoesNotContain(music, control => control.Key == PlaybackControlKey.SleepTimer);

        AssertContainsKeys(audiobook, PlaybackControlKey.SkipBack, PlaybackControlKey.SkipForward, PlaybackControlKey.Speed, PlaybackControlKey.Chapters, PlaybackControlKey.History, PlaybackControlKey.Bookmarks, PlaybackControlKey.SleepTimer);
        Assert.DoesNotContain(audiobook, control => control.Key == PlaybackControlKey.Queue);
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.Speed && control.ValueText == "1.5x");
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.SleepTimer && control.IsActive && control.BadgeText == "30m");
        Assert.Contains(audiobook, control => control.Key == PlaybackControlKey.Chapters && !control.IsDisabled);

        var selectedSleepWithoutTimer = PlaybackControlCatalog.BuildToolStrip(
            PlaybackExperience.Audiobook,
            PlaybackControlSurface.Bottom,
            new PlaybackControlState(ActiveSheet: "sleep", IsSleepTimerActive: false, SleepTimerValueText: null))
            .Single(control => control.Key == PlaybackControlKey.SleepTimer);
        Assert.True(selectedSleepWithoutTimer.IsSelected);
        Assert.False(selectedSleepWithoutTimer.IsActive);

        AssertContainsKeys(video, PlaybackControlKey.SkipBack, PlaybackControlKey.SkipForward, PlaybackControlKey.Queue, PlaybackControlKey.History, PlaybackControlKey.Speed, PlaybackControlKey.Chapters, PlaybackControlKey.Captions, PlaybackControlKey.AudioTrack, PlaybackControlKey.Quality, PlaybackControlKey.Fullscreen, PlaybackControlKey.PictureInPicture, PlaybackControlKey.Resume);
        Assert.DoesNotContain(video, control => control.Key is PlaybackControlKey.SkipIntro or PlaybackControlKey.SkipCredits);

        var movie = PlaybackControlCatalog.Build(PlaybackExperience.Video, PlaybackControlSurface.PrimaryVideo, new PlaybackControlState());
        Assert.DoesNotContain(movie, control => control.Key is PlaybackControlKey.Queue or PlaybackControlKey.Captions or PlaybackControlKey.AudioTrack or PlaybackControlKey.Quality or PlaybackControlKey.PictureInPicture);
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
        var popup = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor"));
        var shared = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor"));
        var sharedStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenTransportControls.razor.css"));
        var skip = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackRelativeSkipButton.razor"));
        var skipStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackRelativeSkipButton.razor.css"));
        var primary = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPrimaryButton.razor"));
        var primaryStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPrimaryButton.razor.css"));
        var timeline = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackTimelineMetaRow.razor"));
        var timelineStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackTimelineMetaRow.razor.css"));
        var controlCatalog = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackControlCatalog.cs"));
        var controlStrip = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackControlStrip.razor"));
        var controlStripStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackControlStrip.razor.css"));
        var iconButton = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackIconButton.razor"));
        var iconButtonStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackIconButton.razor.css"));
        var toolSheet = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor"));
        var toolSheetStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackToolSheet.razor.css"));
        var sheetHandle = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetHandleButton.razor"));
        var sheetHandleStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetHandleButton.razor.css"));
        var popoutShell = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPopoutShell.razor"));
        var miniPlayer = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackMiniPlayer.razor"));
        var sheetList = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetList.razor"));
        var sheetListStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetList.razor.css"));
        var sheetRow = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetRow.razor"));
        var sheetRowStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSheetRow.razor.css"));
        var positionRow = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPositionRow.razor"));
        var positionRowStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPositionRow.razor.css"));
        var positionList = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPositionList.razor"));
        var positionListStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackPositionList.razor.css"));
        var speedControl = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor"));
        var speedControlStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSpeedControl.razor.css"));
        var sleepTimerControl = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor"));
        var sleepTimerControlStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackSleepTimerControl.razor.css"));
        var rangeSlider = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor"));
        var rangeSliderStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Shared/PlaybackRangeSlider.razor.css"));
        var barStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css"));
        var popupStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/ListenPlayerPopupPage.razor.css"));
        var playbackConfig = File.ReadAllText(Path.Combine(root, "config/ui/playback-client.json"));
        var playbackSettings = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Settings/PlaybackTab.razor"));

        Assert.Contains("<ListenTransportControls", bar, StringComparison.Ordinal);
        Assert.Contains("<ListenTransportControls", popup, StringComparison.Ordinal);
        Assert.Contains("listen-transport--popup-music", shared, StringComparison.Ordinal);
        Assert.Contains("listen-popup__volume-control", popup, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.VolumeDown", popup, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.VolumeUp", popup, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-size: 82px;", popupStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPrimaryButton", shared, StringComparison.Ordinal);
        Assert.DoesNotContain("MudIcon Icon=\"@(IsPlaying", shared, StringComparison.Ordinal);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", primary, StringComparison.Ordinal);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", primary, StringComparison.Ordinal);
        Assert.Contains("playback-primary-button-shell--compact", primaryStyles, StringComparison.Ordinal);
        Assert.Contains("playback-primary-button-shell--large", primaryStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-icon-size: 34px;", primaryStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-size: 58px;", primaryStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-primary-size: 64px;", primaryStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("--playback-primary-size: 74px;", primaryStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("--playback-primary-size: 96px;", primaryStyles, StringComparison.Ordinal);
        Assert.Contains("\"popup\" => \"large\"", shared, StringComparison.Ordinal);
        Assert.Contains("\"panel\" => \"standard\"", shared, StringComparison.Ordinal);
        Assert.Contains("_ => \"compact\"", shared, StringComparison.Ordinal);
        Assert.Contains("listen-transport__secondary-icon", shared, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-secondary-size: 52px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-secondary-icon-size: 30px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-secondary-size: 50px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-primary-size: 58px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-secondary-size: 54px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--listen-transport-primary-size: 64px;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: var(--listen-transport-secondary-size) var(--listen-transport-secondary-size) var(--listen-transport-primary-size) var(--listen-transport-secondary-size) var(--listen-transport-secondary-size) !important;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("width: var(--listen-transport-secondary-size) !important;", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: var(--listen-transport-secondary-size);", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 52px 52px 64px 52px 52px;", barStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 54px 54px 64px 54px 54px;", popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: 38px 54px 52px 54px 38px", barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: 72px 64px 104px 64px 72px", popupStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackTimelineMetaRow", popup, StringComparison.Ordinal);
        Assert.Contains("playback-timeline-meta-row", timeline, StringComparison.Ordinal);
        Assert.Contains("color: var(--playback-text);", timelineStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackControlStrip", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackControlStrip", bar, StringComparison.Ordinal);
        Assert.Contains("Class=\"listen-player__audiobook-actions\"", bar, StringComparison.Ordinal);
        Assert.Contains("ActiveSheet: Playback.IsPanelOpen ? _activeAudiobookPanelTool : null", bar, StringComparison.Ordinal);
        Assert.Contains("SleepTimerValueText: BottomSleepTimerValueText", bar, StringComparison.Ordinal);
        Assert.Contains("Playback.TogglePanel();", bar, StringComparison.Ordinal);
        Assert.Contains("ShortSleepTimerLabel", bar, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(220px, 0.9fr) minmax(260px, 1.2fr) minmax(560px, 1.4fr);", barStyles, StringComparison.Ordinal);
        Assert.Contains(".listen-player__actions ::deep .playback-control-strip.listen-player__audiobook-actions", barStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(5, minmax(52px, 1fr)) !important;", barStyles, StringComparison.Ordinal);
        Assert.Contains("width: clamp(300px, 24vw, 350px) !important;", barStyles, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 1240px)", barStyles, StringComparison.Ordinal);
        Assert.Contains("grid-column: 1 / -1;", barStyles, StringComparison.Ordinal);
        Assert.Contains("width: 100%;", barStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: 24px 10px 12px;", barStyles, StringComparison.Ordinal);
        Assert.Contains("min-height: 52px;", barStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackToolSheet", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPopoutShell", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackMiniPlayer", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("<PlaybackValueToolButton", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSheetList", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSheetRow", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPositionRow", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPositionRow", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPositionList", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPositionList", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSpeedControl", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSpeedControl", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSleepTimerControl", popup, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSleepTimerControl", bar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackRangeSlider", speedControl, StringComparison.Ordinal);
        Assert.Contains("<PlaybackRangeSlider", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("InputStep=\"@Step\"", speedControl, StringComparison.Ordinal);
        Assert.Contains("TickStep=\"0.5\"", speedControl, StringComparison.Ordinal);
        Assert.DoesNotContain("TickStep=\"0.1\"", speedControl, StringComparison.Ordinal);
        Assert.Contains("TickFormatter=\"FormatSpeedTick\"", speedControl, StringComparison.Ordinal);
        Assert.Contains("FormatSpeedTick", speedControl, StringComparison.Ordinal);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", speedControl, StringComparison.Ordinal);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", speedControl, StringComparison.Ordinal);
        Assert.Contains("type=\"range\"", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("@oninput=\"HandleInputAsync\"", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("@onchange=\"HandleInputAsync\"", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("_interactiveValue = snapped", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("ResolvedInputStep", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("ResolvedInputStepText", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("SnapValueToStep", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("TickValues", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("TickFormatter", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("SelectTickAsync", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("TickLabelClass", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("TickButtonAriaLabel", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("<AppNativeButton Type=\"button\"", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__visual", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__track", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__fill", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__thumb", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("<div class=\"@RootClass\" style=\"@TrackStyle\">", rangeSlider, StringComparison.Ordinal);
        Assert.Contains("ValueChanged.InvokeAsync", rangeSlider + speedControl, StringComparison.Ordinal);
        Assert.Contains("Quick presets", speedControl, StringComparison.Ordinal);
        Assert.Contains("Fine adjustment", speedControl, StringComparison.Ordinal);
        Assert.Contains("Reset to @DisplayFormat.FormatSpeedSlider(ResetValue)", speedControl, StringComparison.Ordinal);
        Assert.Contains("Quick presets", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("Current timer", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("Step=\"@SliderStepMinutes\"", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("InputStep=\"@SliderStepMinutes\"", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("SnapValueToStep=\"@(!IsTimerActive)\"", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("MajorPresetMinutes", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("SliderMaxMinutes", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("ClampToTimerStep", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("SleepTimerStop", sleepTimerControl, StringComparison.Ordinal);
        Assert.DoesNotContain("Off\\n0 min", sleepTimerControl, StringComparison.Ordinal);
        Assert.DoesNotContain("playback-sleep-timer__step-label", sleepTimerControl + sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Adjust in @FineStepMinutes-minute steps", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("System.Threading.Timer? _refreshTimer", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("TimeSpan.FromSeconds(15)", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("EndOfSectionChanged", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("playback-sleep-timer__presets", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-sleep-timer__stepper", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-sleep-timer__chapter", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-sleep-timer__step--decrease", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains("playback-sleep-timer__step--increase", sleepTimerControl, StringComparison.Ordinal);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__preset", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__chapter", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-sleep-timer ::deep .playback-sleep-timer__step", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(5, minmax(0, 1fr));", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(2, minmax(0, 1fr));", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("white-space: nowrap;", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("grid-template-columns: repeat(auto-fit, minmax(82px, 1fr));", sleepTimerControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-speed-control__presets", speedControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-speed-control__stepper", speedControlStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-speed-control ::deep .playback-speed-control__preset", speedControlStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-speed-control ::deep .playback-speed-control__step", speedControlStyles, StringComparison.Ordinal);
        Assert.Contains("playback-speed-control__fine", speedControlStyles, StringComparison.Ordinal);
        Assert.Contains("_pendingValue", speedControl, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__input", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-range-percent", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-range-track-inset", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__visual", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__track", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__fill", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("playback-range-slider__thumb", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("opacity: 0;", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("inset: 0 var(--playback-range-track-inset);", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("margin: 0 var(--playback-range-track-inset);", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-range-slider ::deep .playback-range-slider__label", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("position: absolute;", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("top: 0;", rangeSliderStyles, StringComparison.Ordinal);
        Assert.Contains("BookmarkPositionText(bookmark)", popup + bar, StringComparison.Ordinal);
        Assert.Contains("BookmarkSubtitle(bookmark)", popup + bar, StringComparison.Ordinal);
        Assert.Contains("Presentation=\"list\"", popup + bar, StringComparison.Ordinal);
        Assert.Contains("Kind=\"add\"", popup + bar, StringComparison.Ordinal);
        Assert.Contains("Size=\"compact\"", bar, StringComparison.Ordinal);
        Assert.Contains("HistoryPositionText(item)", popup + bar, StringComparison.Ordinal);
        Assert.Contains("Presentation=\"full-overlay\"", popup, StringComparison.Ordinal);
        Assert.Contains("playback-popout-shell", popoutShell, StringComparison.Ordinal);
        Assert.Contains("playback-mini-player", miniPlayer, StringComparison.Ordinal);
        Assert.Contains("playback-position-row", positionRow, StringComparison.Ordinal);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", positionRow, StringComparison.Ordinal);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", positionRow, StringComparison.Ordinal);
        Assert.Contains("Icons.Material.Outlined.CalendarMonth", positionRow, StringComparison.Ordinal);
        Assert.Contains("playback-position-row__action-ring", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("playback-position-row--action-plain", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("playback-position-row-shell--list", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("playback-position-row--list", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("playback-position-row--add", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("border: 1px solid var(--playback-accent-border);", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("border-bottom: 1px solid var(--playback-divider);", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("margin-top: 0;", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("var(--playback-accent)", positionRowStyles, StringComparison.Ordinal);
        Assert.Contains("playback-position-list", positionList, StringComparison.Ordinal);
        Assert.Contains("overflow: hidden;", positionListStyles, StringComparison.Ordinal);
        Assert.Contains("playback-sheet-list", sheetList, StringComparison.Ordinal);
        Assert.Contains("padding: 4px 12px 18px;", sheetListStyles, StringComparison.Ordinal);
        Assert.Contains("playback-sheet-row", sheetRow, StringComparison.Ordinal);
        Assert.Contains("playback-sheet-row-shell", sheetRow, StringComparison.Ordinal);
        Assert.Contains(".playback-sheet-row-shell ::deep .playback-sheet-row", sheetRowStyles, StringComparison.Ordinal);
        Assert.Contains("justify-content: space-between;", sheetRowStyles, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlCatalog.BuildToolStrip", popup, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlCatalog.BuildToolStrip", bar, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlKey.Captions", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlKey.AudioTrack", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlKey.Quality", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("<PlaybackIconButton", controlStrip, StringComparison.Ordinal);
        Assert.Contains(".playback-control-strip.listen-popup__actions", controlStripStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(5, minmax(44px, 1fr));", controlStripStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackSheetHandleButton", toolSheet, StringComparison.Ordinal);
        Assert.Contains("NormalizedPresentation", toolSheet, StringComparison.Ordinal);
        Assert.Contains("full-overlay", toolSheet, StringComparison.Ordinal);
        Assert.Contains("playback-tool-sheet--full-overlay", toolSheetStyles, StringComparison.Ordinal);
        Assert.Contains("playback-tool-sheet-roll-up", toolSheetStyles, StringComparison.Ordinal);
        Assert.Contains("prefers-reduced-motion", toolSheetStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("border-bottom: 1px solid rgba(148, 163, 184, 0.16);", toolSheetStyles, StringComparison.Ordinal);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", iconButton, StringComparison.Ordinal);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", iconButton, StringComparison.Ordinal);
        Assert.Contains("::deep .playback-icon-button__value", iconButtonStyles, StringComparison.Ordinal);
        Assert.Contains("::deep .playback-icon-button__label", iconButtonStyles, StringComparison.Ordinal);
        Assert.Contains("Control.BadgeText", iconButton, StringComparison.Ordinal);
        Assert.Contains("playback-icon-button__badge", iconButton + iconButtonStyles, StringComparison.Ordinal);
        Assert.Contains("Control.IsSelected ? \"is-selected\" : null", iconButton, StringComparison.Ordinal);
        Assert.Contains(".playback-icon-button.is-selected", iconButtonStyles, StringComparison.Ordinal);
        Assert.Contains("playback-icon-button--sleep-timer.is-active", iconButtonStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-icon-button-badge-color: var(--playback-accent);", iconButtonStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("#c084fc", iconButtonStyles, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("[Parameter(CaptureUnmatchedValues = true)]", sheetHandle, StringComparison.Ordinal);
        Assert.Contains("@attributes=\"AdditionalAttributes\"", sheetHandle, StringComparison.Ordinal);
        Assert.Contains("playback-sheet-handle-button-shell", sheetHandle, StringComparison.Ordinal);
        Assert.Contains(".playback-sheet-handle-button-shell ::deep .playback-sheet-handle-button", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("align-items: flex-start;", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("box-sizing: border-box;", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("height: 44px;", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("margin: 8px auto 0;", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("padding: 4px 36px 0;", sheetHandleStyles, StringComparison.Ordinal);
        Assert.Contains("OnKeyDown=\"HandlePopupKeyDown\"", popup, StringComparison.Ordinal);
        Assert.Contains("listen-popup-sheet__backdrop", popup + popupStyles, StringComparison.Ordinal);
        Assert.Contains("Size=\"standard\"", popup, StringComparison.Ordinal);
        Assert.Contains("listen-popup__actions", popupStyles, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: repeat(5, minmax(44px, 1fr));", popupStyles, StringComparison.Ordinal);
        Assert.Contains("Class=\"listen-player__audiobook-actions\"", bar, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Audiobook tools\"", bar, StringComparison.Ordinal);
        Assert.Contains("OnControl=\"HandleAudiobookPanelControl\"", bar, StringComparison.Ordinal);
        Assert.Contains("OnClose=\"CloseBottomPanelAsync\"", bar, StringComparison.Ordinal);
        Assert.Contains("BottomPanelTitle", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Class=\"listen-player-panel__audiobook-actions\"", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-player-panel__audiobook-actions", barStyles, StringComparison.Ordinal);
        Assert.Contains("::deep .listen-player-panel__tool-row", barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Playback.CyclePlaybackRateAsync()", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Icons.Material.Outlined.MoreHoriz", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("BottomPanelAriaLabel", bar, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlSurface.PictureInPicture", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlKey.Resume", controlCatalog, StringComparison.Ordinal);
        Assert.Contains("state.CanPictureInPicture", controlCatalog, StringComparison.Ordinal);
        Assert.DoesNotContain("SpeedActionButton", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("AudiobookActionButton", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("private RenderFragment ActionButton", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("history-index", bar + popup + barStyles + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("playback-sheet-row__index", popup + sheetRowStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__index", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("Variant=\"history\"", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("PlaybackHistoryRow", popup + bar + positionRow + positionRowStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__primary", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-player-panel__tool-primary", bar + barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("SecondaryActionLabel=\"Delete bookmark\"", popup + bar, StringComparison.Ordinal);
        Assert.Contains("\"speed\" => \"Adjust playback speed\"", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("SpeedRates", popup + bar, StringComparison.Ordinal);
        Assert.DoesNotContain("Choose playback speed", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("sheet-row--speed", popupStyles + barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__speed-row", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__radio", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-player-panel__radio", bar + barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("SleepTimerRow", bar + popup, StringComparison.Ordinal);
        Assert.DoesNotContain("TimerRow(", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("SleepTimerDisplay", popup, StringComparison.Ordinal);
        Assert.DoesNotContain("Current timer:", bar + popup, StringComparison.Ordinal);
        Assert.DoesNotContain("tool-row--timer", bar + barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__row--timer", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__grabber", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("listen-popup-sheet__close", popup + popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-popup__action strong", popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("display: none", popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-popup__chapter-row", popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-player__play {", barStyles, StringComparison.Ordinal);
        Assert.DoesNotContain(".listen-popup__play {", popupStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("background: var(--listen-accent", sharedStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("background: var(--listen-audio-accent", sharedStyles, StringComparison.Ordinal);
        Assert.Contains("<PlaybackRelativeSkipButton", shared, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Play or pause\"", shared, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Previous track\"", shared, StringComparison.Ordinal);
        Assert.Contains("AriaLabel=\"Next track\"", shared, StringComparison.Ordinal);
        Assert.Contains("viewBox=\"0 0 56 56\"", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__glyph", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__arc", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__arrow", skip, StringComparison.Ordinal);
        Assert.Contains("<text class=\"@NumberClass\"", skip, StringComparison.Ordinal);
        Assert.Contains("data-playback-seek-delta", skip, StringComparison.Ordinal);
        Assert.Contains("M47 29A19 19 0 1 1 28 9", skip, StringComparison.Ordinal);
        Assert.Contains("M9 29A19 19 0 1 0 28 9", skip, StringComparison.Ordinal);
        Assert.Contains("M27 4.8L36.5 8.5L27 12.2Z", skip, StringComparison.Ordinal);
        Assert.Contains("M29 4.8L19.5 8.5L29 12.2Z", skip, StringComparison.Ordinal);
        Assert.DoesNotContain("translate(56 0) scale(-1 1)", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__number--one-digit", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__number--two-digit", skip, StringComparison.Ordinal);
        Assert.Contains("playback-relative-skip__number--three-digit", skip, StringComparison.Ordinal);
        Assert.Contains("WatchingSkipBackOptions = [5, 10, 15, 30]", playbackSettings, StringComparison.Ordinal);
        Assert.Contains("WatchingSkipForwardOptions = [10, 30, 60, 90]", playbackSettings, StringComparison.Ordinal);
        Assert.Contains("ListeningSkipBackOptions = [10, 15, 30]", playbackSettings, StringComparison.Ordinal);
        Assert.Contains("ListeningSkipForwardOptions = [15, 30, 60]", playbackSettings, StringComparison.Ordinal);
        Assert.Contains("<AppNativeButton", skip, StringComparison.Ordinal);
        Assert.DoesNotContain("<button", skip, StringComparison.Ordinal);
        Assert.Contains(".playback-relative-skip ::deep .playback-relative-skip__button", skipStyles, StringComparison.Ordinal);
        Assert.Contains(".playback-relative-skip ::deep .playback-relative-skip__number", skipStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: 46px;", skipStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: 50px;", skipStyles, StringComparison.Ordinal);
        Assert.Contains("--playback-relative-skip-size: 54px;", skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("AudiobookSkipButton", shared + skip + skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("audiobook-skip-button", shared + skip + skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("audiobook-skip-button__line", shared + skip + skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("audiobook-skip-button__unit", shared + skip + skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("data-listen-seek-delta", shared + skip + skipStyles, StringComparison.Ordinal);
        Assert.DoesNotContain("title=\"@label\"", shared + skip, StringComparison.Ordinal);
        Assert.Contains("\"popup_width\": 460", playbackConfig, StringComparison.Ordinal);
        Assert.Contains("\"popup_height\": 820", playbackConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("private RenderFragment SkipButton", bar, StringComparison.Ordinal);
        Assert.DoesNotContain("private RenderFragment SkipButton", popup, StringComparison.Ordinal);
    }

    [Fact]
    public void DockedPlayers_UseCenteredLargeControlsAndCoverOnlyThumbnails()
    {
        var root = FindRepoRoot();
        var listenBar = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor"));
        var listenStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Listen/ListenNowPlayingBar.razor.css"));
        var videoHost = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor"));
        var videoStyles = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Watch/VideoPlaybackHost.razor.css"));
        var watchPlayer = File.ReadAllText(Path.Combine(root, "src/MediaEngine.Web/Components/Pages/WatchPlayerPage.razor"));

        Assert.Contains("Class=\"listen-player__utility-glyph\"", listenBar, StringComparison.Ordinal);
        Assert.Contains("Open Now Playing", listenBar, StringComparison.Ordinal);
        Assert.DoesNotContain("Playback device unavailable", listenBar, StringComparison.Ordinal);
        Assert.Contains("font-size: 26px !important;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("place-items: center;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("font-size: 28px !important;", videoStyles, StringComparison.Ordinal);
        Assert.Contains("font-size: 34px !important;", videoStyles, StringComparison.Ordinal);
        Assert.Contains("object-fit: contain;", videoStyles, StringComparison.Ordinal);
        Assert.True(
            listenBar.IndexOf("<div class=\"listen-player__progress\"", StringComparison.Ordinal)
            < listenBar.IndexOf("<div class=\"listen-player__actions\"", StringComparison.Ordinal));
        Assert.Contains("listen-player__chapter-context", listenBar, StringComparison.Ordinal);
        Assert.Contains("video-playback-restore", videoHost, StringComparison.Ordinal);
        Assert.DoesNotContain("<section class=\"video-playback-dock\"", videoHost, StringComparison.Ordinal);
        Assert.Contains(".listen-player__progress {", listenStyles, StringComparison.Ordinal);
        Assert.Contains("padding-inline: 14px;", listenStyles, StringComparison.Ordinal);
        Assert.Contains("border-inline: 1px solid rgba(148, 163, 184, 0.22);", listenStyles, StringComparison.Ordinal);
        Assert.Contains(".video-playback-restore {", videoStyles, StringComparison.Ordinal);
        Assert.Contains("GetDetailPageAsync", watchPlayer, StringComparison.Ordinal);
        Assert.Contains("OwnedEpisodeQueuePlanner.NextPlayableCandidates", watchPlayer, StringComparison.Ordinal);
        Assert.Contains("CoverUrl = detail.CoverUrl,", watchPlayer, StringComparison.Ordinal);
        Assert.DoesNotContain("CoverUrl = _detail.BackgroundUrl", watchPlayer, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaybackSurfaces_UseOneOrangeAdaptiveDesignSystem()
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

        Assert.Contains("--playback-accent: var(--tl-status-warning);", tokens, StringComparison.Ordinal);
        Assert.Contains("--playback-tool-width: 390px;", tokens, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("Icon=\"@Icon\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("aria-modal=\"@(Modal ? \"true\" : null)\"", toolSheet, StringComparison.Ordinal);
        Assert.Contains("@media (max-width: 720px)", toolSheetCss, StringComparison.Ordinal);
        Assert.Contains("max-height: 82svh;", toolSheetCss, StringComparison.Ordinal);
        Assert.Contains("<PlaybackToolSheet Title=\"@BottomPanelTitle\"", listenBar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackToolSheet Title=\"@MusicPanelTitle\"", listenBar, StringComparison.Ordinal);
        Assert.Contains("PlaybackControlCatalog.BuildToolStrip", listenBar, StringComparison.Ordinal);
        Assert.Contains("<PlaybackRelativeSkipButton", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackPrimaryButton", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackControlStrip Controls=\"@VideoToolControls\"", videoHost, StringComparison.Ordinal);
        Assert.Contains("<PlaybackToolSheet Title=\"@VideoToolTitle\"", videoHost, StringComparison.Ordinal);

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
