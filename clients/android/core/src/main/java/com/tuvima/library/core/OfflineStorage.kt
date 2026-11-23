package com.tuvima.library.core

// Rules for how much a phone may keep and over which connection: free space, the Wi-Fi-only choice and rough sizes used to plan how
// many titles fit. Pure Kotlin (no Android types), so they are checked by plain JVM unit tests. The downloader reads the phone's real
// free space and connection and asks these rules what to do.

/** Which connections a download may use. */
enum class DownloadNetwork { AnyConnection, UnmeteredOnly }

object OfflineStorageLogic {
    /** Kept free on the phone so a download never fills it: a download stops short of this. */
    const val FREE_SPACE_RESERVE_BYTES: Long = 500L * 1024L * 1024L

    const val NOT_ENOUGH_SPACE = "Not enough free space on this phone. Free some space, then Retry."

    private const val MB = 1024L * 1024L

    /** The connection a download waits for: any (mobile data included), or only an unmetered one such as home Wi-Fi. */
    fun networkFor(wifiOnly: Boolean): DownloadNetwork = if (wifiOnly) DownloadNetwork.UnmeteredOnly else DownloadNetwork.AnyConnection

    /** What a queued download says it is waiting for. */
    fun waitingWords(wifiOnly: Boolean): String = if (wifiOnly) "Waiting for Wi-Fi" else "Waiting for a connection"

    /**
     * True when [remainingBytes] more may be written without taking the phone below its reserve. An unknown size (0) only needs the
     * reserve itself to be there.
     */
    fun fitsFreeSpace(freeBytes: Long, remainingBytes: Long): Boolean =
        freeBytes - remainingBytes.coerceAtLeast(0L) >= FREE_SPACE_RESERVE_BYTES

    /** True when a failure's own words say the disk is full (`ENOSPC`: "No space left on device"). */
    fun isOutOfSpace(message: String?): Boolean =
        message != null && message.contains("no space left", ignoreCase = true)

    /**
     * About how big one title is, only to plan how many of a batch fit under the storage limit before any of them is on disk. The
     * downloader checks the real size once the Engine has said it. A music track is small, an audiobook part large, a video depends
     * on the chosen quality.
     */
    fun estimateBytes(entityType: String, profileKey: String): Long {
        val kind = OfflineLogic.downloadKindFor(entityType)
        return when {
            kind == DownloadKind.Audio && entityType.contains("audiobook", ignoreCase = true) -> 150L * MB
            kind == DownloadKind.Audio -> 8L * MB
            profileKey == OfflineLogic.QUALITY_SMALL -> 350L * MB
            else -> 800L * MB
        }
    }

    /** One line for the top of Downloads: what downloads hold of the limit, and how much room the phone itself has left. */
    fun usageLine(committedBytes: Long, capBytes: Long, freeBytes: Long?): String {
        val used = "Downloads use ${OfflineLogic.formatBytes(committedBytes)} of ${OfflineLogic.formatBytes(capBytes)}"
        return if (freeBytes == null) used else "$used · ${OfflineLogic.formatBytes(freeBytes)} free on this phone"
    }

    /** A warning when the phone itself is nearly full, even if the storage limit is not reached; null when there is room. */
    fun lowSpaceNote(freeBytes: Long?): String? =
        if (freeBytes != null && freeBytes < FREE_SPACE_RESERVE_BYTES * 2) {
            "This phone is almost full. New downloads stop when less than ${OfflineLogic.formatBytes(FREE_SPACE_RESERVE_BYTES)} is free."
        } else {
            null
        }
}
