package com.tuvima.library.core

// The side panels of the television player as plain data (spec native-apps WP5): each builder turns what the player knows into
// a header, a body and a footer in the contract's words, and one key reducer moves focus over any of them. The screen only
// draws a panel; it never decides a word or a row. Panels hold nothing that has to be typed: there is no note field and no
// search box on a television.

/** One choice inside a row (the Delete and Keep buttons of a bookmark) or a segment (Mine, Global). */
data class TvChoice(val key: String, val label: String)

sealed interface TvItem

/** A small heading such as "Audio" or "Timeline shows". */
data class TvLabel(val text: String) : TvItem

/** A line of quiet text: an empty state, a hint, the saved-automatically note. */
data class TvNote(val text: String) : TvItem

/** A fact on a line that cannot be selected (Playback information, the chapter identity). */
data class TvInfoLine(val label: String, val value: String) : TvItem

/**
 * One row. [actions] are buttons at its right that Right reaches (the bookmark's Delete); [adjustable] rows change with Left
 * and Right (the fine speed, the Season); [primary] is the full-width purple button; [current] marks the playing episode.
 */
data class TvRow(
    val key: String,
    val title: String,
    val sub: String? = null,
    val trailing: String? = null,
    val chip: String? = null,
    val checked: Boolean = false,
    val imageUrl: String? = null,
    val showImage: Boolean = false,
    val actions: List<TvChoice> = emptyList(),
    val adjustable: Boolean = false,
    val primary: Boolean = false,
    val current: Boolean = false,
) : TvItem

/** A two-way choice (Mine | Global, Chapter | Whole book): Left and Right change it, and it is one focus stop. */
data class TvSegment(val key: String, val options: List<TvChoice>, val selectedKey: String) : TvItem

/** A switch (Autoplay next episode, Show on timeline). */
data class TvSwitch(val key: String, val label: String, val on: Boolean) : TvItem

/** A panel: [back] shows the back arrow of a nested view. [focusKey] is the row focus starts on. */
data class TvPanel(
    val id: TvPanelId,
    val title: String,
    val back: Boolean = false,
    val body: List<TvItem> = emptyList(),
    val footer: List<TvItem> = emptyList(),
    val focusKey: String? = null,
) {
    /** The items that take focus, body first and then the footer. */
    fun focusable(): List<TvItem> = (body + footer).filter { it.focusKey() != null }

    /** Where focus starts: the [focusKey] row, else the first row, else the header (-1) when there is nothing to select. */
    fun initialIndex(): Int {
        val items = focusable()
        if (items.isEmpty()) return -1
        val wanted = focusKey ?: return 0
        return items.indexOfFirst { it.focusKey() == wanted }.coerceAtLeast(0)
    }
}

fun TvItem.focusKey(): String? = when (this) {
    is TvRow -> key
    is TvSegment -> key
    is TvSwitch -> key
    else -> null
}

/** [index] counts the focusable items (-1 is the header); [sub] is the button at a row's right (or in the header). */
data class TvPanelFocus(val index: Int = 0, val sub: Int = 0)

sealed interface TvPanelEffect {
    data object None : TvPanelEffect

    /** Select on a row (or on one of its [action] buttons) or a switch. */
    data class Activate(val key: String, val action: String? = null) : TvPanelEffect

    /** Left or Right on a segment picked another option. */
    data class Choose(val segmentKey: String, val optionKey: String) : TvPanelEffect

    /** Left or Right on an adjustable row. */
    data class Adjust(val key: String, val delta: Int) : TvPanelEffect

    /** Left at the edge of the panel: focus returns to the tool row (the panel stays open). */
    data object Leave : TvPanelEffect
}

object TvPanelKeys {
    /** The keys of the header's two buttons in [TvPanelEffect.Activate]. */
    const val HEADER_BACK = "header-back"
    const val HEADER_CLOSE = "header-close"

    fun reduce(panel: TvPanel, focus: TvPanelFocus, key: TvKeyAction): Pair<TvPanelFocus, TvPanelEffect> {
        val items = panel.focusable()
        // Index -1 is the header: the back arrow of a nested view, then Close. A panel with nothing to select stays there.
        if (focus.index < 0 || items.isEmpty()) return header(panel, focus.sub, key, hasItems = items.isNotEmpty())
        val index = focus.index.coerceIn(0, items.size - 1)
        val item = items[index]
        return when (key) {
            TvKeyAction.Up -> TvPanelFocus(index - 1) to TvPanelEffect.None
            TvKeyAction.Down -> TvPanelFocus((index + 1).coerceAtMost(items.size - 1)) to TvPanelEffect.None
            TvKeyAction.Select -> when (item) {
                is TvRow -> TvPanelFocus(index, focus.sub.coerceIn(0, item.actions.size)) to
                    TvPanelEffect.Activate(item.key, item.actions.getOrNull(focus.sub - 1)?.key)
                is TvSwitch -> TvPanelFocus(index) to TvPanelEffect.Activate(item.key)
                is TvSegment -> TvPanelFocus(index) to next(item)
                else -> focus to TvPanelEffect.None
            }
            TvKeyAction.Left -> when {
                item is TvSegment -> {
                    val at = item.options.indexOfFirst { it.key == item.selectedKey }
                    if (at > 0) TvPanelFocus(index) to TvPanelEffect.Choose(item.key, item.options[at - 1].key)
                    else TvPanelFocus(index) to TvPanelEffect.Leave
                }
                item is TvRow && focus.sub > 0 -> TvPanelFocus(index, focus.sub - 1) to TvPanelEffect.None
                item is TvRow && item.adjustable -> TvPanelFocus(index) to TvPanelEffect.Adjust(item.key, -1)
                else -> TvPanelFocus(index) to TvPanelEffect.Leave
            }
            TvKeyAction.Right -> when {
                item is TvSegment -> {
                    val at = item.options.indexOfFirst { it.key == item.selectedKey }
                    if (at in 0 until item.options.size - 1) TvPanelFocus(index) to TvPanelEffect.Choose(item.key, item.options[at + 1].key)
                    else TvPanelFocus(index) to TvPanelEffect.None
                }
                item is TvRow && item.adjustable -> TvPanelFocus(index) to TvPanelEffect.Adjust(item.key, 1)
                item is TvRow && focus.sub < item.actions.size -> TvPanelFocus(index, focus.sub + 1) to TvPanelEffect.None
                else -> TvPanelFocus(index, focus.sub) to TvPanelEffect.None
            }
            else -> focus to TvPanelEffect.None
        }
    }

    private fun header(panel: TvPanel, sub: Int, key: TvKeyAction, hasItems: Boolean): Pair<TvPanelFocus, TvPanelEffect> {
        val last = if (panel.back) 1 else 0
        val at = sub.coerceIn(0, last)
        return when (key) {
            TvKeyAction.Down -> (if (hasItems) TvPanelFocus(0) else TvPanelFocus(-1, at)) to TvPanelEffect.None
            TvKeyAction.Select -> TvPanelFocus(-1, at) to TvPanelEffect.Activate(if (panel.back && at == 0) HEADER_BACK else HEADER_CLOSE)
            TvKeyAction.Left -> if (at > 0) TvPanelFocus(-1, at - 1) to TvPanelEffect.None else TvPanelFocus(-1, 0) to TvPanelEffect.Leave
            TvKeyAction.Right -> TvPanelFocus(-1, (at + 1).coerceAtMost(last)) to TvPanelEffect.None
            else -> TvPanelFocus(-1, at) to TvPanelEffect.None
        }
    }

    // Select on a segment moves to the next option, wrapping, so the choice can be made with one key too.
    private fun next(segment: TvSegment): TvPanelEffect {
        val at = segment.options.indexOfFirst { it.key == segment.selectedKey }
        val target = segment.options.getOrNull((at + 1) % segment.options.size.coerceAtLeast(1)) ?: return TvPanelEffect.None
        return TvPanelEffect.Choose(segment.key, target.key)
    }
}

/** What the Episodes (or Lessons) panel knows about the video in play. */
data class TvPlayingNow(
    val assetId: String,
    val playing: Boolean,
    val positionSeconds: Double,
    val durationSeconds: Double,
    val nextAssetId: String? = null,
)

/** A subtitle or audio track the player reports, in the panel's terms. [id] is the player's own handle for it. */
data class TvAudioTrack(
    val id: String,
    val language: String,
    val mimeType: String? = null,
    val channels: Int = 0,
    val selected: Boolean = false,
    val commentary: Boolean = false,
    val description: Boolean = false,
)

data class TvSubtitleTrack(
    val id: String,
    val language: String,
    val selected: Boolean = false,
    val hearingImpaired: Boolean = false,
    val forced: Boolean = false,
)

/** Bookmark lists: loading state and what the last save did. */
data class TvBookmarkView(val loaded: Boolean = false, val error: Boolean = false, val confirmDeleteId: String? = null)

object TvPanels {
    // ---- Episodes and Lessons ----

    /** "20 episodes" for the Episodes panel, "12 lessons" for Lessons (one reads in the singular). */
    fun countLabel(count: Int, lessons: Boolean): String {
        val noun = if (lessons) "lesson" else "episode"
        return "$count $noun" + if (count == 1) "" else "s"
    }

    /**
     * One row of the Episodes panel. The current row reads "Playing · 2:23 of 22:41" (or "Paused"); another that was left
     * part-way reads "Paused · 4:10 of 22:00"; the next one carries the "Up next" chip.
     */
    fun episodeRow(item: TvEpisode, now: TvPlayingNow): TvRow {
        val current = item.assetId.equals(now.assetId, ignoreCase = true)
        val sep = TvPlayerWords.SEP
        val sub = when {
            current -> (if (now.playing) TvPlayerWords.PLAYING else TvPlayerWords.PAUSED) + sep +
                TvPlayerLogic.formatTime(now.positionSeconds) + " of " + TvPlayerLogic.formatTime(now.durationSeconds)
            item.inProgress -> TvPlayerWords.PAUSED + sep + TvPlayerLogic.formatTime(item.positionSeconds ?: 0.0) +
                " of " + TvPlayerLogic.formatTime(item.durationSeconds ?: 0.0)
            else -> listOf(item.duration, item.description).filter { it.isNotBlank() }.joinToString(sep)
        }
        return TvRow(
            key = "ep:" + item.assetId,
            title = (item.number?.let { "$it$sep" }.orEmpty()) + item.title,
            sub = sub.ifBlank { null },
            chip = if (!current && now.nextAssetId != null && item.assetId.equals(now.nextAssetId, ignoreCase = true)) TvPlayerWords.UP_NEXT else null,
            imageUrl = item.stillUrl,
            showImage = true,
            current = current,
        )
    }

    fun episodes(seasons: List<TvSeason>, seasonIndex: Int, lessons: Boolean, now: TvPlayingNow, autoplay: Boolean): TvPanel {
        val season = seasons.getOrNull(seasonIndex.coerceIn(0, (seasons.size - 1).coerceAtLeast(0)))
        val items = season?.items.orEmpty()
        val body = buildList<TvItem> {
            // The Season dropdown is for Episodes only; Left and Right (or Select) change the Season.
            if (!lessons && season != null) {
                add(TvRow("season", season.title + (if (seasons.size > 1) " ▾" else ""), trailing = countLabel(items.size, false), adjustable = seasons.size > 1))
            } else if (lessons) {
                add(TvNote(countLabel(items.size, true)))
            }
            items.forEach { add(episodeRow(it, now)) }
        }
        val current = items.firstOrNull { it.assetId.equals(now.assetId, ignoreCase = true) }
        return TvPanel(
            id = TvPanelId.Episodes,
            title = if (lessons) TvPlayerWords.LESSONS else TvPlayerWords.EPISODES,
            body = body,
            footer = listOf(TvSwitch("autoplay", TvPlayerWords.AUTOPLAY, autoplay)),
            focusKey = current?.let { "ep:" + it.assetId } ?: items.firstOrNull()?.let { "ep:" + it.assetId },
        )
    }

    // ---- Audio & subtitles ----

    private val CODEC_NAMES = mapOf(
        "audio/eac3-joc" to "Dolby Atmos", "audio/eac3" to "Dolby Digital Plus", "audio/ac3" to "Dolby Digital",
        "audio/true-hd" to "Dolby TrueHD", "audio/vnd.dts" to "DTS", "audio/vnd.dts.hd" to "DTS-HD", "audio/mp4a-latm" to "AAC",
        "audio/mpeg" to "MP3", "audio/flac" to "FLAC", "audio/opus" to "Opus", "audio/vorbis" to "Vorbis",
    )

    /** "Mono", "Stereo", "5.1", "7.1" or "N channels"; empty when the count is not known. */
    fun channelsLabel(channels: Int): String = when {
        channels == 1 -> "Mono"
        channels == 2 -> "Stereo"
        channels == 6 -> "5.1"
        channels == 8 -> "7.1"
        channels > 0 -> "$channels channels"
        else -> ""
    }

    /** "Dolby Digital · 5.1": the track's facts under its language. */
    fun audioFacts(track: TvAudioTrack): String =
        listOf(CODEC_NAMES[track.mimeType?.lowercase()].orEmpty(), channelsLabel(track.channels)).filter { it.isNotEmpty() }
            .joinToString(TvPlayerWords.SEP)

    /** "English", "English · Director commentary" or "English · Audio description". */
    fun audioName(track: TvAudioTrack): String = when {
        track.commentary -> track.language + TvPlayerWords.SEP + TvPlayerWords.DIRECTOR_COMMENTARY
        track.description -> track.language + TvPlayerWords.SEP + TvPlayerWords.AUDIO_DESCRIPTION
        else -> track.language
    }

    /** "English", "English · SDH" or "English · Forced". */
    fun subtitleName(track: TvSubtitleTrack): String {
        var name = track.language
        if (track.hearingImpaired) name += TvPlayerWords.SEP + "SDH"
        if (track.forced) name += TvPlayerWords.SEP + "Forced"
        return name
    }

    /**
     * Audio rows (only when there are several tracks, commentary and audio description last), then Subtitles starting with Off,
     * and the footer rows Subtitle appearance and (only when a source is ready and the profile may use it) Find subtitles….
     */
    fun audioSubtitles(audio: List<TvAudioTrack>, subtitles: List<TvSubtitleTrack>, canFind: Boolean): TvPanel {
        val body = buildList<TvItem> {
            if (audio.size > 1) {
                add(TvLabel(TvPlayerWords.AUDIO))
                (audio.filter { !it.commentary && !it.description } + audio.filter { it.commentary || it.description }).forEach {
                    add(TvRow("audio:" + it.id, audioName(it), sub = audioFacts(it).ifBlank { null }, checked = it.selected))
                }
            }
            add(TvLabel(TvPlayerWords.SUBTITLES))
            add(TvRow("sub:off", TvPlayerWords.OFF, checked = subtitles.none { it.selected }))
            subtitles.forEach { add(TvRow("sub:" + it.id, subtitleName(it), checked = it.selected)) }
        }
        val footer = buildList<TvItem> {
            add(TvRow("appearance", TvPlayerWords.SUBTITLE_APPEARANCE, trailing = "›"))
            if (canFind) add(TvRow("find", FindAnotherWording.FIND_SUBTITLES_ENTRY, trailing = "›"))
        }
        val selected = subtitles.firstOrNull { it.selected }?.let { "sub:" + it.id } ?: "sub:off"
        return TvPanel(TvPanelId.AudioSubtitles, TvPlayerWords.AUDIO_SUBTITLES, body = body, footer = footer, focusKey = selected)
    }

    fun subtitleAppearance(style: String, background: String): TvPanel = TvPanel(
        id = TvPanelId.SubtitleAppearance,
        title = TvPlayerWords.SUBTITLE_APPEARANCE,
        back = true,
        body = buildList {
            add(TvLabel(TvPlayerWords.STYLE))
            TvPlayerLogic.APPEARANCE_STYLES.forEach { add(TvRow("style:$it", it, checked = it == style)) }
            add(TvLabel(TvPlayerWords.BACKGROUND))
            TvPlayerLogic.APPEARANCE_BACKGROUNDS.forEach { add(TvRow("background:$it", it, checked = it == background)) }
            add(TvNote(TvPlayerWords.CHANGES_SAVED))
        },
        footer = listOf(TvRow("reset-appearance", TvPlayerWords.RESET_TO_DEFAULT)),
        focusKey = "style:$style",
    )

    // ---- Bookmarks ----

    /** The bookmark rows of one tab, in time order: the time, then the name or note (the maker's name on Global). */
    fun bookmarkRows(items: List<TvBookmark>, tab: TvBookmarkTab, view: TvBookmarkView): List<TvRow> =
        items.sortedBy { it.positionSeconds }.map { b ->
            val words = b.label.ifBlank { b.note.ifBlank { bookmarkAtLabel(b.positionSeconds) } }
            val canChange = tab == TvBookmarkTab.Mine && b.isYours
            TvRow(
                key = "bm:" + b.id,
                title = TvPlayerLogic.formatTime(b.positionSeconds),
                sub = if (tab == TvBookmarkTab.Global && b.ownerName.isNotBlank()) words + TvPlayerWords.SEP + b.ownerName else words,
                imageUrl = b.frameUrl,
                showImage = true,
                // Delete asks once: Delete, then Delete or Keep. There is no Edit here: renaming needs typing.
                actions = when {
                    !canChange -> emptyList()
                    view.confirmDeleteId == b.id -> listOf(TvChoice("delete-yes", TvPlayerWords.DELETE), TvChoice("delete-keep", TvPlayerWords.KEEP))
                    else -> listOf(TvChoice("delete", TvPlayerWords.DELETE))
                },
            )
        }

    fun bookmarkAtLabel(positionSeconds: Double): String =
        TvPlayerWords.fill(TvPlayerWords.BOOKMARK_AT, TvPlayerLogic.formatTime(positionSeconds))

    /**
     * Mine | Global: Mine first and the default; the choice shows only when a Global set exists (the Engine marks which are
     * which with isYours and inEveryone). "Bookmark at 7:42" saves at once with no note.
     */
    fun bookmarks(items: List<TvBookmark>, tab: TvBookmarkTab, positionSeconds: Double, view: TvBookmarkView, ticksOn: Boolean): TvPanel {
        val mine = items.filter { it.isYours }
        val global = items.filter { it.inEveryone }
        val showChoice = global.isNotEmpty()
        val shown = if (showChoice && tab == TvBookmarkTab.Global) TvBookmarkTab.Global else TvBookmarkTab.Mine
        val rows = bookmarkRows(if (shown == TvBookmarkTab.Global) global else mine, shown, view)
        val body = buildList<TvItem> {
            if (showChoice) {
                add(
                    TvSegment(
                        "tab",
                        listOf(
                            TvChoice(TvBookmarkTab.Mine.key, TvPlayerWords.MINE + TvPlayerWords.SEP + mine.size),
                            TvChoice(TvBookmarkTab.Global.key, TvPlayerWords.GLOBAL + TvPlayerWords.SEP + global.size),
                        ),
                        shown.key,
                    ),
                )
            }
            add(TvRow("add", bookmarkAtLabel(positionSeconds), primary = true))
            if (view.error) add(TvNote(TvPlayerWords.COULD_NOT_SAVE))
            if (rows.isEmpty()) {
                if (view.loaded) add(TvNote(if (shown == TvBookmarkTab.Global) TvPlayerWords.NO_GLOBAL else TvPlayerWords.NO_MINE))
            } else {
                rows.forEach { add(it) }
                add(TvNote(TvPlayerWords.SELECT_BOOKMARK))
            }
        }
        return TvPanel(
            id = TvPanelId.Bookmarks,
            title = TvPlayerWords.BOOKMARKS,
            body = body,
            footer = listOf(TvSwitch("ticks", TvPlayerWords.SHOW_ON_TIMELINE, ticksOn)),
            focusKey = "add",
        )
    }

    /** Where each bookmark sits on the timeline, as a percentage; one past the end or with no length is left out. */
    fun bookmarkTicks(items: List<TvBookmark>, durationSeconds: Double): List<Double> {
        if (!(durationSeconds > 0.0)) return emptyList()
        return items.map { it.positionSeconds / durationSeconds * 100.0 }.filter { it in 0.0..100.0 }
    }

    // ---- Playback settings ----

    private fun secondsLabel(seconds: Int) = "$seconds s"

    fun playbackSettings(speed: Double, skip: TvSkip): TvPanel = TvPanel(
        id = TvPanelId.PlaybackSettings,
        title = TvPlayerWords.PLAYBACK_SETTINGS,
        body = listOf(
            TvRow("speed", TvPlayerWords.PLAYBACK_SPEED, trailing = TvPlayerLogic.speedLabel(speed) + " ›"),
            TvRow("skip-back", TvPlayerWords.SKIP_BACK, trailing = secondsLabel(skip.back) + " ›"),
            TvRow("skip-forward", TvPlayerWords.SKIP_FORWARD, trailing = secondsLabel(skip.forward) + " ›"),
            TvRow("info", TvPlayerWords.PLAYBACK_INFORMATION, trailing = "›"),
        ),
        footer = listOf(TvRow("reset-preferences", TvPlayerWords.RESET_PREFERENCES)),
        focusKey = "speed",
    )

    /** Playback speed: the presets (ticked only when the speed equals one) and a fine row that Left and Right step by 0.05. */
    fun speed(speed: Double): TvPanel = TvPanel(
        id = TvPanelId.Speed,
        title = TvPlayerWords.PLAYBACK_SPEED,
        back = true,
        body = buildList {
            add(TvLabel(TvPlayerWords.CURRENT_SPEED))
            add(TvRow("speed-fine", TvPlayerLogic.speedLabel(speed), trailing = "◂ ▸", adjustable = true))
            TvPlayerLogic.SPEED_PRESETS.forEach { add(TvRow("preset:$it", TvPlayerLogic.speedLabel(it), checked = TvPlayerLogic.clampSpeed(speed) == it)) }
        },
        footer = listOf(TvRow("reset-speed", TvPlayerWords.RESET_SPEED)),
        focusKey = "speed-fine",
    )

    fun skipChoices(forward: Boolean, current: Int): TvPanel = TvPanel(
        id = if (forward) TvPanelId.SkipForward else TvPanelId.SkipBack,
        title = if (forward) TvPlayerWords.SKIP_FORWARD else TvPlayerWords.SKIP_BACK,
        back = true,
        body = (if (forward) TvPlayerLogic.SKIP_FORWARD_CHOICES else TvPlayerLogic.SKIP_BACK_CHOICES)
            .map { TvRow("seconds:$it", secondsLabel(it), checked = it == current) },
        focusKey = "seconds:$current",
    )

    fun playbackInformation(lines: List<Pair<String, String>>): TvPanel = TvPanel(
        id = TvPanelId.PlaybackInformation,
        title = TvPlayerWords.PLAYBACK_INFORMATION,
        back = true,
        body = lines.map { TvInfoLine(it.first, it.second) },
    )

    // ---- Chapters ----

    /** The chapter panel of an audiobook: the identity, a row per chapter and the Chapter | Whole book choice of the timeline. */
    fun chapters(chapters: List<Chapter>, positionSeconds: Double, durationSeconds: Double?, wholeBook: Boolean): TvPanel {
        val current = TvChapters.indexAt(chapters, positionSeconds)
        val percent = if (durationSeconds != null && durationSeconds > 0.0) Math.round(positionSeconds / durationSeconds * 100.0).toInt().coerceIn(0, 100) else 0
        val body = buildList<TvItem> {
            if (chapters.isNotEmpty()) {
                add(TvLabel(TvPlayerWords.fill(TvPlayerWords.CHAPTER_OF, current + 1, chapters.size)))
                add(TvNote(TvPlayerWords.fill(TvPlayerWords.BOOK_COMPLETE, percent)))
            }
            chapters.forEachIndexed { i, c ->
                val end = TvChapters.endOf(chapters, i, durationSeconds)
                val finished = end != null && positionSeconds >= end
                add(
                    TvRow(
                        key = "ch:$i",
                        title = c.title.ifBlank { "Chapter ${i + 1}" },
                        sub = when {
                            i == current && end != null -> TvPlayerWords.fill(
                                TvPlayerWords.ELAPSED_REMAINING,
                                TvPlayerLogic.formatTime(positionSeconds - c.startSeconds),
                                TvPlayerLogic.formatTime(end - positionSeconds),
                            )
                            finished -> TvPlayerWords.FINISHED
                            else -> end?.let { TvPlayerLogic.formatTime(it - c.startSeconds) }
                        },
                        checked = finished,
                        current = i == current,
                    ),
                )
            }
            add(TvNote(TvPlayerWords.SELECT_CHAPTER))
        }
        return TvPanel(
            id = TvPanelId.Chapters,
            title = TvPlayerWords.CHAPTERS,
            body = body,
            footer = listOf(
                TvLabel(TvPlayerWords.TIMELINE_SHOWS),
                TvSegment(
                    "timeline",
                    listOf(TvChoice("chapter", TvPlayerWords.CHAPTER), TvChoice("book", TvPlayerWords.WHOLE_BOOK)),
                    if (wholeBook) "book" else "chapter",
                ),
            ),
            focusKey = "ch:${current.coerceAtLeast(0)}",
        )
    }
}
