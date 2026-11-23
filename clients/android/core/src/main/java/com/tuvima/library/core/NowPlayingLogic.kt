package com.tuvima.library.core

// The music and audiobook players' rules without a screen (spec native-apps WP10c): what is playing, what Previous, Next and the skips do, the
// timeline, the Sleep timer's choices, the Lyrics panel's words and the lock-screen card's facts. The media service and the Now playing screen both
// read these, so the two never disagree. Every fixed word is the contract's (clients/contracts/v1/player-controls.md).

/**
 * What the media service is playing, as far as the screens need to know: the kind, the title lines, the chapters of an audiobook, the work its
 * bookmarks hang on and the name of where the queue plays from. [None] means nothing, or a video (which has its own screen).
 */
data class AudioNow(
    val assetId: String,
    val kind: TvMediaKind,
    val title: String,
    val artist: String? = null,
    val album: String? = null,
    val author: String? = null,
    val workId: String? = null,
    val artworkUrl: String? = null,
    val sourceName: String? = null,
    val chapters: List<Chapter> = emptyList(),
    /** True when the file is a downloaded copy on this phone (a bookmark made with no connection waits on the phone). */
    val isDownload: Boolean = false,
) {
    val isAudiobook: Boolean get() = kind == TvMediaKind.Audiobook

    companion object {
        val None = AudioNow(assetId = "", kind = TvMediaKind.Music, title = "")
    }

    val isNone: Boolean get() = assetId.isEmpty()
}

/** What one press of Previous, Next or a skip asks the player to do. */
sealed interface TransportStep {
    data object Nothing : TransportStep

    /** Go to this moment of the file in play (a restart, a chapter start or a skip). */
    data class SeekTo(val seconds: Double) : TransportStep

    /** Go to the song before or after in the queue. */
    data object PreviousItem : TransportStep
    data object NextItem : TransportStep
}

object NowPlayingLogic {
    /** The Engine's media type for the file (or the Details entity type) decides the kind: an audiobook, otherwise music. */
    fun kindOf(mediaType: String?, entityType: String? = null): TvMediaKind {
        val text = (mediaType.orEmpty() + " " + entityType.orEmpty()).lowercase()
        return if ("audiobook" in text) TvMediaKind.Audiobook else TvMediaKind.Music
    }

    /** The top line of the expanded player: "Playing from Northern Lights"; null when the queue has no source name. */
    fun playingFrom(sourceName: String?): String? =
        sourceName?.trim()?.takeIf(String::isNotEmpty)?.let { QueueLogic.PLAYING_FROM + " " + it }

    /** The line under the title: artist · album for a song, author · chapter name for an audiobook (the lock screen's second line). */
    fun secondLine(now: AudioNow, positionSeconds: Double): String? =
        LockScreen.line2(factsFor(now, positionSeconds, hasNext = false))

    /** The chapter's name at [positionSeconds] ("Chapter 8" when the file calls it nothing). Null without chapters. */
    fun chapterName(chapters: List<Chapter>, positionSeconds: Double): String? {
        if (chapters.isEmpty()) return null
        val index = TvChapters.indexAt(chapters, positionSeconds)
        return chapters[index].title.trim().ifEmpty { "Chapter ${index + 1}" }
    }

    /** The lock-screen facts of what is playing now. */
    fun factsFor(now: AudioNow, positionSeconds: Double, hasNext: Boolean): LockScreenFacts = LockScreenFacts(
        kind = now.kind,
        title = now.title,
        artist = now.artist,
        album = now.album,
        author = now.author,
        chapterTitle = if (now.kind == TvMediaKind.Audiobook) chapterName(now.chapters, positionSeconds) else null,
        hasNext = hasNext,
        hasChapters = now.chapters.isNotEmpty(),
    )

    /**
     * Whether Previous and Next can act now, for dimming them in place. Music: Previous can always restart the song, Next needs a song after
     * (or Repeat all). An audiobook: both need timed chapters.
     */
    fun canPrevious(kind: TvMediaKind, positionSeconds: Double, hasPreviousItem: Boolean, chapters: List<Chapter>): Boolean =
        if (kind == TvMediaKind.Audiobook) TvChapters.hasPrevious(chapters, positionSeconds) else MusicPrevious.canAct(positionSeconds, hasPreviousItem)

    fun canNext(kind: TvMediaKind, positionSeconds: Double, hasNextItem: Boolean, chapters: List<Chapter>): Boolean =
        if (kind == TvMediaKind.Audiobook) TvChapters.hasNext(chapters, positionSeconds) else hasNextItem

    /** True when a song follows the one in play: a later song, or any song with Repeat all on. */
    fun hasNextSong(index: Int, count: Int, repeat: TvRepeatMode): Boolean = count > 0 && (index < count - 1 || (repeat == TvRepeatMode.All && count > 1))

    /** True when a song comes before the one in play: an earlier song, or any song with Repeat all on. */
    fun hasPreviousSong(index: Int, count: Int, repeat: TvRepeatMode): Boolean = count > 0 && (index > 0 || (repeat == TvRepeatMode.All && count > 1))

    /**
     * Previous. A song restarts after 3 seconds of play and otherwise goes to the song before (at the start of the queue it restarts); an
     * audiobook goes to the start of this chapter after a few seconds into it, otherwise to the chapter before.
     */
    fun previous(kind: TvMediaKind, positionSeconds: Double, hasPreviousItem: Boolean, chapters: List<Chapter>): TransportStep {
        if (kind == TvMediaKind.Audiobook) {
            return TvChapters.previousStart(chapters, positionSeconds)?.let { TransportStep.SeekTo(it) } ?: TransportStep.Nothing
        }
        return when (MusicPrevious.decide(positionSeconds, hasPreviousItem)) {
            PreviousAction.Restart -> TransportStep.SeekTo(0.0)
            PreviousAction.GoToPrevious -> TransportStep.PreviousItem
        }
    }

    fun next(kind: TvMediaKind, positionSeconds: Double, hasNextItem: Boolean, chapters: List<Chapter>): TransportStep {
        if (kind == TvMediaKind.Audiobook) {
            return TvChapters.nextStart(chapters, positionSeconds)?.let { TransportStep.SeekTo(it) } ?: TransportStep.Nothing
        }
        return if (hasNextItem) TransportStep.NextItem else TransportStep.Nothing
    }

    /** A skip by [deltaSeconds] (negative goes back) kept inside the file. */
    fun skip(positionSeconds: Double, deltaSeconds: Int, durationSeconds: Double?): TransportStep {
        val target = positionSeconds + deltaSeconds
        val end = durationSeconds?.takeIf { it > 0.0 }
        return TransportStep.SeekTo(if (end != null) target.coerceIn(0.0, end) else target.coerceAtLeast(0.0))
    }

    /** The timeline under the title: the whole file, or an audiobook's chapter when the person chose "Chapter". */
    fun timeline(now: AudioNow, positionSeconds: Double, durationSeconds: Double?, wholeBook: Boolean): TvTimeline =
        TvChapters.timeline(if (now.isAudiobook) now.chapters else emptyList(), positionSeconds, durationSeconds, wholeBook)

    /**
     * Where a drag on the timeline lands, in seconds of the file. A chapter timeline (an audiobook with the "Chapter" choice) moves inside
     * its chapter; any other moves across the whole file.
     */
    fun seekFromTimeline(now: AudioNow, fraction: Double, positionSeconds: Double, durationSeconds: Double?, wholeBook: Boolean): Double {
        val f = fraction.coerceIn(0.0, 1.0)
        if (now.isAudiobook && !wholeBook && now.chapters.isNotEmpty()) {
            val index = TvChapters.indexAt(now.chapters, positionSeconds)
            val start = now.chapters[index].startSeconds
            val end = TvChapters.endOf(now.chapters, index, durationSeconds)
            if (end != null && end > start) return start + f * (end - start)
        }
        return (durationSeconds ?: 0.0) * f
    }

    // ---- Sleep timer ----

    /**
     * The timer a choice of the Sleep timer panel starts: a preset's minutes, or End of chapter. Null for Custom duration… (the screen asks for a
     * length first) and for a choice that cannot start (End of chapter without chapters).
     */
    fun sleepTimerFor(choiceKey: String, nowMs: Long, chapters: List<Chapter>, positionSeconds: Double, durationSeconds: Double?): SleepTimer? = when {
        choiceKey == SleepTimerLogic.CUSTOM_KEY -> null
        choiceKey == SleepTimerLogic.END_OF_CHAPTER_KEY ->
            SleepTimerLogic.endOfChapter(chapters, positionSeconds, durationSeconds).takeIf { it != SleepTimer.Off }
        else -> choiceKey.toIntOrNull()?.let { SleepTimerLogic.after(it, nowMs) }
    }

    /**
     * The timer after the item in play changed. An End of chapter timer belongs to one book and one chapter, so it is dropped when another item
     * takes over; the same item put back in place (a network change, a token refresh) keeps it. A timed timer is never dropped by an item change.
     */
    fun timerAfterItemChange(timer: SleepTimer, previousAssetId: String?, newAssetId: String?): SleepTimer =
        if (timer is SleepTimer.EndOfChapter && previousAssetId != newAssetId) SleepTimer.Off else timer

    /** The Sleep timer panel's rows with the one that is running ticked (an End of chapter timer ticks its row; a timed one ticks none). */
    fun sleepChoiceChecked(choice: SleepChoice, timer: SleepTimer): Boolean =
        timer is SleepTimer.EndOfChapter && choice.key == SleepTimerLogic.END_OF_CHAPTER_KEY

    // ---- Lyrics ----

    const val LYRICS_SYNCED_FOOTER = "Synced lyrics"
    const val LYRICS_UNSYNCED_FOOTER = "Unsynced lyrics"
    const val RESUME_FOLLOWING = "Resume following"
    const val OPEN_NOW_PLAYING = "Open Now playing"
    const val SELECT_LINE = "Select a line to jump to that moment."

    /**
     * A line of synced lyrics seeks when it is selected; plain (unsynced) lyrics have no times and do not.
     * Returns the moment to go to, or null.
     */
    fun lyricSeek(line: LyricLine): Double? = line.atSeconds

    /** The hint under the lyrics: only synced lyrics can be selected to jump. */
    fun lyricsHint(parsed: ParsedLyrics?): String? = if (parsed?.synced == true) SELECT_LINE else null

    /**
     * Whether the lyrics follow playback: they do until the person scrolls by hand, and again after "Resume following". Returns whether a
     * pill with those words should show.
     */
    fun showResumeFollowing(parsed: ParsedLyrics?, userScrolled: Boolean): Boolean = parsed?.synced == true && userScrolled

    // ---- the audiobook's speed ----

    /** The speed an audiobook starts at (this phone's last choice, else the profile's, else 1×) and music always plays at 1×. */
    fun startSpeed(kind: TvMediaKind, prefs: PlaybackPreferences?, device: PhoneDeviceChoices?): Double =
        if (kind == TvMediaKind.Audiobook) PhonePlayerLogic.startingSpeed(prefs, device, audiobook = true) else 1.0

    // ---- the mini player ----

    /** The 56 dp bar shows while a song or an audiobook is loaded; a video, which has its own screen, never shows it. */
    fun showsMiniPlayer(now: AudioNow): Boolean = !now.isNone

    /** What the bar reads under the title: the same second line as the lock screen. */
    fun barLine(now: AudioNow, positionSeconds: Double): String? = secondLine(now, positionSeconds)
}
