package com.tuvima.library.core

// Bookmarks of a downloaded video or audiobook while the phone has no connection. The list the Engine had when the download finished
// is kept on the phone, a bookmark added offline waits in a queue, and the sync sends the queue once there is a connection. Position
// (progress) already works the same way (OfflineProgressEntry). Music has no bookmarks on the phone. Pure Kotlin.

/** One bookmark added while offline, waiting to be sent. [localId] is only for this phone's queue. */
data class PendingBookmark(
    val localId: String,
    val workId: String,
    val assetId: String,
    val positionSeconds: Double,
    val durationSeconds: Double?,
    val recordedAtMs: Long,
)

object OfflineBookmarkLogic {
    /** Two bookmarks of the same file closer than this are the same moment. */
    private const val SAME_MOMENT_SECONDS = 1.0

    const val SAVED_ON_PHONE_LABEL = "Saved on this phone"

    /** Only videos and audiobooks have bookmarks on the phone (no Bookmarks for music). */
    fun keepsBookmarks(entityType: String): Boolean {
        val type = entityType.lowercase()
        if ("audiobook" in type) return true
        return OfflineLogic.downloadKindFor(entityType) == DownloadKind.Video
    }

    /** Adds [entry] to the queue; a second press at the same moment of the same file replaces the first. */
    fun queue(queue: List<PendingBookmark>, entry: PendingBookmark): List<PendingBookmark> =
        queue.filterNot { it.assetId == entry.assetId && kotlin.math.abs(it.positionSeconds - entry.positionSeconds) < SAME_MOMENT_SECONDS } + entry

    /** The queue in the order the bookmarks were made, so the Engine receives them as they happened. */
    fun sendOrder(queue: List<PendingBookmark>): List<PendingBookmark> = queue.sortedWith(compareBy({ it.recordedAtMs }, { it.localId }))

    /**
     * What to do with a queued bookmark after the Engine answered. Any 2xx is done. A refusal that will not change (the title is gone
     * or not allowed for this person) is dropped; a missing connection, 401, 408, 429 and server errors are tried again later.
     */
    fun classify(statusCode: Int): ReportOutcome = when {
        statusCode in 200..299 -> ReportOutcome.Done
        statusCode in 400..499 && statusCode != 401 && statusCode != 408 && statusCode != 429 -> ReportOutcome.Drop
        else -> ReportOutcome.Retry
    }

    /** The Engine's request body for a queued bookmark: the same one a bookmark made online sends. */
    fun body(entry: PendingBookmark) =
        TvBookmarks.createBody(entry.assetId, entry.positionSeconds, entry.durationSeconds)

    /**
     * The list to show offline: the Engine's list as it was when last read, plus the bookmarks waiting to be sent for [workId] (they
     * are the person's own and say they are saved on this phone), by position.
     */
    fun forDisplay(cached: List<TvBookmark>, pending: List<PendingBookmark>, workId: String): List<TvBookmark> {
        val waiting = pending.filter { it.workId == workId }.map {
            TvBookmark(
                id = "offline-${it.localId}",
                positionSeconds = it.positionSeconds,
                label = SAVED_ON_PHONE_LABEL,
                note = "",
                frameUrl = null,
                ownerName = "",
                isYours = true,
                inEveryone = false,
            )
        }
        return (cached + waiting).sortedBy { it.positionSeconds }
    }

    /** True when a bookmark id is one made offline that has not reached the Engine (it can be removed from the queue, not from the Engine). */
    fun isWaiting(bookmarkId: String): Boolean = bookmarkId.startsWith("offline-")

    fun localIdOf(bookmarkId: String): String? = bookmarkId.removePrefix("offline-").takeIf { isWaiting(bookmarkId) && it.isNotEmpty() }
}
