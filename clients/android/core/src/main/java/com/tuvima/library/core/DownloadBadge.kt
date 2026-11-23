package com.tuvima.library.core

// The small "Downloaded" mark on a poster or a Details page: shown when this title's own file is on the phone and still plays. A show,
// season, Series, album or playlist is a group of files, not one, so it gets no mark (it would be wrong when only part is kept). Pure Kotlin.

object DownloadBadgeLogic {
    const val LABEL = "Downloaded"

    private val containerWords = listOf("show", "season", "series", "album", "collection", "playlist", "universe", "person")

    /** The files that are on the phone now and play: complete, not expired, not a photo. */
    fun heldAssetIds(records: List<DownloadRecord>, nowMs: Long): Set<String> =
        records.filter { it.isComplete && !it.isPhoto && !OfflineLogic.isExpired(it, nowMs) && it.filePath != null }
            .map { it.assetId }
            .toSet()

    /** True for the entity types (a Details route's first word) that are one file: a movie, an episode, an audiobook, a song. */
    fun isSingleFile(entityType: String): Boolean {
        val type = entityType.lowercase()
        return containerWords.none { it in type }
    }

    /** True when the mark belongs on a tile or page of [entityType] whose play file is [assetId]. */
    fun showsBadge(entityType: String, assetId: String?, held: Set<String>): Boolean =
        assetId != null && isSingleFile(entityType) && assetId in held

    /**
     * True when a card's own file is on the phone. The Engine gives a card an `assetId` of its own only when the card is one file (a
     * movie, an episode, a song, an audiobook); a show, Series, album or collection card has none, however its Play button resolves.
     * The route an episode or song card opens (its show or album) says nothing about that, so the route is not used.
     */
    fun cardShowsBadge(cardAssetId: String?, held: Set<String>): Boolean = cardAssetId != null && cardAssetId in held

    /**
     * The kind to keep a download under when it was asked for on a page of [routeType]: a show page's play file is an episode and an
     * album page's is a song, so the download is recorded as that (and gets the Downloaded mark), not as the container.
     */
    fun recordedType(routeType: String): String = when (routeType.lowercase()) {
        "tvshow", "show", "tv_show", "tv-show" -> "tvEpisode"
        "musicalbum", "album" -> "musicTrack"
        else -> routeType
    }

    /**
     * True when the Details page of [routeType] should show the mark for [assetId]: the page is one file, or the copy on the phone
     * was kept as one file ([heldType], the type the download is recorded under; see [recordedType]).
     */
    fun pageShowsBadge(routeType: String, assetId: String?, heldType: String?, held: Set<String>): Boolean =
        assetId != null && assetId in held && (isSingleFile(routeType) || (heldType != null && isSingleFile(heldType)))
}
