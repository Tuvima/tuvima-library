package com.tuvima.library.core

// What the Play button of a Details page does when the page was opened without a file (an album from the Albums tab or from search, an audiobook
// from search): the page's own `mediaGroups` name the files, so no new Engine route is needed (spec native-apps WP10c).

/** What Play starts. */
sealed interface DetailPlayTarget {
    /** Nothing on the page can be played from here (a book, or a title whose file the page does not name). */
    data object Nothing : DetailPlayTarget

    /** One file: a song, an audiobook or a video the page was opened with. */
    data class OneFile(val assetId: String) : DetailPlayTarget

    /** An album or playlist: its songs in the page's order, played as one queue named after the page. */
    data class Songs(val tracks: List<QueueTrack>, val sourceName: String?) : DetailPlayTarget
}

object DetailPlay {
    /**
     * An album's (or playlist's) songs win over a single file the page happened to come with, because Play on an album plays the album.
     * Otherwise the page's own file, then an audiobook's file from the page's parts.
     */
    fun resolve(
        assetId: String?,
        children: List<DetailChildGroup>,
        title: String?,
        artist: String?,
        artworkUrl: String?,
    ): DetailPlayTarget {
        val songs = PlayQueue.tracksOf(children, title, artist, artworkUrl).filter { it.assetId.isNotBlank() }
        if (songs.isNotEmpty()) return DetailPlayTarget.Songs(songs, title?.trim()?.takeIf(String::isNotEmpty))
        if (!assetId.isNullOrBlank()) return DetailPlayTarget.OneFile(assetId)
        val book = children.flatMap { it.children }.firstOrNull { it.entityType == "audiobook" }
        return book?.let { DetailPlayTarget.OneFile(it.assetId) } ?: DetailPlayTarget.Nothing
    }

    /** The queue request for Play (or Shuffle) on a page that plays songs; null for any other target. */
    fun request(target: DetailPlayTarget, shuffle: Boolean, random: kotlin.random.Random): PlayQueueRequest? {
        if (target !is DetailPlayTarget.Songs) return null
        val base = PlayQueue.playAll(target.tracks, shuffle = false, sourceName = target.sourceName) ?: return null
        return if (shuffle) QueueShuffle.forShuffleHeader(base, random) else base
    }

    /** Shuffle is offered where more than one song plays. */
    fun offersShuffle(target: DetailPlayTarget): Boolean = target is DetailPlayTarget.Songs && target.tracks.size > 1
}
