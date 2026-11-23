package com.tuvima.library.core

// Downloading many titles at once: a whole season or show, an album or playlist, every part of an audiobook. The Details page already
// lists the owned files (see DetailChildren), so this only decides which of them to queue and what to say. Pure Kotlin.

/** Why [OfflineBatchLogic.plan] stopped before the end of the list. */
enum class BatchStop { None, StorageCap, ListFull, TooMany }

/** One choice in the "what to download" list: everything, or one season or group. */
data class BatchScope(val key: String, val label: String, val children: List<DetailChild>)

/**
 * What to queue from a list: the [toQueue] files, how many were [alreadyHeld] (on the phone or already coming), and why it stopped
 * early, if it did.
 */
data class BatchPlan(val toQueue: List<DetailChild>, val alreadyHeld: Int, val total: Int, val stop: BatchStop)

/** The words one queued title is kept under (the Downloads list shows them without the Engine). */
data class DownloadName(val title: String, val subtitle: String?)

object OfflineBatchLogic {
    /** The most one press queues; the storage limit and the Engine's list limit usually stop it sooner. */
    const val MAX_BATCH = 200

    /** All distinct files of the page, in page order. */
    fun allChildren(groups: List<DetailChildGroup>): List<DetailChild> = groups.flatMap { it.children }.distinctBy { it.assetId }

    /** True when the page lists more than one file to keep, so a "download all" choice is worth showing. */
    fun offersBatch(groups: List<DetailChildGroup>): Boolean = allChildren(groups).size > 1

    /**
     * The choices, best first: everything on the page, then each group when there is more than one (each season of a show). One
     * group offers only "All". The wording names the thing downloaded ("episodes", "songs", "parts").
     */
    fun scopes(groups: List<DetailChildGroup>): List<BatchScope> {
        val withFiles = groups.filter { it.children.isNotEmpty() }
        val all = allChildren(withFiles)
        if (all.isEmpty()) return emptyList()
        val result = mutableListOf(BatchScope("all", "All ${all.size} ${noun(all, all.size)}", all))
        if (withFiles.size > 1) {
            withFiles.forEach { group ->
                result.add(BatchScope(group.key, "${group.title} (${group.children.size} ${noun(group.children, group.children.size)})", group.children))
            }
        }
        return result
    }

    /** The plural (or single) word for what these files are. */
    fun noun(children: List<DetailChild>, count: Int): String {
        val types = children.map { it.entityType }.distinct()
        val one = count == 1
        return when (types.singleOrNull()) {
            "tvEpisode" -> if (one) "episode" else "episodes"
            "musicTrack" -> if (one) "song" else "songs"
            "audiobook" -> if (one) "part" else "parts"
            "movie" -> if (one) "film" else "films"
            else -> if (one) "title" else "titles"
        }
    }

    /**
     * Chooses which of [children] to queue. Files already held are skipped. It stops at the storage limit (counting [estimateFor]
     * for every file already chosen), at the Engine's list limit ([maxListed] entries of any kind, [listedCount] already used), and
     * at [maxBatch].
     */
    fun plan(
        children: List<DetailChild>,
        held: Set<String>,
        committedBytes: Long,
        capBytes: Long,
        listedCount: Int,
        estimateFor: (DetailChild) -> Long,
        maxListed: Int = OfflineLogic.MAX_LISTED,
        maxBatch: Int = MAX_BATCH,
    ): BatchPlan {
        val chosen = mutableListOf<DetailChild>()
        var reserved = 0L
        var alreadyHeld = 0
        var stop = BatchStop.None
        for (child in children.distinctBy { it.assetId }) {
            if (child.assetId in held) {
                alreadyHeld += 1
                continue
            }
            if (chosen.size >= maxBatch) {
                stop = BatchStop.TooMany
                break
            }
            if (listedCount + chosen.size >= maxListed) {
                stop = BatchStop.ListFull
                break
            }
            val estimate = estimateFor(child)
            if (!OfflineLogic.canQueue(committedBytes + reserved, capBytes, estimate)) {
                stop = BatchStop.StorageCap
                break
            }
            chosen.add(child)
            reserved += estimate.coerceAtLeast(0L)
        }
        return BatchPlan(chosen, alreadyHeld, children.distinctBy { it.assetId }.size, stop)
    }

    /**
     * The title and second line a queued file is kept under. An audiobook is named after the book (its chapters are not downloads),
     * with "Part 2 of 3" when the book is several files. An episode says its season and number. A song says its album.
     */
    fun nameFor(parentTitle: String?, child: DetailChild, partIndex: Int, partCount: Int): DownloadName = when (child.entityType) {
        "audiobook" -> DownloadName(
            title = parentTitle?.takeIf(String::isNotBlank) ?: child.title,
            subtitle = if (partCount > 1) "Part ${partIndex + 1} of $partCount" else child.subtitle,
        )
        "tvEpisode" -> DownloadName(child.title, episodeLine(child))
        "musicTrack" -> DownloadName(child.title, child.subtitle ?: parentTitle?.takeIf(String::isNotBlank))
        else -> DownloadName(child.title, child.subtitle)
    }

    private fun episodeLine(child: DetailChild): String? {
        val season = child.groupKey.removePrefix("season-").toIntOrNull()
        val number = child.trackNumber?.toIntOrNull()
        return OfflineLogic.episodeLabel(season, number) ?: child.groupTitle.takeIf { it.isNotBlank() && !it.equals("episodes", true) }
    }

    /** The words shown after a batch was asked for (a line of text, never a toast). */
    fun message(plan: BatchPlan, nounWord: String): String {
        val queued = plan.toQueue.size
        val limit = when (plan.stop) {
            BatchStop.StorageCap -> " Stopped at the storage limit; delete a download or raise the limit in Settings."
            BatchStop.ListFull -> " Stopped because the server tracks no more downloads for this phone."
            BatchStop.TooMany -> " That is the most to start at once; start the rest again."
            BatchStop.None -> ""
        }
        val held = if (plan.alreadyHeld > 0) " ${plan.alreadyHeld} already in your downloads." else ""
        return when {
            plan.total == 0 -> "There is nothing here to download."
            queued == 0 && plan.stop == BatchStop.None -> "Everything here is already in your downloads."
            queued == 0 -> "Nothing was started.$limit"
            else -> "Started $queued $nounWord. Track them under Downloads.$held$limit"
        }
    }
}
