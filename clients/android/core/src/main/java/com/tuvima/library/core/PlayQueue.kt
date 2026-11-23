package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject

// The music player's queue and transport rules without a screen (spec native-apps WP10b): what a Play on an album, a Songs list or a search
// result hands to the media service, how Previous behaves, the repeat cycle, and the rows of the Queue panel in the contract's words. The
// media service keeps the real queue (a Media3 playlist); these rules decide what goes into it and what the panel shows.

/** One song (or audiobook) handed to the media service. [durationText] is the Engine's own text ("3:41") and is only shown. */
data class QueueTrack(
    val assetId: String,
    val title: String,
    val artist: String? = null,
    val album: String? = null,
    val artworkUrl: String? = null,
    val durationText: String? = null,
    /** The Engine's work id for this file, when the list said so (used to hang bookmarks and to open the item). */
    val workId: String? = null,
)

/** What a Play press asks for: these songs, from [startIndex], optionally shuffled, under a source name ("Northern Lights", "Songs"). */
data class PlayQueueRequest(
    val tracks: List<QueueTrack>,
    val startIndex: Int = 0,
    val shuffle: Boolean = false,
    val sourceName: String? = null,
)

object PlayQueue {
    /** The longest queue one press hands over. The service resolves each song's stream, so a very long list is cut off at the end. */
    const val MAX_TRACKS = 100

    private const val F_ASSET = "a"
    private const val F_TITLE = "t"
    private const val F_ARTIST = "r"
    private const val F_ALBUM = "l"
    private const val F_ART = "w"
    private const val F_LENGTH = "d"
    private const val F_WORK = "k"
    private const val F_TRACKS = "tracks"
    private const val F_START = "start"
    private const val F_SHUFFLE = "shuffle"
    private const val F_SOURCE = "source"

    /**
     * A request cleaned for the service: songs without a file are dropped (the start index follows the song it pointed at), the list is cut at
     * [MAX_TRACKS] around the start, and a start index outside the list becomes the first song.
     */
    fun normalise(request: PlayQueueRequest): PlayQueueRequest {
        val pointed = request.tracks.getOrNull(request.startIndex)
        val kept = request.tracks.filter { it.assetId.isNotBlank() }
        if (kept.isEmpty()) return request.copy(tracks = emptyList(), startIndex = 0)
        val at = (if (pointed != null) kept.indexOfFirst { it === pointed }.takeIf { it >= 0 } else null) ?: 0
        if (kept.size <= MAX_TRACKS) return request.copy(tracks = kept, startIndex = at)
        // Keep a window that holds the start: from the start onward first, then what comes before it.
        val from = at.coerceAtMost(kept.size - MAX_TRACKS)
        return request.copy(tracks = kept.subList(from, from + MAX_TRACKS), startIndex = at - from)
    }

    /** The request as text for an Intent extra. */
    fun toJson(request: PlayQueueRequest): String {
        val tracks = JSONArray()
        request.tracks.forEach { track ->
            val item = JSONObject().put(F_ASSET, track.assetId).put(F_TITLE, track.title)
            track.artist?.let { item.put(F_ARTIST, it) }
            track.album?.let { item.put(F_ALBUM, it) }
            track.artworkUrl?.let { item.put(F_ART, it) }
            track.durationText?.let { item.put(F_LENGTH, it) }
            track.workId?.let { item.put(F_WORK, it) }
            tracks.put(item)
        }
        val json = JSONObject().put(F_TRACKS, tracks).put(F_START, request.startIndex).put(F_SHUFFLE, request.shuffle)
        request.sourceName?.let { json.put(F_SOURCE, it) }
        return json.toString()
    }

    /** The request from [toJson]; null when the text is not one (nothing is guessed from a damaged extra). */
    fun fromJson(text: String?): PlayQueueRequest? {
        if (text.isNullOrBlank()) return null
        return try {
            val json = JSONObject(text)
            val array = json.optJSONArray(F_TRACKS) ?: return null
            val tracks = (0 until array.length()).mapNotNull { index ->
                val item = array.optJSONObject(index) ?: return@mapNotNull null
                val asset = item.optString(F_ASSET).takeIf { it.isNotBlank() } ?: return@mapNotNull null
                QueueTrack(
                    assetId = asset,
                    title = item.optString(F_TITLE),
                    artist = item.optionalString(F_ARTIST),
                    album = item.optionalString(F_ALBUM),
                    artworkUrl = item.optionalString(F_ART),
                    durationText = item.optionalString(F_LENGTH),
                    workId = item.optionalString(F_WORK),
                )
            }
            PlayQueueRequest(tracks, json.optInt(F_START, 0).coerceIn(0, (tracks.size - 1).coerceAtLeast(0)), json.optBoolean(F_SHUFFLE), json.optionalString(F_SOURCE))
        } catch (error: org.json.JSONException) {
            null
        }
    }

    /** The song's line under its title in a list and on the lock screen: artist, then album. */
    fun secondLine(track: QueueTrack): String? =
        listOfNotNull(track.artist, track.album).map(String::trim).filter(String::isNotEmpty).joinToString(" · ").takeIf { it.isNotEmpty() }

    // ---- Songs list header and album pages ----

    const val PLAY_LABEL = "Play"
    const val SHUFFLE_LABEL = "Shuffle"

    /** A song card of a list as a queue entry; null when the card names no file to play. */
    fun trackOf(card: DisplayCard): QueueTrack? {
        val asset = card.playableAssetId() ?: return null
        return QueueTrack(
            assetId = asset,
            title = card.title,
            artist = card.artist ?: card.subtitle,
            album = card.album,
            artworkUrl = card.squareArtUrl ?: card.posterUrl ?: card.artworkUrl,
            durationText = card.duration,
            workId = card.workId,
        )
    }

    /** The songs of a Details page (an album, a playlist), in the order the page lists them; parts of an audiobook and anything not a song are left out. */
    fun tracksOf(groups: List<DetailChildGroup>, albumTitle: String?, artist: String?, artworkUrl: String?): List<QueueTrack> =
        groups.flatMap { it.children }
            .filter { it.entityType == "musicTrack" }
            .map { child ->
                QueueTrack(
                    assetId = child.assetId,
                    title = child.title,
                    artist = child.subtitle ?: artist,
                    album = albumTitle,
                    artworkUrl = child.artworkUrl ?: artworkUrl,
                    workId = child.workId,
                )
            }

    /** The Songs list's Play header plays the shown songs from the first; Shuffle plays them in a random order. */
    fun playAll(tracks: List<QueueTrack>, shuffle: Boolean, sourceName: String?): PlayQueueRequest? =
        normalise(PlayQueueRequest(tracks, 0, shuffle, sourceName)).takeIf { it.tracks.isNotEmpty() }
}

/** The three repeat states (the contract: Repeat off, Repeat all, Repeat one) and the order one press goes through. */
object RepeatCycle {
    fun next(mode: TvRepeatMode): TvRepeatMode = when (mode) {
        TvRepeatMode.Off -> TvRepeatMode.All
        TvRepeatMode.All -> TvRepeatMode.One
        TvRepeatMode.One -> TvRepeatMode.Off
    }

    /** The button's name for a screen reader: "Repeat off", "Repeat all" or "Repeat one". */
    fun label(mode: TvRepeatMode): String = when (mode) {
        TvRepeatMode.Off -> "Repeat off"
        TvRepeatMode.All -> "Repeat all"
        TvRepeatMode.One -> "Repeat one"
    }
}

/** What Previous does in music. */
enum class PreviousAction { Restart, GoToPrevious }

object MusicPrevious {
    /**
     * After 3 seconds of play it restarts the current song. Within the first 3 seconds (or when pressed again within 3 seconds of a restart,
     * which is the same place) it goes to the previous song. At the start of the queue it restarts.
     */
    fun decide(positionSeconds: Double, hasPrevious: Boolean): PreviousAction = when {
        positionSeconds > TvPlayerLogic.MUSIC_RESTART_SECONDS -> PreviousAction.Restart
        hasPrevious -> PreviousAction.GoToPrevious
        else -> PreviousAction.Restart
    }

    /** Previous is never dimmed in music while the song can be restarted; at the very start of the first song there is nothing to do. */
    fun canAct(positionSeconds: Double, hasPrevious: Boolean): Boolean = TvPlayerLogic.musicCanPrevious(positionSeconds, hasPrevious)
}

/** One row of the Queue panel. [index] is its place in the queue (0 is the first song). */
data class QueueRow(
    val index: Int,
    val assetId: String,
    val title: String,
    val line: String?,
    val artworkUrl: String?,
    val isCurrent: Boolean,
    /** The "..." menu of an upcoming row, in the contract's order; empty on the Now playing row and on History. */
    val actions: List<QueueRowAction> = emptyList(),
)

enum class QueueRowAction(val label: String) {
    PlayNext("Play next"),
    MoveUp("Move up"),
    MoveDown("Move down"),
    Remove("Remove"),
}

enum class QueueTab { UpNext, History }

/** What the panel shows: the two tab labels, the rows of the open tab and the footer ("Playing from {source}"). */
data class QueueView(
    val upNextLabel: String,
    val historyLabel: String,
    val tab: QueueTab,
    val nowPlaying: QueueRow?,
    val rows: List<QueueRow>,
    val canClear: Boolean,
    val footer: String?,
)

object QueueLogic {
    const val UP_NEXT = "Up next · {0}"
    const val HISTORY = "History"
    const val CLEAR = "Clear"
    const val NOW_PLAYING = "Now playing"
    const val PLAYING_FROM = "Playing from"

    /**
     * The panel for [tracks] with the song at [currentIndex] playing. Up next lists what follows, History what already played (most recent
     * first). Move up is not offered on the first upcoming row, Move down not on the last, and Play next not on the row that is already next.
     * Clear removes the upcoming songs and never stops the current one.
     */
    fun view(tracks: List<QueueTrack>, currentIndex: Int, tab: QueueTab, sourceName: String?): QueueView {
        val current = currentIndex.coerceIn(-1, tracks.size - 1)
        val upcoming = if (current < 0) emptyList() else tracks.indices.filter { it > current }
        val rowOf: (Int) -> QueueRow = { i ->
            val track = tracks[i]
            QueueRow(i, track.assetId, track.title, PlayQueue.secondLine(track), track.artworkUrl, isCurrent = i == current)
        }
        val now = if (current >= 0) rowOf(current) else null
        val rows = when (tab) {
            QueueTab.UpNext -> upcoming.mapIndexed { position, i ->
                rowOf(i).copy(
                    actions = buildList {
                        if (position > 0) add(QueueRowAction.PlayNext)
                        if (position > 0) add(QueueRowAction.MoveUp)
                        if (position < upcoming.size - 1) add(QueueRowAction.MoveDown)
                        add(QueueRowAction.Remove)
                    },
                )
            }
            QueueTab.History -> (0 until current.coerceAtLeast(0)).reversed().map(rowOf)
        }
        return QueueView(
            upNextLabel = TvPlayerWords.fill(UP_NEXT, upcoming.size),
            historyLabel = HISTORY,
            tab = tab,
            nowPlaying = now,
            rows = rows,
            canClear = upcoming.isNotEmpty(),
            footer = sourceName?.trim()?.takeIf { it.isNotEmpty() }?.let { "$PLAYING_FROM · $it" },
        )
    }

    /** The index a row's Play next moves it to: right after the song in play. */
    fun playNextTarget(currentIndex: Int): Int = currentIndex + 1
}
