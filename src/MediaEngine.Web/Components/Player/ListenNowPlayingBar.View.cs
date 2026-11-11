using System.Globalization;
using MediaEngine.Contracts.Playback;
using MediaEngine.Domain.Enums;
using MediaEngine.Domain.Services;
using MediaEngine.Web.Components.Shared;
using MediaEngine.Web.Services.Integration;
using MediaEngine.Web.Services.MediaTiles;
using MediaEngine.Web.Services.Navigation;
using MediaEngine.Web.Services.Playback;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace MediaEngine.Web.Components.Player;

public partial class ListenNowPlayingBar
{
    // What the bar draws: the derived values the markup reads (shell class, state, identity, artwork, the Play button's start attributes) and small formatters.

    private string PlayerShellClass
    {
        get
        {
            var classes = IsListenRoute
                ? "listen-player-shell listen-player-shell--listen-route"
                : "listen-player-shell";

            if (Playback.PresentationSurface == PlaybackPresentationSurface.NowPlaying && IsPhonePresentation)
            {
                classes += " listen-player-shell--expanded";
            }

            return Playback.IsAudiobookMode
                ? $"{classes} listen-player-shell--audiobook"
                : classes;
        }
    }

    private double ProgressMax => Playback.DurationSeconds > 0 ? Playback.DurationSeconds : 1;

    private double VolumeStep => Playback.ClientSettings.VolumeStep;
    private string ProgressValueText => $"{FormatTimestamp(Playback.CurrentTimeSeconds)} of {FormatTimestamp(ProgressMax)}";
    private static string FormatVolumeValue(double value) => $"{Math.Clamp((int)Math.Round(value * 100d), 0, 100)}%";

    // The persistent bar (unified player v2 D1 to D6). PlaybackShell draws it from PlaybackControlCatalog; these are the host's answers.
    private PlaybackBarMedia BarMedia => Playback.IsAudiobookMode ? PlaybackBarMedia.Audiobook : PlaybackBarMedia.Music;
    private PlaybackSlotArtwork BarArtwork => PlaybackArtworkUrl.ForBarSlot(BarMedia, Playback.CurrentItem);
    // The music bar shares its Favorite state with Now playing; an audiobook's Favorite is read and written by the identity actions themselves.
    private bool? BarIsFavorite => Playback.IsMusicMode ? _isFavorite : null;
    private EventCallback BarFavoriteChanged => Playback.IsMusicMode ? EventCallback.Factory.Create(this, ToggleFavoriteAsync) : default;
    private PlaybackBarIdentity BarIdentity => Playback.CurrentItem is { } item
        ? PlaybackBarIdentity.Build(BarMedia, item, chapterTitle: Playback.CurrentChapter?.Title)
        : new(string.Empty, null, null, []);
    private PlaybackBarState BarState => new(
        IsPlaying: Playback.IsPlaying,
        NeedsUserGestureToStart: Playback.NeedsUserGestureToStart,
        // Music Previous always acts (it restarts the song at the start of the queue); an audiobook's steps are its chapters.
        CanPrevious: Playback.IsAudiobookMode ? Playback.CurrentItem?.Chapters.Count is > 0 : true,
        CanNext: Playback.IsAudiobookMode ? Playback.CurrentItem?.Chapters.Count is > 0 : CanPlayNext,
        SkipBackSeconds: Playback.SkipBackSeconds,
        SkipForwardSeconds: Playback.SkipForwardSeconds,
        ShuffleEnabled: Playback.ShuffleEnabled,
        RepeatMode: Playback.RepeatMode,
        PlaybackRate: Playback.PlaybackRate,
        HasChapters: CurrentChapters.Count > 0,
        CanBookmark: CanBookmarkCurrent,
        IsSleepTimerActive: Playback.SleepTimerState.Mode != AudiobookSleepTimerModes.Off,
        HasQueue: Playback.HasQueue,
        ActiveSheet: IsNowPlayingPage ? "now-playing" : null);
    private static readonly IReadOnlySet<PlaybackControlKey> HostedBarTools = new HashSet<PlaybackControlKey>
    {
        PlaybackControlKey.Lyrics, PlaybackControlKey.Queue, PlaybackControlKey.Chapters, PlaybackControlKey.History, PlaybackControlKey.Bookmarks, PlaybackControlKey.Speed, PlaybackControlKey.SleepTimer,
    };

    // Starting playback from a click needs the browser's own gesture, so the Play button carries what the page script needs to start the stream.
    private IReadOnlyDictionary<string, object> PlayButtonAttributes => new Dictionary<string, object>
    {
        ["data-listen-audio-toggle"] = "true",
        ["data-listen-start-url"] = Playback.CurrentBrowserStreamUrl ?? string.Empty,
        ["data-listen-start-position"] = Playback.CurrentTimeSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        ["data-listen-start-rate"] = Playback.PlaybackRate.ToString("0.###", CultureInfo.InvariantCulture),
        ["data-listen-start-volume"] = Playback.Volume.ToString("0.###", CultureInfo.InvariantCulture),
        ["data-listen-start-muted"] = Playback.IsMuted ? "true" : "false",
    };

    private bool CanPlayNext =>
        Playback.CurrentIndex < Playback.Queue.Count - 1
        || Playback.Queue.Count > 1 && Playback.ShuffleEnabled
        || Playback.Queue.Count > 0 && Playback.RepeatMode == PlayerRepeatModes.All;
    private string RepeatAriaLabel => Playback.RepeatMode switch
    {
        PlayerRepeatModes.One => "Repeat one",
        PlayerRepeatModes.All => "Repeat queue",
        _ => "Repeat off",
    };
    private PlaybackExperience CurrentMiniPlayerExperience => Playback.IsAudiobookMode ? PlaybackExperience.Audiobook : PlaybackExperience.Music;
    // The chapters the Sleep timer counts and stops at: every chapter the book has, as on the phone player, so a one-chapter book offers End of chapter in both.
    private IReadOnlyList<PlaybackChapterDto> SleepTimerChapters => Playback.CurrentItem?.Chapters ?? [];

    private IReadOnlyList<PlaybackChapterDto> CurrentChapters =>
        Playback.CurrentItem is { Chapters.Count: > 1 } item ? item.Chapters : [];
    private IReadOnlyList<PlaybackControlDefinition> AudiobookDockControls => SelectWorkspacePanels(
        PlaybackControlCatalog.BuildAudiobookDockToolStrip(new PlaybackControlState(
            PlaybackRate: Playback.PlaybackRate,
            HasChapters: CurrentChapters.Count > 0,
            IsSleepTimerActive: Playback.SleepTimerState.Mode != AudiobookSleepTimerModes.Off,
            SleepTimerValueText: SleepTimerBadgeText,
            HistoryCount: Playback.AudiobookHistory.Count)));
    private PlaybackControlDefinition AudiobookMuteControl => PlaybackControlCatalog.BuildUtilityControls(
        PlaybackExperience.Audiobook,
        PlaybackControlSurface.NowPlaying,
        new PlaybackControlState(IsMuted: Playback.IsMuted)).Single(control => control.Key == PlaybackControlKey.Mute);
    private IReadOnlyList<PlaybackControlDefinition> MusicNowPlayingControls =>
        SelectWorkspacePanels(PlaybackControlCatalog.BuildPrimaryToolStrip(
            PlaybackExperience.Music,
            PlaybackControlSurface.NowPlaying,
            new PlaybackControlState(
                HasQueue: Playback.HasQueue,
                CanFindLyrics: Playback.CurrentItem?.AssetId is not null,
                IsShuffleEnabled: Playback.ShuffleEnabled,
                IsRepeatEnabled: Playback.RepeatMode != PlayerRepeatModes.Off,
                RepeatMode: Playback.RepeatMode)));
    private IReadOnlyList<PlaybackControlDefinition> MusicDockControls =>
        MusicNowPlayingControls.Where(control => control.Key is PlaybackControlKey.Queue or PlaybackControlKey.Lyrics).ToList();
    private IReadOnlyList<PlaybackControlDefinition> MusicSecondaryControls =>
        SelectWorkspacePanels(PlaybackControlCatalog.BuildSecondaryToolStrip(
            PlaybackExperience.Music,
            PlaybackControlSurface.NowPlaying,
            new PlaybackControlState(
                HasQueue: Playback.HasQueue,
                CanFindLyrics: Playback.CurrentItem?.AssetId is not null,
                IsShuffleEnabled: Playback.ShuffleEnabled,
                IsRepeatEnabled: Playback.RepeatMode != PlayerRepeatModes.Off,
                RepeatMode: Playback.RepeatMode)));
    private IReadOnlyList<PlaybackControlDefinition> MusicMoreControls =>
        MusicDockControls.Concat(MusicSecondaryControls.Where(control => control.Key == PlaybackControlKey.History)).ToList();
    private IReadOnlyList<PlaybackControlDefinition> SelectWorkspacePanels(IReadOnlyList<PlaybackControlDefinition> controls)
    {
        if (IsPhonePresentation)
        {
            return controls;
        }

        var layout = AudioSidebar.For(Playback);
        return controls.Select(control =>
        {
            var selected = control.Sheet is { } sheet && layout.Open && layout.ActivePanelKey == sheet;
            return control with { IsSelected = selected };
        }).ToList();
    }

    private string? SleepTimerBadgeText => Playback.SleepTimerState.Mode switch
    {
        AudiobookSleepTimerModes.Timer when Playback.SleepTimerState.DeadlineUtc is { } deadline =>
            $"{Math.Max(0, (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalMinutes))}m",
        AudiobookSleepTimerModes.EndCurrent => "CH",
        AudiobookSleepTimerModes.EndNext => "NEXT",
        _ => null,
    };

    private static string PlayerModeButtonClass(bool active) =>
        $"listen-player__mode-button {(active ? "is-active" : string.Empty)}".Trim();

    private static string PlayerSubtitleFor(ListenQueueItem item, string? sourceLabel)
    {
        if (IsAudiobook(item))
        {
            return StringHelpers.FirstNonBlankOr("Library audio", item.Subtitle, item.Album, sourceLabel, "Audiobook");
        }

        return StringHelpers.FirstNonBlankOr("Library audio", item.Subtitle, item.Album, sourceLabel, "Music library");
    }

    private static string FormatHistoryStamp(DateTimeOffset? playedAt)
        => playedAt.HasValue
            ? playedAt.Value.LocalDateTime.ToString("M/d h:mm tt", CultureInfo.CurrentCulture)
            : "Played earlier";

    private static string FormatHistoryTime(DateTimeOffset? playedAt)
        => playedAt?.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture) ?? "—";

    private static string FormatHistoryTime(DateTimeOffset playedAt)
        => playedAt.LocalDateTime.ToString("h:mm tt", CultureInfo.CurrentCulture);

    private static string ContextSubtitle(params string?[] values)
        => StringHelpers.FirstNonBlankOr("Library audio", values);

    private static string FormatTimestamp(double seconds)
    {
        var safe = Math.Max(0, seconds);
        var span = TimeSpan.FromSeconds(safe);
        return span.TotalHours >= 1
            ? span.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private static string ChapterDuration(PlaybackChapterDto chapter) =>
        chapter.EndSeconds is > 0 && chapter.EndSeconds.Value > chapter.StartSeconds
            ? FormatTimestamp(chapter.EndSeconds.Value - chapter.StartSeconds)
            : string.Empty;

    private static bool IsAudiobook(ListenQueueItem item)
        => MediaKindClassifier.IsAudiobook(item.MediaType);
}
