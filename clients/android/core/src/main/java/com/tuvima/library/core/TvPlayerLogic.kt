package com.tuvima.library.core

import java.util.Locale

// The television player's rules without a screen (spec native-apps WP5): which transport buttons and tools show, the lines of
// text under the title, the profile's skip seconds, the key map of the contract's Televisions section and the rule that hides
// the overlay. Every fixed word is the contract's (clients/contracts/v1/player-controls.md); `TvPlayerContractTest` compares
// them with that file. The same decisions as the TV web app's player-logic.js, so every television reads the same.

/** What is playing, which decides the transport and the tool row. */
enum class TvMediaKind { Episode, Lesson, Film, Music, Audiobook }

enum class TvRepeatMode { Off, All, One }

/**
 * One button of the transport row. [dimmed] means it cannot act right now (dimmed in place, never left out); [selected] is the
 * purple state of Shuffle and Repeat; [seconds] is the number shown inside a skip button.
 */
data class TvControl(
    val key: String,
    val label: String,
    val dimmed: Boolean = false,
    val seconds: Int? = null,
    val selected: Boolean = false,
)

/** One tool of the tool row. */
data class TvTool(val key: String, val label: String)

/** The side panels a tool opens (and the nested views some of them hold). */
enum class TvPanelId { Episodes, AudioSubtitles, SubtitleAppearance, Bookmarks, PlaybackSettings, Speed, SkipBack, SkipForward, PlaybackInformation, Lyrics, Chapters }

/** The words of the television player, as the contract tables spell them. */
object TvPlayerWords {
    const val EPISODES = "Episodes"
    const val LESSONS = "Lessons"
    const val AUDIO_SUBTITLES = "Audio & subtitles"
    const val BOOKMARKS = "Bookmarks"
    const val PLAYBACK_SETTINGS = "Playback settings"
    const val LYRICS = "Lyrics"
    const val CHAPTERS = "Chapters"
    const val CLOSE = "Close"
    const val PLAY = "Play"
    const val PAUSE = "Pause"
    const val SEASON = "Season"
    const val AUTOPLAY = "Autoplay next episode"
    const val UP_NEXT = "Up next"
    const val PLAYING = "Playing"
    const val PAUSED = "Paused"
    const val AUDIO = "Audio"
    const val SUBTITLES = "Subtitles"
    const val OFF = "Off"
    const val DIRECTOR_COMMENTARY = "Director commentary"
    const val AUDIO_DESCRIPTION = "Audio description"
    const val SUBTITLE_APPEARANCE = "Subtitle appearance"
    const val STYLE = "Style"
    const val STYLE_DEFAULT = "Default"
    const val STYLE_LARGE = "Large text"
    const val STYLE_CONTRAST = "High contrast"
    const val BACKGROUND = "Background"
    const val BACKGROUND_NONE = "None"
    const val BACKGROUND_SHADOW = "Shadow"
    const val BACKGROUND_BOX = "Box"
    const val RESET_TO_DEFAULT = "Reset to default"
    const val CHANGES_SAVED = "Changes saved automatically"
    const val BOOKMARK_AT = "Bookmark at {0}"
    const val MINE = "Mine"
    const val GLOBAL = "Global"
    const val SHOW_ON_TIMELINE = "Show on timeline"
    const val DELETE = "Delete"
    const val KEEP = "Keep"
    const val COULD_NOT_SAVE = "Couldn’t save · Try again"
    const val NO_GLOBAL = "No Global bookmarks on this title yet"
    const val NO_MINE = "No bookmarks of yours yet"
    const val SELECT_BOOKMARK = "Select a bookmark to jump to that moment."
    const val PLAYBACK_SPEED = "Playback speed"
    const val CURRENT_SPEED = "Current speed"
    const val RESET_SPEED = "Reset to 1×"
    const val SKIP_BACK = "Skip back"
    const val SKIP_FORWARD = "Skip forward"
    const val PLAYBACK_INFORMATION = "Playback information"
    const val RESET_PREFERENCES = "Reset playback preferences"
    const val CHAPTER_OF = "Chapter {0} of {1}"
    const val BOOK_COMPLETE = "Book · {0}% complete"
    const val ELAPSED_REMAINING = "{0} elapsed · {1} remaining"
    const val FINISHED = "Finished"
    const val TIMELINE_SHOWS = "Timeline shows"
    const val CHAPTER = "Chapter"
    const val WHOLE_BOOK = "Whole book"
    const val SELECT_CHAPTER = "Select a chapter to start listening."
    const val END_HEADING = "Up next"
    const val END_COUNTDOWN = "Starts in {0} seconds"
    const val NEXT_EPISODE = "Next episode"
    const val NEXT_LESSON = "Next lesson"
    const val CANCEL = "Cancel"
    const val PLAY_NOW = "Play now"
    const val SEP = " · "

    /** Replaces `{0}` and `{1}` in a contract string. */
    fun fill(template: String, a: Any? = null, b: Any? = null): String =
        template.replace("{0}", a?.toString().orEmpty()).replace("{1}", b?.toString().orEmpty())
}

/** What this device keeps for itself: the profile's preferences are read-only for apps, so these never go to the Engine. */
data class TvDeviceChoices(
    val skipBack: Int? = null,
    val skipForward: Int? = null,
    val autoplay: Boolean? = null,
    val speed: Double? = null,
    val subtitleStyle: String? = null,
    val subtitleBackground: String? = null,
    val timelineWholeBook: Boolean = false,
    val showBookmarksOnTimeline: Boolean = true,
)

data class TvSkip(val back: Int, val forward: Int)

object TvPlayerLogic {
    const val OVERLAY_HIDE_MS = 5_000L
    const val DEFAULT_SKIP_BACK = 10
    const val DEFAULT_SKIP_FORWARD = 30

    /** Seconds in which Previous in music restarts the song instead of going to the one before (`music_previous_restart_seconds`). */
    const val MUSIC_RESTART_SECONDS = 3.0

    /** The profile's choices for video (the contract's skip-defaults table). */
    val SKIP_BACK_CHOICES = listOf(5, 10, 15, 30)
    val SKIP_FORWARD_CHOICES = listOf(10, 30, 60, 90)

    /** Speed presets (the contract's speed-presets table) and the range and step the Left and Right keys use. */
    val SPEED_PRESETS = listOf(0.75, 1.0, 1.25, 1.5, 2.0)
    const val SPEED_MIN = 0.5
    const val SPEED_MAX = 3.0
    const val SPEED_STEP = 0.05

    val APPEARANCE_STYLES = listOf(TvPlayerWords.STYLE_DEFAULT, TvPlayerWords.STYLE_LARGE, TvPlayerWords.STYLE_CONTRAST)
    val APPEARANCE_BACKGROUNDS = listOf(TvPlayerWords.BACKGROUND_NONE, TvPlayerWords.BACKGROUND_SHADOW, TvPlayerWords.BACKGROUND_BOX)

    // ---- time and length ----

    /** "7:42", "1:05:09". */
    fun formatTime(seconds: Double): String {
        val total = seconds.takeIf { it.isFinite() && it > 0.0 }?.toLong() ?: 0L
        val h = total / 3600
        val m = (total % 3600) / 60
        val s = total % 60
        val tail = (if (m < 10 && h > 0) "0" else "") + m + ":" + (if (s < 10) "0" else "") + s
        return if (h > 0) "$h:$tail" else tail
    }

    /** The time left at the right of the timeline, "-14:59"; nothing while the length is not known. */
    fun timeLeft(position: Double, duration: Double?): String {
        if (duration == null || !(duration > 0.0)) return ""
        return "-" + formatTime((duration - position).coerceAtLeast(0.0))
    }

    /** A length under an hour reads "22 min", from an hour "1h 52m"; nothing when it is not known. */
    fun lengthText(seconds: Double?): String {
        if (seconds == null || !(seconds > 0.0)) return ""
        val minutes = maxOf(1L, (seconds / 60).toLong())
        return if (minutes < 60) "$minutes min" else "${minutes / 60}h ${minutes % 60}m"
    }

    // ---- what is playing ----

    /** The kind from the Engine's media type and whether the title belongs to a show. A song or book is never a video. */
    fun kindOf(mediaType: String?, hasShow: Boolean): TvMediaKind {
        val type = mediaType?.trim()?.lowercase(Locale.ROOT).orEmpty()
        return when {
            "audiobook" in type -> TvMediaKind.Audiobook
            "music" in type || "song" in type || "track" in type -> TvMediaKind.Music
            type == "videos" || type == "video" -> TvMediaKind.Lesson
            hasShow || type == "tv" || "episode" in type -> TvMediaKind.Episode
            else -> TvMediaKind.Film
        }
    }

    /**
     * The one line under the title: "S1 E1 · The First Wish · 22 min" (episode), "3 · Ball handling · 12 min" (folder video),
     * "2019 · 1h 52m" (film).
     */
    fun detailLine(
        kind: TvMediaKind,
        season: Int? = null,
        episode: Int? = null,
        number: Int? = null,
        title: String? = null,
        year: Int? = null,
        durationSeconds: Double? = null,
    ): String {
        val length = lengthText(durationSeconds)
        val parts = mutableListOf<String>()
        when (kind) {
            TvMediaKind.Episode -> {
                val s = season?.takeIf { it > 0 }?.let { "S$it" }
                val e = episode?.takeIf { it > 0 }?.let { "E$it" }
                val code = if (s != null && e != null) "$s $e" else s ?: e
                if (code != null) parts += code
                if (!title.isNullOrBlank()) parts += title
                if (length.isNotEmpty()) parts += length
            }
            TvMediaKind.Lesson -> {
                number?.takeIf { it > 0 }?.let { parts += it.toString() }
                if (!title.isNullOrBlank()) parts += title
                if (length.isNotEmpty()) parts += length
            }
            else -> {
                year?.takeIf { it > 0 }?.let { parts += it.toString() }
                if (length.isNotEmpty()) parts += length
            }
        }
        return parts.joinToString(TvPlayerWords.SEP)
    }

    // ---- the profile's preferences ----

    private fun wholeSeconds(value: Int?, fallback: Int): Int =
        if (value != null && value > 0 && value <= PlaybackPreferences.MAX_SKIP_SECONDS) value else fallback

    /**
     * The seconds the skip buttons and the Left and Right keys use: a choice made on this device in Playback settings, else the
     * profile's saved pair (read from the Engine), else 10 back and 30 forward.
     */
    fun skipSeconds(prefs: PlaybackPreferences?, device: TvDeviceChoices?): TvSkip {
        val p = prefs ?: PlaybackPreferences.Default
        return TvSkip(
            back = wholeSeconds(device?.skipBack, wholeSeconds(p.skipBackSeconds, DEFAULT_SKIP_BACK)),
            forward = wholeSeconds(device?.skipForward, wholeSeconds(p.skipForwardSeconds, DEFAULT_SKIP_FORWARD)),
        )
    }

    /** Autoplay next episode: this device's switch, else the profile's saved preference, else on. */
    fun autoplayOn(prefs: PlaybackPreferences?, device: TvDeviceChoices?): Boolean =
        device?.autoplay ?: prefs?.autoplayNextEpisode ?: true

    // ---- speed ----

    fun clampSpeed(value: Double): Double {
        if (!value.isFinite() || !(value > 0.0)) return 1.0
        return Math.round(value.coerceIn(SPEED_MIN, SPEED_MAX) * 100.0) / 100.0
    }

    fun stepSpeed(value: Double, direction: Int): Double =
        clampSpeed(clampSpeed(value) + if (direction < 0) -SPEED_STEP else SPEED_STEP)

    /** "1×", "1.25×". */
    fun speedLabel(value: Double): String {
        val v = clampSpeed(value)
        val text = if (v == Math.floor(v)) v.toLong().toString() else v.toString().trimEnd('0').trimEnd('.')
        return "$text×"
    }

    /** A preset is ticked only when the speed equals it. */
    fun isPreset(value: Double): Boolean = SPEED_PRESETS.any { Math.abs(it - clampSpeed(value)) < 0.0001 }

    // ---- transport and tools ----

    /**
     * The transport row of the contract's transport tables. A button that cannot act is dimmed in place. For music, [canPrevious]
     * is [musicCanPrevious]: a song can be restarted once it has played a few seconds, or when a queue has one before it.
     */
    fun transport(
        kind: TvMediaKind,
        skip: TvSkip,
        playing: Boolean,
        canPrevious: Boolean,
        canNext: Boolean,
        shuffleOn: Boolean = false,
        repeat: TvRepeatMode = TvRepeatMode.Off,
    ): List<TvControl> {
        val play = TvControl("play-pause", if (playing) TvPlayerWords.PAUSE else TvPlayerWords.PLAY)
        val back = TvControl("skip-back", "Skip back ${skip.back} s", seconds = skip.back)
        val forward = TvControl("skip-forward", "Skip forward ${skip.forward} s", seconds = skip.forward)
        return when (kind) {
            TvMediaKind.Music -> listOf(
                TvControl("shuffle", "Shuffle", selected = shuffleOn),
                TvControl("previous", "Previous track", dimmed = !canPrevious),
                play,
                TvControl("next", "Next track", dimmed = !canNext),
                TvControl("repeat", "Repeat", selected = repeat != TvRepeatMode.Off),
            )
            TvMediaKind.Audiobook -> listOf(
                TvControl("previous", "Previous chapter", dimmed = !canPrevious), back, play, forward,
                TvControl("next", "Next chapter", dimmed = !canNext),
            )
            TvMediaKind.Episode -> listOf(
                TvControl("previous", "Previous episode", dimmed = !canPrevious), back, play, forward,
                TvControl("next", "Next episode", dimmed = !canNext),
            )
            TvMediaKind.Film, TvMediaKind.Lesson -> listOf(
                TvControl("previous", "Previous", dimmed = !canPrevious), back, play, forward,
                TvControl("next", "Next", dimmed = !canNext),
            )
        }
    }

    /** The index of Play in [transport] (third of five for every media); focus starts there. */
    const val PLAY_INDEX = 2

    /**
     * The tool row: the Dashboard's tools without Volume (the remote owns it), Fullscreen, Close and Picture in picture (a
     * television is already full screen). A tool the media or the app does not support is absent.
     */
    fun tools(kind: TvMediaKind, hasEpisodes: Boolean, canBookmark: Boolean, hasChapters: Boolean = false): List<TvTool> = when (kind) {
        TvMediaKind.Music -> listOf(TvTool("lyrics", TvPlayerWords.LYRICS))
        TvMediaKind.Audiobook -> if (hasChapters) listOf(TvTool("chapters", TvPlayerWords.CHAPTERS)) else emptyList()
        else -> buildList {
            if (hasEpisodes && (kind == TvMediaKind.Episode || kind == TvMediaKind.Lesson)) {
                add(TvTool("episodes", if (kind == TvMediaKind.Lesson) TvPlayerWords.LESSONS else TvPlayerWords.EPISODES))
            }
            add(TvTool("audio-subtitles", TvPlayerWords.AUDIO_SUBTITLES))
            if (canBookmark) add(TvTool("bookmarks", TvPlayerWords.BOOKMARKS))
            add(TvTool("playback-settings", TvPlayerWords.PLAYBACK_SETTINGS))
        }
    }

    /** The panel a tool opens. */
    fun panelOf(toolKey: String): TvPanelId? = when (toolKey) {
        "episodes" -> TvPanelId.Episodes
        "audio-subtitles" -> TvPanelId.AudioSubtitles
        "bookmarks" -> TvPanelId.Bookmarks
        "playback-settings" -> TvPanelId.PlaybackSettings
        "lyrics" -> TvPanelId.Lyrics
        "chapters" -> TvPanelId.Chapters
        else -> null
    }

    /** The tool that stays highlighted while a panel (or one of its nested views) is open. */
    fun toolOf(panel: TvPanelId): String = when (panel) {
        TvPanelId.Episodes -> "episodes"
        TvPanelId.AudioSubtitles, TvPanelId.SubtitleAppearance -> "audio-subtitles"
        TvPanelId.Bookmarks -> "bookmarks"
        TvPanelId.PlaybackSettings, TvPanelId.Speed, TvPanelId.SkipBack, TvPanelId.SkipForward, TvPanelId.PlaybackInformation -> "playback-settings"
        TvPanelId.Lyrics -> "lyrics"
        TvPanelId.Chapters -> "chapters"
    }

    /** The panel one level up from a nested view, or null at the top of a panel. */
    fun parentOf(panel: TvPanelId): TvPanelId? = when (panel) {
        TvPanelId.SubtitleAppearance -> TvPanelId.AudioSubtitles
        TvPanelId.Speed, TvPanelId.SkipBack, TvPanelId.SkipForward, TvPanelId.PlaybackInformation -> TvPanelId.PlaybackSettings
        else -> null
    }

    // ---- paused details and the end card ----

    /** Whether the paused details show: paused, no panel open (they come back when the panel closes) and no end card. */
    fun pausedDetailsVisible(paused: Boolean, panelOpen: Boolean, endCard: Boolean): Boolean = paused && !panelOpen && !endCard

    /** The overlay hides after 5 seconds, but never while a panel, the Find window or the end card is open, nor while paused. */
    fun overlayShouldHide(paused: Boolean, panelOpen: Boolean, finderOpen: Boolean, endCard: Boolean): Boolean =
        !paused && !panelOpen && !finderOpen && !endCard

    /** The rows of the end card. [remaining] is null while autoplay is off: the card stays and has no countdown. */
    fun endCard(nextTitle: String, kind: TvMediaKind, remaining: Int?): TvEndCardView = TvEndCardView(
        heading = TvPlayerWords.END_HEADING,
        countdown = if (remaining == null) "" else TvPlayerWords.fill(TvPlayerWords.END_COUNTDOWN, remaining),
        title = nextTitle,
        kind = if (kind == TvMediaKind.Lesson) TvPlayerWords.NEXT_LESSON else TvPlayerWords.NEXT_EPISODE,
        cancel = TvPlayerWords.CANCEL,
        playNow = TvPlayerWords.PLAY_NOW,
    )

    /** The end card's two buttons; Play now has focus first and Left and Right move between them. */
    fun endCardFocus(current: Int, key: TvKeyAction): Int = when (key) {
        TvKeyAction.Left -> 0
        TvKeyAction.Right -> 1
        else -> current
    }

    /** What Select does on the end card for the focused button (0 Cancel, 1 Play now). */
    fun endCardEffect(focused: Int): TvKeyEffect = if (focused == 0) TvKeyEffect.CancelEndCard else TvKeyEffect.PlayNext

    /** Music: after [MUSIC_RESTART_SECONDS] of play Previous restarts the song, within them it goes to the one before. */
    fun musicPreviousRestarts(positionSeconds: Double): Boolean = positionSeconds > MUSIC_RESTART_SECONDS

    /** Previous in music acts when there is a song before it or the current one has played long enough to restart. */
    fun musicCanPrevious(positionSeconds: Double, hasPreviousInQueue: Boolean): Boolean =
        hasPreviousInQueue || musicPreviousRestarts(positionSeconds)
}

data class TvEndCardView(
    val heading: String,
    val countdown: String,
    val title: String,
    val kind: String,
    val cancel: String,
    val playNow: String,
)

// ---- the remote ----

enum class TvKeyAction { Up, Down, Left, Right, Select, Back, PlayPause, Play, Pause, Rewind, FastForward, Stop, Captions }

object TvRemoteKeys {
    // Android's KeyEvent codes, written out so this stays free of Android types.
    private const val BACK = 4
    private const val DPAD_UP = 19
    private const val DPAD_DOWN = 20
    private const val DPAD_LEFT = 21
    private const val DPAD_RIGHT = 22
    private const val DPAD_CENTER = 23
    private const val MENU = 82
    private const val ENTER = 66
    private const val MEDIA_PLAY_PAUSE = 85
    private const val MEDIA_STOP = 86
    private const val MEDIA_REWIND = 89
    private const val MEDIA_FAST_FORWARD = 90
    private const val MEDIA_PLAY = 126
    private const val MEDIA_PAUSE = 127
    private const val ESCAPE = 111
    private const val NUMPAD_ENTER = 160
    private const val CAPTIONS = 175

    fun actionFor(keyCode: Int): TvKeyAction? = when (keyCode) {
        DPAD_UP -> TvKeyAction.Up
        DPAD_DOWN -> TvKeyAction.Down
        DPAD_LEFT -> TvKeyAction.Left
        DPAD_RIGHT -> TvKeyAction.Right
        DPAD_CENTER, ENTER, NUMPAD_ENTER -> TvKeyAction.Select
        BACK, ESCAPE -> TvKeyAction.Back
        MEDIA_PLAY_PAUSE -> TvKeyAction.PlayPause
        MEDIA_PLAY -> TvKeyAction.Play
        MEDIA_PAUSE -> TvKeyAction.Pause
        MEDIA_REWIND -> TvKeyAction.Rewind
        MEDIA_FAST_FORWARD -> TvKeyAction.FastForward
        MEDIA_STOP -> TvKeyAction.Stop
        CAPTIONS, MENU -> TvKeyAction.Captions
        else -> null
    }
}

/** What a key makes the player do. [Move] is the normal focus movement and Select on the focused control. */
enum class TvKeyEffect {
    Move, ShowOverlay, HideOverlay, ClosePanel, PanelBack, SkipBack, SkipForward, TogglePlay, Play, Pause,
    CancelEndCard, PlayNext, ClosePlayer, OpenAudioSubtitles,
}

/** Where the focus is, for the one rule that depends on it. */
enum class TvZone { Timeline, Transport, Tools, Panel }

data class TvKeyState(
    val overlayVisible: Boolean,
    val panelOpen: Boolean = false,
    /** True inside a nested view of a panel (Subtitle appearance, Playback speed ...). */
    val panelNested: Boolean = false,
    val endCard: Boolean = false,
    val zone: TvZone = TvZone.Transport,
)

data class TvKeyResult(val effect: TvKeyEffect, val consume: Boolean)

object TvKeyMap {
    /**
     * The contract's tv-keys table. Any key shows the overlay (a first key press on a hidden overlay only shows it); Left and
     * Right with the timeline focused skip by the profile's seconds; Rewind and Fast-forward are the Skip back and Skip forward
     * actions; Back closes the open side panel first (a nested view goes up one level), then hides the overlay, then leaves.
     */
    fun effect(state: TvKeyState, action: TvKeyAction): TvKeyResult {
        if (state.endCard) {
            return when (action) {
                TvKeyAction.Back -> TvKeyResult(TvKeyEffect.CancelEndCard, true)
                TvKeyAction.Stop -> TvKeyResult(TvKeyEffect.ClosePlayer, true)
                else -> TvKeyResult(TvKeyEffect.Move, false)
            }
        }
        when (action) {
            TvKeyAction.Back -> return if (state.panelOpen) {
                TvKeyResult(if (state.panelNested) TvKeyEffect.PanelBack else TvKeyEffect.ClosePanel, true)
            } else {
                TvKeyResult(if (state.overlayVisible) TvKeyEffect.HideOverlay else TvKeyEffect.ClosePlayer, true)
            }
            TvKeyAction.Stop -> return TvKeyResult(TvKeyEffect.ClosePlayer, true)
            TvKeyAction.Rewind -> return TvKeyResult(TvKeyEffect.SkipBack, true)
            TvKeyAction.FastForward -> return TvKeyResult(TvKeyEffect.SkipForward, true)
            TvKeyAction.PlayPause -> return TvKeyResult(TvKeyEffect.TogglePlay, true)
            TvKeyAction.Play -> return TvKeyResult(TvKeyEffect.Play, true)
            TvKeyAction.Pause -> return TvKeyResult(TvKeyEffect.Pause, true)
            TvKeyAction.Captions -> return TvKeyResult(TvKeyEffect.OpenAudioSubtitles, true)
            else -> Unit
        }
        if (!state.overlayVisible) return TvKeyResult(TvKeyEffect.ShowOverlay, true)
        if ((action == TvKeyAction.Left || action == TvKeyAction.Right) && state.zone == TvZone.Timeline) {
            return TvKeyResult(if (action == TvKeyAction.Left) TvKeyEffect.SkipBack else TvKeyEffect.SkipForward, true)
        }
        return TvKeyResult(TvKeyEffect.Move, false)
    }
}

/** Where focus is on the controls overlay: the timeline, the transport row or the tool row. */
data class TvFocus(val zone: TvZone, val transportIndex: Int, val toolIndex: Int = 0, val panelOpen: Boolean = false) {
    companion object {
        fun initial(playIndex: Int) = TvFocus(TvZone.Transport, playIndex)
    }
}

object TvControlsFocus {
    /**
     * Up and Down move between the timeline, the transport row and the tool row; Left and Right move along a row (on the
     * timeline they skip, which [TvKeyMap] has already decided). With a panel open Right from the last tool reaches the panel
     * (the caller then focuses the panel's first row).
     */
    fun move(focus: TvFocus, key: TvKeyAction, transportCount: Int, toolCount: Int): TvFocus = when (key) {
        TvKeyAction.Up -> when (focus.zone) {
            TvZone.Tools -> focus.copy(zone = TvZone.Transport)
            TvZone.Transport -> focus.copy(zone = TvZone.Timeline)
            else -> focus
        }
        TvKeyAction.Down -> when (focus.zone) {
            TvZone.Timeline -> focus.copy(zone = TvZone.Transport)
            TvZone.Transport -> if (toolCount > 0) focus.copy(zone = TvZone.Tools, toolIndex = focus.toolIndex.coerceIn(0, toolCount - 1)) else focus
            else -> focus
        }
        TvKeyAction.Left -> when (focus.zone) {
            TvZone.Transport -> focus.copy(transportIndex = (focus.transportIndex - 1).coerceAtLeast(0))
            TvZone.Tools -> focus.copy(toolIndex = (focus.toolIndex - 1).coerceAtLeast(0))
            else -> focus
        }
        TvKeyAction.Right -> when (focus.zone) {
            TvZone.Transport -> focus.copy(transportIndex = (focus.transportIndex + 1).coerceAtMost((transportCount - 1).coerceAtLeast(0)))
            TvZone.Tools ->
                if (focus.panelOpen && focus.toolIndex >= toolCount - 1) focus.copy(zone = TvZone.Panel)
                else focus.copy(toolIndex = (focus.toolIndex + 1).coerceAtMost((toolCount - 1).coerceAtLeast(0)))
            else -> focus
        }
        else -> focus
    }

    /** Left from the panel returns to the tool row that opened it (the panel stays open). */
    fun leavePanel(focus: TvFocus): TvFocus = focus.copy(zone = TvZone.Tools)
}
