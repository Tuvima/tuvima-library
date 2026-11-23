package com.tuvima.library.core

import kotlin.random.Random
import org.json.JSONObject

// The phone's Playlists (spec native-apps WP19): the profile's own playlists read from `GET /api/v1/collections/playlists` and the songs of one
// from `GET /api/v1/collections/{id}/items` (engineering/specs, docs/reference/api-endpoints.md "Playlists for paired apps"). Read only: playlists
// are made, renamed and changed on the Dashboard. The playlist list carries no artwork address, so a tile's cover comes from the first songs'
// covers. Pure wire reading and rules; nothing here draws anything or touches the network.

/** How a playlist's cover is drawn: a mosaic of its songs' covers, or one picture. The Engine's `cover_mode`. */
enum class PlaylistCoverMode { Mosaic, Picture }

/** One playlist of the profile, as the list route gives it. [isSmart] is a playlist the Engine builds from a rule. */
data class Playlist(
    val id: String,
    val name: String,
    val description: String?,
    val itemCount: Int,
    val totalDurationSeconds: Double?,
    val isPinned: Boolean,
    val isSmart: Boolean,
    val coverMode: PlaylistCoverMode,
)

data class PlaylistsPage(val items: List<Playlist>, val totalCount: Int, val nextCursor: String?, val hasMore: Boolean)

/**
 * One song of a playlist. [assetId] is the file that plays it (null when it has none); [sortOrder] counts from zero across the whole
 * playlist, so it keeps running on the next page.
 */
data class PlaylistSong(
    val id: String,
    val workId: String?,
    val title: String,
    val artist: String?,
    val album: String?,
    val coverUrl: String?,
    val assetId: String?,
    val durationSeconds: Double?,
    val sortOrder: Int,
)

data class PlaylistSongsPage(val items: List<PlaylistSong>, val totalCount: Int, val nextCursor: String?, val hasMore: Boolean)

internal fun parsePlaylist(json: JSONObject): Playlist = Playlist(
    id = json.getString("id"),
    name = json.optString("name"),
    description = json.optionalString("description"),
    itemCount = json.optInt("item_count"),
    totalDurationSeconds = json.optionalDouble("total_duration_seconds"),
    isPinned = json.optBoolean("is_pinned"),
    isSmart = json.optString("membership_mode").equals("Smart", ignoreCase = true),
    coverMode = if (json.optString("cover_mode").equals("picture", ignoreCase = true)) PlaylistCoverMode.Picture else PlaylistCoverMode.Mosaic,
)

internal fun parsePlaylistsPage(json: JSONObject): PlaylistsPage = PlaylistsPage(
    items = json.optJSONArray("items")?.objects(::parsePlaylist).orEmpty(),
    totalCount = json.optInt("total_count"),
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
)

internal fun parsePlaylistSongsPage(json: JSONObject, absolute: (String?) -> String?): PlaylistSongsPage = PlaylistSongsPage(
    items = json.optJSONArray("items")?.objects { item ->
        PlaylistSong(
            id = item.getString("id"),
            workId = item.optionalString("work_id"),
            title = item.optString("title"),
            artist = item.optionalString("creator"),
            album = item.optionalString("album"),
            coverUrl = absolute(item.optionalString("cover_url")),
            assetId = item.optionalString("asset_id"),
            durationSeconds = item.optionalDouble("duration_seconds"),
            sortOrder = item.optInt("sort_order"),
        )
    }.orEmpty(),
    totalCount = json.optInt("total_count"),
    nextCursor = json.optionalString("next_cursor"),
    hasMore = json.optBoolean("has_more"),
)

object PlaylistsLogic {
    /** The playlist list is read 50 at a time (the Engine's own default). */
    const val LIST_PAGE = 50

    /** A playlist's songs are read 200 at a time (the Engine's largest page), so a long one takes few reads. */
    const val SONGS_PAGE = 200

    /** The most pages of songs read for one playlist, so a runaway list can never loop for ever (200 songs each). */
    const val MAX_SONG_PAGES = 50

    /** A tile's cover is drawn from the first songs; four fill a mosaic. */
    const val COVER_SAMPLE = 4

    /** The route a playlist hit of the search overlay carries, and the type the phone gives such a hit. */
    const val SEARCH_TYPE = "playlist"
    private const val ROUTE_MARK = "playlists"
    private const val SYSTEM_PLAYLIST = "system"

    const val PINNED = "Pinned"
    const val SMART = "Smart playlist"
    const val PLAY_LABEL = PlayQueue.PLAY_LABEL
    const val SHUFFLE_LABEL = PlayQueue.SHUFFLE_LABEL
    const val DOWNLOAD_ALL = "Download all"

    const val EMPTY_LIST = "No playlists yet. Playlists are created and changed on the Dashboard; the ones you make there show up here to play."
    const val EMPTY_PLAYLIST = "This playlist has no songs yet. Playlists are changed on the Dashboard; Library only plays them."
    const val OFFLINE_LIST = "You are offline. Playlists need a connection to open, but songs you downloaded play from Downloads."
    const val OFFLINE_PLAYLIST = "You are offline. Only the songs downloaded to this phone play right now."
    const val OFFLINE_NOTHING_HELD = "You are offline and none of this playlist's songs are downloaded to this phone."
    const val NO_FILE = "This song has no file to play."

    /** The short line under a row that has no file (an album row of a smart playlist): it is shown, dimmed, and cannot be played from here. */
    const val NO_FILE_ROW = "No file to play here"
    const val NOT_AVAILABLE = "That playlist is not available."

    /** The tile's second line: "Smart playlist · 12 songs · 48 min", whichever parts are known. */
    fun tileLine(playlist: Playlist): String =
        listOfNotNull(
            SMART.takeIf { playlist.isSmart },
            songCountText(playlist.itemCount).takeIf(String::isNotEmpty) ?: "No songs",
            totalLength(playlist.totalDurationSeconds).takeIf(String::isNotEmpty),
        ).joinToString(" · ")

    /** "1 song" / "1,204 songs"; empty for zero. */
    fun songCountText(count: Int): String =
        if (count <= 0) "" else "${"%,d".format(java.util.Locale.US, count)} ${if (count == 1) "song" else "songs"}"

    /** A whole playlist's length: "48 min", "2 hr 2 min", "3 hr"; empty when unknown or nothing. */
    fun totalLength(seconds: Double?): String {
        if (seconds == null || seconds.isNaN() || seconds <= 0.0) return ""
        val minutes = Math.round(seconds / 60.0).toInt().coerceAtLeast(1)
        val hours = minutes / 60
        val rest = minutes % 60
        return when {
            hours == 0 -> "$minutes min"
            rest == 0 -> "$hours hr"
            else -> "$hours hr $rest min"
        }
    }

    /** One song's length as it is shown in a row: "3:21", "1:01:40"; empty when unknown. */
    fun songLength(seconds: Double?): String {
        if (seconds == null || seconds.isNaN()) return ""
        val total = seconds.toInt().coerceAtLeast(0)
        val hours = total / 3600
        val minutes = (total % 3600) / 60
        val rest = total % 60
        return if (hours > 0) "%d:%02d:%02d".format(java.util.Locale.ROOT, hours, minutes, rest) else "%d:%02d".format(java.util.Locale.ROOT, minutes, rest)
    }

    /** "Artist · Album" under a song's title; whichever is missing is left out and a repeat is not shown twice. */
    fun songLine(song: PlaylistSong): String =
        listOfNotNull(song.artist, song.album).map(String::trim).filter(String::isNotEmpty).distinct().joinToString(" · ")

    /**
     * The header's line under a playlist's name: the song count the Engine gave, and the length once every song is read (the length is the
     * sum of the songs' own lengths, which a page does not give until it is read).
     */
    fun headerLine(totalCount: Int, loaded: List<PlaylistSong>, allLoaded: Boolean, smart: Boolean): String {
        val length = if (allLoaded) totalLength(loaded.sumOf { it.durationSeconds ?: 0.0 }) else ""
        return listOfNotNull(
            SMART.takeIf { smart },
            songCountText(totalCount).takeIf(String::isNotEmpty),
            length.takeIf(String::isNotEmpty),
        ).joinToString(" · ")
    }

    /**
     * The covers a tile draws, taken from the first songs' own covers (the list route has no artwork): a mosaic of four when the playlist
     * is a mosaic and has four distinct covers, otherwise the single first cover. Empty while no song has a cover.
     */
    fun coverLayout(mode: PlaylistCoverMode, songCovers: List<String?>): List<String> {
        val distinct = songCovers.filterNotNull().filter(String::isNotBlank).distinct()
        return when {
            distinct.isEmpty() -> emptyList()
            mode == PlaylistCoverMode.Mosaic && distinct.size >= COVER_SAMPLE -> distinct.take(COVER_SAMPLE)
            else -> listOf(distinct.first())
        }
    }

    /** Adds a page of playlists to those shown, dropping one that arrives twice. */
    fun mergePlaylists(shown: List<Playlist>, incoming: List<Playlist>): List<Playlist> {
        val seen = shown.mapTo(HashSet()) { it.id }
        return shown + incoming.filter { seen.add(it.id) }
    }

    /** The cursor for the next page, or null when the Engine says there is no more. */
    fun cursorFor(page: PlaylistsPage): String? = page.nextCursor.takeIf { page.hasMore }

    fun cursorFor(page: PlaylistSongsPage): String? = page.nextCursor.takeIf { page.hasMore }

    /**
     * Adds a page of songs to those shown, in the order the pages arrive (the Engine sends a playlist in its saved order, and a smart playlist
     * in the order its rule gives, so nothing is sorted again here). A song that arrives twice (the same row in an overlapping page) is
     * shown once, by its id.
     */
    fun mergeSongs(shown: List<PlaylistSong>, incoming: List<PlaylistSong>): List<PlaylistSong> {
        val seen = shown.mapTo(HashSet()) { it.id }
        return shown + incoming.filter { seen.add(it.id) }
    }

    /** True while the playlist has another page of songs worth asking for. */
    fun hasNextSongPage(page: PlaylistSongsPage, pagesRead: Int): Boolean =
        page.hasMore && page.items.isNotEmpty() && pagesRead < MAX_SONG_PAGES

    /**
     * True when more songs exist than the pages read: the Engine still offers a next page, but the page limit stopped the reading. Such a
     * playlist is shown as partial rather than as complete.
     */
    fun isPartial(page: PlaylistSongsPage, continues: Boolean): Boolean = page.hasMore && !continues

    /** The line shown above the songs when only the first part of a very long playlist was read. */
    fun partialNote(shown: Int, total: Int): String =
        "Showing the first ${"%,d".format(java.util.Locale.US, shown)} of ${"%,d".format(java.util.Locale.US, total)} songs."

    /** True when the song names a file the player can start. */
    fun isPlayable(song: PlaylistSong): Boolean = !song.assetId.isNullOrBlank()

    /**
     * A song as a queue entry (the player's own shape, the one a Songs list hands over); null when it has no file. The length is the text the
     * queue shows.
     */
    fun trackOf(song: PlaylistSong): QueueTrack? {
        val asset = song.assetId?.takeIf(String::isNotBlank) ?: return null
        return QueueTrack(
            assetId = asset,
            title = song.title,
            artist = song.artist,
            album = song.album,
            artworkUrl = song.coverUrl,
            durationText = songLength(song.durationSeconds).takeIf(String::isNotEmpty),
            workId = song.workId,
        )
    }

    /**
     * The songs that can start now. While online that is every song with a file; while offline ([held] is the files on the phone) only the
     * downloaded ones, so a queue never reaches a song the phone cannot play.
     */
    fun playableSongs(songs: List<PlaylistSong>, held: Set<String>?): List<PlaylistSong> =
        songs.filter { song -> isPlayable(song) && (held == null || song.assetId in held) }

    /**
     * What a press on a song (or Play, with [pressed] null) starts: the playlist's songs from there in the playlist's order, the way the
     * Songs list does it (up to [PlayQueue.MAX_TRACKS] around the start). Null when no song can play.
     */
    fun playFrom(songs: List<PlaylistSong>, pressed: PlaylistSong?, source: String?, held: Set<String>?): PlayQueueRequest? {
        val playable = playableSongs(songs, held)
        val tracks = playable.mapNotNull(::trackOf)
        if (tracks.isEmpty()) return null
        val start = pressed?.let { p -> playable.indexOfFirst { it.id == p.id } }?.takeIf { it >= 0 } ?: 0
        return PlayQueue.normalise(PlayQueueRequest(tracks, start, shuffle = false, sourceName = clean(source)))
    }

    /**
     * The Shuffle button: a playlist longer than one queue holds is cut to a random [PlayQueue.MAX_TRACKS] of its songs (kept in playlist
     * order, so turning Shuffle off goes back to it), then a random song starts with Shuffle on.
     */
    fun shuffled(songs: List<PlaylistSong>, source: String?, held: Set<String>?, random: Random): PlayQueueRequest? {
        val tracks = playableSongs(songs, held).mapNotNull(::trackOf)
        if (tracks.isEmpty()) return null
        val picked = if (tracks.size <= PlayQueue.MAX_TRACKS) {
            tracks
        } else {
            tracks.indices.shuffled(random).take(PlayQueue.MAX_TRACKS).sorted().map { tracks[it] }
        }
        return QueueShuffle.forShuffleHeader(PlayQueue.normalise(PlayQueueRequest(picked, 0, shuffle = false, sourceName = clean(source))), random)
    }

    /** The playlist's songs as the files "Download all" can queue (the same shape a Details page's songs have). */
    fun downloadChildren(songs: List<PlaylistSong>): List<DetailChild> =
        songs.filter(::isPlayable).map { song ->
            DetailChild(
                workId = song.workId,
                assetId = song.assetId.orEmpty(),
                title = song.title,
                subtitle = song.artist,
                artworkUrl = song.coverUrl,
                entityType = "musicTrack",
                trackNumber = null,
                groupKey = "playlist",
                groupTitle = "Songs",
            )
        }

    /** The words above the songs while offline, or null when online. [heldCount] is how many of the playlist's songs are on the phone. */
    fun offlineNote(online: Boolean, heldCount: Int): String? = when {
        online -> null
        heldCount > 0 -> OFFLINE_PLAYLIST
        else -> OFFLINE_NOTHING_HELD
    }

    /** The playlist a search result's route opens (`/listen/music/playlists/{id}`), or null for any other route (the built-in lists live under `system`). */
    fun idFromRoute(route: String?): String? {
        val path = route?.substringBefore('?')?.substringBefore('#') ?: return null
        val parts = path.split('/').filter(String::isNotBlank)
        val at = parts.indexOf(ROUTE_MARK)
        if (at < 0 || parts.size != at + 2) return null
        return parts[at + 1].takeIf { it != SYSTEM_PLAYLIST }
    }

    private fun clean(source: String?): String? = source?.trim()?.takeIf(String::isNotEmpty)
}
