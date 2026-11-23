package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject

// What the television player reads from the Engine's answers and how it works with it, without a screen: a show's episodes (the
// Episodes panel, Previous and Next), the person's bookmarks, an audiobook's chapters, the subtitle appearance this device
// keeps, and the Fire TV check. The same decisions as the TV web app (player-logic.js): Mine | Global uses the Engine's
// isYours and inEveryone flags and a client never filters by its own profile.

// ---- episodes ----

/** One owned episode of a show, with what its row shows. */
data class TvEpisode(
    val assetId: String,
    val workId: String,
    val season: Int?,
    val number: Int?,
    val title: String,
    val description: String,
    val duration: String,
    val positionSeconds: Double?,
    val durationSeconds: Double?,
    val state: String,
    val stillUrl: String?,
) {
    /** Left part-way: another row reads "Paused · 4:10 of 22:00". */
    val inProgress: Boolean
        get() = state == "InProgress" && (positionSeconds ?: 0.0) > 0.0 && (durationSeconds ?: 0.0) > 0.0
}

data class TvSeason(val key: String, val title: String, val items: List<TvEpisode>)

data class TvNeighbours(val previous: TvEpisode?, val next: TvEpisode?)

object TvEpisodes {
    /**
     * The Seasons of a show's details answer (`sequencePlacement.groups`), owned episodes only. A group that has none is left
     * out, and a group with no title is "Season {number}" from its first episode.
     */
    fun seasons(detail: JSONObject?, absolute: (String?) -> String? = { it }): List<TvSeason> {
        val groups = detail?.optJSONObject("sequencePlacement")?.optJSONArray("groups") ?: return emptyList()
        val seasons = mutableListOf<TvSeason>()
        for (g in 0 until groups.length()) {
            val group = groups.optJSONObject(g) ?: continue
            val items = group.optJSONArray("items").objectsOrEmpty().mapNotNull { episodeOf(it, absolute) }
            if (items.isEmpty()) continue
            val first = items.first().season
            seasons += TvSeason(
                key = group.optionalString("key") ?: g.toString(),
                title = group.optionalString("title")
                    ?: (TvPlayerWords.SEASON + " " + (first ?: (seasons.size + 1))),
                items = items,
            )
        }
        return seasons
    }

    private fun JSONArray?.objectsOrEmpty(): List<JSONObject> =
        if (this == null) emptyList() else (0 until length()).mapNotNull { optJSONObject(it) }

    private fun episodeOf(item: JSONObject, absolute: (String?) -> String?): TvEpisode? {
        val context = item.optJSONObject("episodeContext") ?: return null
        val assetId = context.optionalString("episodeAssetId") ?: return null
        if (item.has("isOwned") && !item.optBoolean("isOwned", true)) return null
        return TvEpisode(
            assetId = assetId,
            workId = context.optionalString("episodeWorkId").orEmpty(),
            season = context.optInt("seasonNumber", 0).takeIf { it > 0 },
            number = context.optInt("episodeNumber", 0).takeIf { it > 0 },
            title = item.optString("title"),
            description = item.optString("description"),
            duration = item.optString("duration"),
            positionSeconds = item.optionalDouble("positionSeconds") ?: context.optionalDouble("positionSeconds"),
            durationSeconds = item.optionalDouble("durationSeconds") ?: context.optionalDouble("durationSeconds"),
            state = context.optString("state"),
            stillUrl = absolute(item.optionalString("episodeStillUrl") ?: item.optionalString("artworkUrl")),
        )
    }

    fun flatten(seasons: List<TvSeason>): List<TvEpisode> = seasons.flatMap { it.items }

    /** The episode before and after the one in play, across Seasons; null where there is none. */
    fun neighbours(seasons: List<TvSeason>, assetId: String): TvNeighbours {
        val all = flatten(seasons)
        val at = all.indexOfFirst { it.assetId.equals(assetId, ignoreCase = true) }
        if (at < 0) return TvNeighbours(null, null)
        return TvNeighbours(all.getOrNull(at - 1), all.getOrNull(at + 1))
    }

    fun find(seasons: List<TvSeason>, assetId: String): TvEpisode? =
        flatten(seasons).firstOrNull { it.assetId.equals(assetId, ignoreCase = true) }

    fun seasonIndexOf(seasons: List<TvSeason>, assetId: String): Int =
        seasons.indexOfFirst { s -> s.items.any { it.assetId.equals(assetId, ignoreCase = true) } }.coerceAtLeast(0)
}

// ---- bookmarks ----

enum class TvBookmarkTab(val key: String) {
    Mine("mine"),
    Global("global"),
    ;

    companion object {
        fun fromKey(key: String?): TvBookmarkTab = entries.firstOrNull { it.key == key } ?: Mine
    }
}

/** One bookmark as the Engine lists it (`AudiobookBookmarkDto`). [frameUrl] is the 320 px still of that moment. */
data class TvBookmark(
    val id: String,
    val positionSeconds: Double,
    val label: String,
    val note: String,
    val frameUrl: String?,
    val ownerName: String,
    val isYours: Boolean,
    val inEveryone: Boolean,
)

object TvBookmarks {
    fun parse(array: JSONArray?, absolute: (String?) -> String? = { it }): List<TvBookmark> {
        if (array == null) return emptyList()
        return (0 until array.length()).mapNotNull { array.optJSONObject(it) }.map { parseOne(it, absolute) }
    }

    fun parseOne(json: JSONObject, absolute: (String?) -> String? = { it }): TvBookmark = TvBookmark(
        id = json.optString("id"),
        positionSeconds = json.optDouble("positionSeconds", 0.0).takeIf { it.isFinite() } ?: 0.0,
        label = json.optString("label"),
        note = json.optString("note"),
        frameUrl = absolute(json.optionalString("frameUrl")),
        ownerName = json.optString("ownerName"),
        isYours = json.optBoolean("isYours"),
        inEveryone = json.optBoolean("inEveryone"),
    )

    /** The body of "Bookmark at {0}": saved at the moment it was pressed, at once, with no note, and never Global. */
    fun createBody(assetId: String, positionSeconds: Double, durationSeconds: Double?): JSONObject {
        val body = JSONObject()
            .put("assetId", assetId)
            .put("positionSeconds", Math.round(positionSeconds.coerceAtLeast(0.0) * 10.0) / 10.0)
            .put("isGlobal", false)
        if (durationSeconds != null && durationSeconds > 0.0) body.put("durationSeconds", Math.round(durationSeconds * 10.0) / 10.0)
        return body
    }
}

// ---- chapters and the timeline ----

/** What the timeline shows: the time played at the left, the time left at the right and how far along the bar is (0 to 1). */
data class TvTimeline(val elapsed: String, val left: String, val fraction: Double)

object TvChapters {
    /** The index of the chapter in play at [positionSeconds] (the last that has started), 0 when none has. */
    fun indexAt(chapters: List<Chapter>, positionSeconds: Double): Int =
        chapters.indexOfLast { it.startSeconds <= positionSeconds + 0.001 }.coerceAtLeast(0)

    fun endOf(chapters: List<Chapter>, index: Int, durationSeconds: Double?): Double? =
        chapters.getOrNull(index)?.let { it.endSeconds ?: chapters.getOrNull(index + 1)?.startSeconds ?: durationSeconds }

    /** Previous chapter: after a few seconds into a chapter it restarts that chapter, otherwise it goes to the one before. */
    fun previousStart(chapters: List<Chapter>, positionSeconds: Double): Double? {
        if (chapters.isEmpty()) return null
        val index = indexAt(chapters, positionSeconds)
        val start = chapters[index].startSeconds
        return if (positionSeconds - start > TvPlayerLogic.MUSIC_RESTART_SECONDS || index == 0) start else chapters[index - 1].startSeconds
    }

    fun nextStart(chapters: List<Chapter>, positionSeconds: Double): Double? =
        chapters.getOrNull(indexAt(chapters, positionSeconds) + 1)?.startSeconds

    fun hasPrevious(chapters: List<Chapter>, positionSeconds: Double): Boolean =
        chapters.isNotEmpty() && (indexAt(chapters, positionSeconds) > 0 || positionSeconds - chapters[0].startSeconds > TvPlayerLogic.MUSIC_RESTART_SECONDS)

    fun hasNext(chapters: List<Chapter>, positionSeconds: Double): Boolean = nextStart(chapters, positionSeconds) != null

    /** The timeline: the whole title, or (an audiobook's "Chapter" choice) the chapter in play. */
    fun timeline(chapters: List<Chapter>, positionSeconds: Double, durationSeconds: Double?, wholeBook: Boolean): TvTimeline {
        if (!wholeBook && chapters.isNotEmpty()) {
            val index = indexAt(chapters, positionSeconds)
            val start = chapters[index].startSeconds
            val end = endOf(chapters, index, durationSeconds)
            if (end != null && end > start) {
                val into = (positionSeconds - start).coerceIn(0.0, end - start)
                return TvTimeline(TvPlayerLogic.formatTime(into), TvPlayerLogic.timeLeft(into, end - start), into / (end - start))
            }
        }
        val fraction = if (durationSeconds != null && durationSeconds > 0.0) (positionSeconds / durationSeconds).coerceIn(0.0, 1.0) else 0.0
        return TvTimeline(TvPlayerLogic.formatTime(positionSeconds), TvPlayerLogic.timeLeft(positionSeconds, durationSeconds), fraction)
    }
}

// ---- subtitle appearance (kept on this device) ----

data class TvAppearance(val style: String, val background: String)

object TvSubtitleAppearance {
    /** The two Subtitle appearance choices: this device's saved pair, else what the profile's size and background imply. */
    fun of(prefs: PlaybackPreferences?, device: TvDeviceChoices?): TvAppearance {
        val p = prefs ?: PlaybackPreferences.Default
        val size = p.subtitleSize.trim().lowercase()
        val style = device?.subtitleStyle?.takeIf { it in TvPlayerLogic.APPEARANCE_STYLES }
            ?: if (size == "large" || size == "extralarge" || size == "extra large") TvPlayerWords.STYLE_LARGE else TvPlayerWords.STYLE_DEFAULT
        val background = device?.subtitleBackground?.takeIf { it in TvPlayerLogic.APPEARANCE_BACKGROUNDS }
            ?: if (p.subtitleBackground) TvPlayerWords.BACKGROUND_BOX else TvPlayerWords.BACKGROUND_NONE
        return TvAppearance(style, background)
    }

    /** The text size as a fraction of the picture height: Large text is the large step, otherwise the profile's own size. */
    fun sizeFraction(appearance: TvAppearance, profileSize: String?): Float =
        if (appearance.style == TvPlayerWords.STYLE_LARGE) SubtitleStyleRules.sizeFraction("large") else SubtitleStyleRules.sizeFraction(profileSize)
}

// ---- what this device is ----

object TvDevice {
    /** Fire TV is Amazon's: the maker says Amazon or the system has the Fire TV feature (`amazon.hardware.fire_tv`). */
    fun isFireTv(manufacturer: String?, hasFireTvFeature: Boolean): Boolean =
        hasFireTvFeature || manufacturer?.trim().equals("Amazon", ignoreCase = true)

    /** Google Cast does not work on Fire TV, so a Cast button is hidden there and unchanged everywhere else. */
    fun castButtonVisible(castAvailable: Boolean, isFireTv: Boolean): Boolean = castAvailable && !isFireTv

    const val FIRE_TV_FEATURE = "amazon.hardware.fire_tv"
}

// ---- playback information ----

object TvPlaybackInformation {
    /** The lines of the Playback information view: the picture, the sound, how it is delivered and the connection. */
    fun lines(manifest: PlaybackManifest?, videoHeight: Int, videoMime: String?, audioMime: String?, channels: Int): List<Pair<String, String>> {
        val sep = TvPlayerWords.SEP
        val lines = mutableListOf<Pair<String, String>>()
        val video = listOf(if (videoHeight > 0) "${videoHeight}p" else "", codecShort(videoMime)).filter { it.isNotEmpty() }.joinToString(sep)
        if (video.isNotEmpty()) lines += "Video" to video
        val audio = listOf(codecShort(audioMime), TvPanels.channelsLabel(channels))
            .filter { it.isNotEmpty() }.joinToString(sep)
        if (audio.isNotEmpty()) lines += TvPlayerWords.AUDIO to audio
        if (manifest != null) lines += "Delivery" to if (manifest.recommendedDelivery == "hls") "Converted" else "Direct"
        return lines
    }

    private fun codecShort(mime: String?): String = mime?.substringAfter('/', "")?.uppercase().orEmpty()
}
