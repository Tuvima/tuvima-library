package com.tuvima.library.core

// The phone players' rules without a screen (spec native-apps WP10b): the collapsed bar, the expanded transport, the one row of tools, the
// "..." menu, the skip seconds, the subtitles the phone remembers, and the next episode when only downloads are reachable. Every fixed word
// is the contract's (clients/contracts/v1/player-controls.md, the phone tables); `PhonePlayerContractTest` compares them with that file. The
// television player's panel builders (`TvPanels`) are reused by the phone's bottom sheets, so the words of a panel are written once.

/** One tool of the phone's tool row. [selected] is the purple state of Shuffle and Repeat; [badge] is the speed on the Speed chip ("1.25×"). */
data class PhoneTool(val key: String, val label: String, val selected: Boolean = false, val badge: String? = null)

/** A row of the expanded player's "..." menu (or the video overlay's). */
data class PhoneMenuRow(val key: String, val label: String)

object PhonePlayerLogic {
    /** The collapsed bar is 56 dp high (the contract's phone section). */
    const val BAR_HEIGHT_DP = 56

    /** Every control of a phone player is at least this big (the app's standing 44 dp rule). */
    const val MIN_TARGET_DP = 44

    /**
     * The collapsed bar's buttons. Music: Play and Next track; an audiobook: Play and Skip forward; video: Play only. Next track is
     * dimmed in place when there is no next song.
     */
    fun bar(kind: TvMediaKind, playing: Boolean, skip: TvSkip, canNext: Boolean): List<TvControl> {
        val play = TvControl("play-pause", if (playing) TvPlayerWords.PAUSE else TvPlayerWords.PLAY)
        return when (kind) {
            TvMediaKind.Music -> listOf(play, TvControl("next", "Next track", dimmed = !canNext))
            TvMediaKind.Audiobook -> listOf(play, TvControl("skip-forward", "Skip forward ${skip.forward} s", seconds = skip.forward))
            else -> listOf(play)
        }
    }

    /**
     * The expanded player's transport. Music: Previous track, Play, Next track (Shuffle and Repeat are tools on a phone). An audiobook and
     * video: the five buttons of the contract's transport tables.
     */
    fun transport(kind: TvMediaKind, skip: TvSkip, playing: Boolean, canPrevious: Boolean, canNext: Boolean): List<TvControl> = when (kind) {
        TvMediaKind.Music -> listOf(
            TvControl("previous", "Previous track", dimmed = !canPrevious),
            TvControl("play-pause", if (playing) TvPlayerWords.PAUSE else TvPlayerWords.PLAY),
            TvControl("next", "Next track", dimmed = !canNext),
        )
        else -> TvPlayerLogic.transport(kind, skip, playing, canPrevious, canNext)
    }

    /**
     * The tool row, at most four tools, in the contract's phone-tools table. A tool the media or the app cannot offer is absent (not
     * dimmed): Chapters without timed chapters, Episodes or Lessons for a title that stands alone, Bookmarks when the title has no work
     * to hang them on, and Fullscreen on a phone, whose video screen is already full screen. Music has no Bookmarks on the phone.
     */
    fun tools(
        kind: TvMediaKind,
        hasChapters: Boolean = false,
        hasEpisodes: Boolean = false,
        canBookmark: Boolean = false,
        canFullscreen: Boolean = false,
        shuffleOn: Boolean = false,
        repeat: TvRepeatMode = TvRepeatMode.Off,
        speed: Double = 1.0,
    ): List<PhoneTool> = when (kind) {
        TvMediaKind.Music -> listOf(
            PhoneTool("shuffle", "Shuffle", selected = shuffleOn),
            PhoneTool("lyrics", TvPlayerWords.LYRICS),
            PhoneTool("queue", "Queue"),
            PhoneTool("repeat", "Repeat", selected = repeat != TvRepeatMode.Off),
        )
        TvMediaKind.Audiobook -> buildList {
            if (hasChapters) add(PhoneTool("chapters", TvPlayerWords.CHAPTERS))
            add(PhoneTool("speed", TvPlayerWords.PLAYBACK_SPEED, badge = TvPlayerLogic.speedLabel(speed)))
            add(PhoneTool("sleep-timer", "Sleep timer"))
            if (canBookmark) add(PhoneTool("bookmarks", TvPlayerWords.BOOKMARKS))
        }
        else -> buildList {
            add(PhoneTool("audio-subtitles", TvPlayerWords.AUDIO_SUBTITLES))
            if (hasEpisodes && (kind == TvMediaKind.Episode || kind == TvMediaKind.Lesson)) {
                add(PhoneTool("episodes", if (kind == TvMediaKind.Lesson) TvPlayerWords.LESSONS else TvPlayerWords.EPISODES))
            }
            if (canBookmark) add(PhoneTool("bookmarks", TvPlayerWords.BOOKMARKS))
            if (canFullscreen) add(PhoneTool("fullscreen", "Fullscreen"))
        }
    }

    /**
     * The "..." menu. An audiobook has Playback settings there; a video's menu (top line of the overlay) has Playback settings and, where
     * the phone can float the picture, Picture in picture. Music has neither speed nor skips and no Bookmarks on the phone, so its menu has no
     * row of its own and the button is not drawn.
     */
    fun menu(kind: TvMediaKind, canPictureInPicture: Boolean = false): List<PhoneMenuRow> = when (kind) {
        TvMediaKind.Music -> emptyList()
        TvMediaKind.Audiobook -> listOf(PhoneMenuRow("playback-settings", TvPlayerWords.PLAYBACK_SETTINGS))
        else -> buildList {
            add(PhoneMenuRow("playback-settings", TvPlayerWords.PLAYBACK_SETTINGS))
            if (canPictureInPicture) add(PhoneMenuRow("picture-in-picture", "Picture in picture"))
        }
    }

    /** The speed an audiobook or a video was last set to on this phone, else the profile's own default, else 1×. */
    fun startingSpeed(prefs: PlaybackPreferences?, device: PhoneDeviceChoices?, audiobook: Boolean): Double {
        val chosen = if (audiobook) device?.audiobookSpeed else device?.videoSpeed
        return TvPlayerLogic.clampSpeed(chosen ?: prefs?.defaultPlaybackSpeed ?: 1.0)
    }

    /**
     * The skip seconds: this phone's choice (kept on the phone, because the profile's preferences have no write route for apps), else the
     * profile's saved pair, else 10 and 30. Audio's choices are 10, 15, 30 back and 15, 30, 60 forward, so a saved video number that
     * audio does not offer is moved to the nearest one it does.
     */
    fun skip(prefs: PlaybackPreferences?, device: PhoneDeviceChoices?, audio: Boolean): TvSkip {
        val video = TvPlayerLogic.skipSeconds(prefs, TvDeviceChoices(skipBack = device?.skipBack, skipForward = device?.skipForward))
        if (!audio) return video
        return TvSkip(nearest(AUDIO_SKIP_BACK_CHOICES, video.back), nearest(AUDIO_SKIP_FORWARD_CHOICES, video.forward))
    }

    val AUDIO_SKIP_BACK_CHOICES = listOf(10, 15, 30)
    val AUDIO_SKIP_FORWARD_CHOICES = listOf(15, 30, 60)

    private fun nearest(choices: List<Int>, value: Int): Int =
        choices.minByOrNull { Math.abs(it - value) } ?: value

    /** Autoplay next episode: this phone's switch, else the profile's saved preference, else on. */
    fun autoplayOn(prefs: PlaybackPreferences?, device: PhoneDeviceChoices?): Boolean = device?.autoplay ?: prefs?.autoplayNextEpisode ?: true

    /** The profile's media type and whether the title belongs to a show, as a kind (the television player's rule). */
    fun kindOf(mediaType: String?, hasShow: Boolean): TvMediaKind = TvPlayerLogic.kindOf(mediaType, hasShow)

    /** Previous in a video: the title before this one in its show, dimmed in place when there is none. Next likewise. */
    fun videoNeighbours(seasons: List<TvSeason>, assetId: String): TvNeighbours = TvEpisodes.neighbours(seasons, assetId)

    /**
     * The tick on the timeline for each bookmark when the person wants them shown, as percentages of the length. A bookmark at or past the
     * end, and every bookmark while the length is not known, is left out.
     */
    fun timelineTicks(items: List<TvBookmark>, durationSeconds: Double?, showOnTimeline: Boolean): List<Double> =
        if (!showOnTimeline || durationSeconds == null) emptyList() else TvPanels.bookmarkTicks(items, durationSeconds)

    /** The line under the title in the video overlay and the paused details: "S1 E1 · The First Wish · 22 min". */
    fun detailLine(kind: TvMediaKind, episode: TvEpisode?, year: Int?, durationSeconds: Double?): String = when (kind) {
        TvMediaKind.Episode -> TvPlayerLogic.detailLine(kind, season = episode?.season, episode = episode?.number, title = episode?.title, durationSeconds = durationSeconds)
        TvMediaKind.Lesson -> TvPlayerLogic.detailLine(kind, number = episode?.number, title = episode?.title, durationSeconds = durationSeconds)
        else -> TvPlayerLogic.detailLine(kind, year = year, durationSeconds = durationSeconds)
    }

    /**
     * The work a title's bookmarks hang on, from the page that started playback: a film's or other single title's own id. A show or a
     * collection has none, and an episode's comes from the show's episode list once that is read.
     */
    fun workIdFor(entityType: String?, id: String?): String? {
        val clean = id?.substringBefore('?')?.substringBefore('#')?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        val type = entityType?.trim().orEmpty()
        if (type.isEmpty() || OfflineLogic.isShowEntity(type) || type.equals("collection", ignoreCase = true) || type.equals("tvEpisode", ignoreCase = true)) return null
        return clean
    }

    /** The wording of an end card's countdown and buttons for a phone (the same rows as the Dashboard's and the television's). */
    fun endCard(nextTitle: String, kind: TvMediaKind, secondsLeft: Int?): TvEndCardView = TvPlayerLogic.endCard(nextTitle, kind, secondsLeft)
}

/**
 * What a phone keeps for itself: the profile's playback preferences have no write route for apps, so Autoplay, the skip seconds, the speed,
 * the subtitle appearance and the Bookmarks "Show on timeline" switch stay on the phone. They are read per profile (the phone is shared
 * by the people who sign in on it), see `PhoneDeviceStore`.
 */
data class PhoneDeviceChoices(
    val skipBack: Int? = null,
    val skipForward: Int? = null,
    val autoplay: Boolean? = null,
    val videoSpeed: Double? = null,
    val audiobookSpeed: Double? = null,
    val subtitleStyle: String? = null,
    val subtitleBackground: String? = null,
    val timelineWholeBook: Boolean = false,
    val showBookmarksOnTimeline: Boolean = true,
    /** The subtitle choice remembered for this profile: "off", or a language such as "English" (see [SubtitleMemory]). Null when none was made. */
    val rememberedSubtitle: String? = null,
)

/**
 * Subtitles start off and are then remembered per profile: until a person picks something, a video starts with the subtitles the profile's own
 * preference asks for (and with none when the Engine could not be asked); picking a language or Off in Audio & subtitles is remembered for that
 * profile on that phone, and the next video starts with it. A remembered language the next video does not carry means no subtitles, never a
 * different language.
 */
object SubtitleMemory {
    const val OFF = "off"

    /** What to remember after the person picked a row: Off, or the picked track's language name. A track with no language keeps nothing. */
    fun remember(pickedOff: Boolean, pickedLanguage: String?): String? =
        if (pickedOff) OFF else pickedLanguage?.trim()?.takeIf { it.isNotEmpty() }

    /**
     * The starting audio and subtitles. [prefsFromEngine] is false when the profile's preferences could not be read (the defaults then say
     * nothing about subtitles, so they start off). A remembered choice always wins over the profile's preference.
     */
    fun choose(
        remembered: String?,
        prefsFromEngine: Boolean,
        prefs: PlaybackPreferences,
        audio: List<AudioTrack>,
        subtitles: List<SubtitleTrack>,
    ): TrackChoice {
        val mode = when {
            remembered == OFF -> ShowSubtitles.Off
            remembered != null -> ShowSubtitles.Always
            prefsFromEngine -> prefs.subtitleMode
            else -> ShowSubtitles.Off
        }
        val wanted = if (remembered != null && remembered != OFF) remembered else prefs.subtitleLanguage
        val picked = PlaybackTrackPicker.pick(audio, subtitles, prefs.audioLanguage, wanted, mode)
        // A remembered language that this video does not carry leaves the subtitles off; the picker's "any default" fallback would show another language.
        if (remembered != null && remembered != OFF && picked.subtitleIndex != null) {
            val track = subtitles.firstOrNull { it.index == picked.subtitleIndex }
            if (track != null && !PlaybackTrackPicker.matches(track.language, remembered)) return TrackChoice(picked.audioIndex, null)
        }
        return picked
    }
}

/**
 * The episodes of a show that are on this phone, for when the Engine cannot be reached: the next one is offered from the downloads
 * ("next episode from downloads"). Order comes from the label every download of an episode carries ("Season 1, episode 2").
 */
object DownloadedEpisodes {
    private val seasonEpisode = Regex("^Season (\\d+), episode (\\d+)$")
    private val special = Regex("^Special (\\d+)$")
    private val episodeOnly = Regex("^Episode (\\d+)$")

    /** Season and episode numbers from a download's label, or null when it is not an episode label. Specials are season 0. */
    fun numbersOf(label: String?): Pair<Int, Int>? {
        val text = label?.trim() ?: return null
        seasonEpisode.matchEntire(text)?.let { return it.groupValues[1].toInt() to it.groupValues[2].toInt() }
        special.matchEntire(text)?.let { return 0 to it.groupValues[1].toInt() }
        episodeOnly.matchEntire(text)?.let { return 1 to it.groupValues[1].toInt() }
        return null
    }

    /**
     * The finished, unexpired downloads of [showId] that come after [currentAssetId], in season and episode order, as the same rows the
     * Engine's next-episodes answer would give. An episode whose label cannot be read goes last, in the order it was queued.
     */
    fun after(records: List<DownloadRecord>, showId: String, currentAssetId: String, nowMs: Long): List<NextEpisode> {
        val own = records
            .filter { it.showId == showId && it.isComplete && !it.isPhoto && !OfflineLogic.isExpired(it, nowMs) }
            .sortedWith(
                compareBy<DownloadRecord>(
                    { numbersOf(it.subtitle)?.first ?: Int.MAX_VALUE },
                    { numbersOf(it.subtitle)?.second ?: Int.MAX_VALUE },
                    { it.queuedAtMs },
                ),
            )
        val at = own.indexOfFirst { it.assetId == currentAssetId }
        val following = if (at < 0) own.filter { it.assetId != currentAssetId } else own.drop(at + 1)
        return following.map {
            val numbers = numbersOf(it.subtitle)
            NextEpisode(
                workId = it.workId,
                assetId = it.assetId,
                title = it.title,
                seasonNumber = numbers?.first,
                episodeNumber = numbers?.second,
                stillUrl = it.artworkUrl,
            )
        }
    }
}
