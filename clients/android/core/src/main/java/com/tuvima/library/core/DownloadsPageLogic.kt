package com.tuvima.library.core

// What the Downloads page shows and says, as pure rules: the sections, the one-line status of each title, the offline note and the
// "pause all / resume all" choices. The screen only draws what these return.

/** Where a title sits on the Downloads page. */
enum class DownloadSection(val title: String) {
    /** Queued, preparing, downloading, paused or failed: still on its way, with progress and Pause, Resume or Retry. */
    InProgress("Downloading"),

    /** Finished and playable without a connection. */
    Ready("On this phone"),

    /** Finished, but its copy has expired: it no longer plays and can be downloaded again or removed. */
    Expired("Expired"),
}

data class DownloadSectionList(val section: DownloadSection, val records: List<DownloadRecord>)

object DownloadsPageLogic {
    /**
     * The non-empty sections in page order. Titles on their way keep the order they were asked for, so the one now downloading is
     * at the top; finished ones show the newest first.
     */
    fun sections(records: List<DownloadRecord>, nowMs: Long): List<DownloadSectionList> {
        val inProgress = records.filter { !it.isComplete }.sortedBy { it.queuedAtMs }
        val finished = records.filter { it.isComplete }
        val ready = finished.filterNot { OfflineLogic.isExpired(it, nowMs) }.sortedByDescending { it.storedAtMs ?: it.queuedAtMs }
        val expired = finished.filter { OfflineLogic.isExpired(it, nowMs) }.sortedByDescending { it.storedAtMs ?: it.queuedAtMs }
        return listOf(
            DownloadSectionList(DownloadSection.InProgress, inProgress),
            DownloadSectionList(DownloadSection.Ready, ready),
            DownloadSectionList(DownloadSection.Expired, expired),
        ).filter { it.records.isNotEmpty() }
    }

    /** What kind of thing a title is, in a word ("Movie", "Episode", "Song", "Audiobook", "Photo"). */
    fun typeLabel(record: DownloadRecord): String {
        if (record.isPhoto) return "Photo"
        val type = record.entityType.lowercase()
        return when {
            "audiobook" in type -> "Audiobook"
            "episode" in type -> "Episode"
            "movie" in type -> "Movie"
            "track" in type || "song" in type || "music" in type || "album" in type -> "Song"
            "show" in type || type.startsWith("tv") -> "Episode"
            else -> "Video"
        }
    }

    /**
     * One line saying where a download is: size, quality and expiry once finished, otherwise its progress or what it waits for.
     * [wifiOnly] is the person's Wi-Fi-only choice, which changes what a queued title says it is waiting for.
     */
    fun statusLine(record: DownloadRecord, nowMs: Long, wifiOnly: Boolean): String {
        val separator = " · "
        val done = OfflineLogic.formatBytes(record.bytesDone)
        val progress = if (record.totalBytes > 0L) "$done of ${OfflineLogic.formatBytes(record.totalBytes)}" else done
        return when (record.state) {
            DownloadState.Complete -> listOfNotNull(
                OfflineLogic.formatBytes(record.bytesDone),
                OfflineLogic.qualityLabel(record.profileKey),
                OfflineLogic.expiryLabel(record.expiresAtMs, nowMs),
            ).joinToString(separator)
            DownloadState.Queued -> record.error ?: OfflineStorageLogic.waitingWords(wifiOnly)
            DownloadState.Preparing -> "Preparing the copy on your server"
            DownloadState.Downloading -> "Downloading $progress"
            DownloadState.Paused -> record.error ?: ("Paused" + separator + progress)
            DownloadState.Failed -> record.error ?: "The download failed"
        }
    }

    /** True when a title can be paused now (it is waiting or moving). */
    fun canPause(record: DownloadRecord): Boolean =
        record.state == DownloadState.Queued || record.state == DownloadState.Preparing || record.state == DownloadState.Downloading

    /** True when a title can be resumed or retried now. */
    fun canResume(record: DownloadRecord): Boolean = record.state == DownloadState.Paused || record.state == DownloadState.Failed

    /** The "Pause all" and "Resume all" buttons: shown only when at least one title can act. */
    fun pauseAllLabel(records: List<DownloadRecord>): String? =
        records.count(::canPause).takeIf { it > 0 }?.let { "Pause all ($it)" }

    fun resumeAllLabel(records: List<DownloadRecord>): String? =
        records.count(::canResume).takeIf { it > 0 }?.let { "Resume all ($it)" }

    /** A line for the top of the page: how many titles and how big. Null for an empty list. */
    fun countLine(records: List<DownloadRecord>, nowMs: Long): String? {
        if (records.isEmpty()) return null
        val ready = records.count { it.isComplete && !OfflineLogic.isExpired(it, nowMs) }
        val active = records.count { !it.isComplete }
        return listOfNotNull(
            if (ready > 0) "$ready ready" else null,
            if (active > 0) "$active on the way" else null,
        ).joinToString(" · ").ifEmpty { null }
    }

    /** What the page says when this phone has no connection; null when it is online. */
    fun offlineNote(online: Boolean, hasReady: Boolean): String? = when {
        online -> null
        hasReady -> "You are offline. Downloaded titles play from this phone, and what you watch or listen to is sent to your server when you are back online."
        else -> "You are offline and nothing is downloaded yet. Connect to download titles for later."
    }

    const val EMPTY_NOTE = "Nothing is downloaded yet. Open a movie, show, album, audiobook or playlist and choose Download to watch " +
        "or listen without a connection, or keep a photo or gallery from View on this phone."

    /** The confirmation for removing everything. */
    fun removeAllWarning(count: Int): String =
        "All $count download${if (count == 1) "" else "s"} will be removed from this phone, including unfinished ones. " +
            "Your library on the server is not changed, and you can download again later."
}
