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
}
