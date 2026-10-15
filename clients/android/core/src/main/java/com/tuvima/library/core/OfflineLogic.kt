package com.tuvima.library.core

import java.time.Instant
import java.time.LocalDateTime
import java.time.OffsetDateTime
import java.time.ZoneOffset
import java.util.Locale
import kotlin.math.abs

// Pure Kotlin rules behind offline downloads. Nothing here touches Android, the network or org.json, so every rule
// can be checked by plain JVM unit tests. The phone app stores the records, performs the calls and shows the screens;
// this file decides what the records mean.

/** Where one download is in its life. Only [Complete] has a playable file. */
enum class DownloadState { Queued, Preparing, Downloading, Paused, Complete, Failed }

/**
 * What a stored item is, as the Engine's device download list names it (`kind`). A [Variant] is a prepared video or audio
 * copy, a [Photo] is a 2048 px preview of a View photo. [Book] is reserved: the Engine refuses it until books reach the
 * native apps, and nothing in this app creates one yet.
 */
enum class OfflineKind(val wire: String) {
    Variant("variant"),
    Photo("photo"),
    Book("book"),
    ;

    companion object {
        /** The kind a list row names; a row without one (an older Engine) or with one this app does not know reads as a variant. */
        fun fromWire(value: String?): OfflineKind =
            entries.firstOrNull { it.wire.equals(value?.trim(), ignoreCase = true) } ?: Variant
    }
}

/**
 * One title the phone holds, or is getting, for offline use.
 * [bytesDone] is what is on disk now; [totalBytes] is the expected final size (0 until the Engine has said).
 * [reportedToServer] turns true once the Engine's device download list has been told about this variant.
 * For a [OfflineKind.Photo] [assetId] is the View item id, [variantId] stays null and [filePath] is the preview image.
 */
data class DownloadRecord(
    val assetId: String,
    val variantId: String?,
    val title: String,
    val subtitle: String?,
    val artworkUrl: String?,
    val artworkPath: String?,
    val entityType: String,
    val showId: String?,
    val profileKey: String,
    val state: DownloadState,
    val bytesDone: Long,
    val totalBytes: Long,
    val storedAtMs: Long?,
    val expiresAtMs: Long?,
    val filePath: String?,
    val reportedToServer: Boolean,
    val variantRequested: Boolean,
    val error: String?,
    val queuedAtMs: Long,
    val kind: OfflineKind = OfflineKind.Variant,
) {
    val isComplete: Boolean get() = state == DownloadState.Complete
    val isPhoto: Boolean get() = kind == OfflineKind.Photo

    /** 0.0 to 1.0, or null while the size is unknown. */
    fun fraction(): Float? =
        if (totalBytes > 0) (bytesDone.toDouble() / totalBytes.toDouble()).toFloat().coerceIn(0f, 1f) else null
}

/**
 * One row of the Engine's device download list (`GET /api/v1/devices/current/downloads`). A photo row names its View item
 * in [photoItemId] and carries the empty id in [assetId] and [variantId]; a variant row has no [photoItemId].
 */
data class ServerDownloadEntry(
    val assetId: String,
    val variantId: String,
    val storedBytes: Long,
    val storedAtMs: Long?,
    val expiresAtMs: Long?,
    val kind: OfflineKind = OfflineKind.Variant,
    val photoItemId: String? = null,
)

/** One owned episode that follows the one in play (`GET /api/v1/details/show/{id}/next-episodes`). */
data class NextEpisode(
    val workId: String?,
    val assetId: String,
    val title: String,
    val seasonNumber: Int?,
    val episodeNumber: Int?,
    val stillUrl: String?,
)

data class NextEpisodes(val showId: String, val afterAssetId: String, val episodes: List<NextEpisode>)

/**
 * Progress recorded on the phone while playing a downloaded file. [pending] is true until it has been replayed to the
 * Engine; [revision] is the last server revision seen for the asset (0 when none).
 */
data class OfflineProgressEntry(
    val assetId: String,
    val positionSeconds: Double,
    val durationSeconds: Double?,
    val progressPct: Double,
    val recordedAtMs: Long,
    val pending: Boolean,
    val revision: Long,
)

/** The state the Engine answers with after a progress read or write. */
data class ServerProgress(
    val revision: Long,
    val progressPct: Double,
    val lastAccessedMs: Long?,
    val positionSeconds: Double?,
)

/** What the phone must tell the Engine's download list about a variant or photo. */
enum class ReportKind { Store, Delete }

/**
 * One report waiting for the Engine. For a photo ([itemKind] is [OfflineKind.Photo]) both [assetId] and [variantId] hold the
 * View item id, so a newer report about the same photo replaces an older one just as it does for a variant.
 */
data class PendingReport(
    val kind: ReportKind,
    val assetId: String,
    val variantId: String,
    val storedBytes: Long,
    val storedAtMs: Long,
    val itemKind: OfflineKind = OfflineKind.Variant,
)

/** What to do with a queued report after the Engine answered: finished, try again later, or give up on it. */
enum class ReportOutcome { Done, Retry, Drop }

/** Which kind of title a download is, which decides the quality choices. [Excluded] titles (books, comics, photos) are not offered. */
enum class DownloadKind { Video, Audio, Unknown, Excluded }

data class QualityChoice(val key: String, val label: String)

data class ExpiryResult(val keep: List<DownloadRecord>, val purge: List<DownloadRecord>)

/** One photo or video of a gallery that a person may keep on the phone. */
data class PhotoCandidate(val id: String, val isVideo: Boolean)

/** Why [OfflineLogic.planPhotoKeep] stopped before the end of the list. */
enum class PhotoKeepStop { None, StorageCap, ListFull }

/**
 * What to keep from a gallery: the [toKeep] ids (oldest request first), how many were [alreadyKept] or skipped as videos,
 * and why it [stop]ped early, if it did.
 */
data class PhotoKeepPlan(
    val toKeep: List<String>,
    val alreadyKept: Int,
    val skippedVideos: Int,
    val stop: PhotoKeepStop,
)

/**
 * What to do when the Engine refused to list a kept photo because the device already lists as many entries as it allows
 * (409): [drop] is the unreported local copy to remove, and [stop] the photos still waiting or downloading, which stop here
 * (the same refusal would meet each of them).
 */
data class DeviceLimitPlan(val drop: DownloadRecord?, val stop: List<DownloadRecord>)

/**
 * The extension point for books kept on the phone, so a reader can open a local file first. It is deliberately not wired:
 * books are not offered in the native apps yet, nothing creates an [OfflineKind.Book] record, and no reader calls this.
 * When books arrive, a reader should ask [localBookFile] before it goes to the network, and use the file when it answers.
 */
interface LocalBookFiles {
    /** The absolute path of the finished local copy of this book asset on this phone for the person using it, or null when there is none. */
    fun localBookFile(assetId: String): String?
}

object OfflineLogic {
    const val QUALITY_SMALL = "mobile-small"
    const val QUALITY_STANDARD = "mobile-standard"
    const val QUALITY_AUDIO = "audio-mobile"

    /** Kept photos are the 2048 px preview (a fraction of the original's size); originals are not kept. */
    const val PHOTO_PROFILE_KEY = "photo-preview-2048"
    const val PHOTO_ENTITY_TYPE = "photo"
    const val PHOTO_PREVIEW_EDGE = 2048

    /** What one 2048 px preview is assumed to weigh until it is on disk; only used to plan how many to queue. */
    const val PHOTO_ESTIMATE_BYTES = 600L * 1024L

    /** The Engine lists at most this many entries of any kind per device. */
    const val MAX_LISTED = 500

    const val DEVICE_LIMIT_MESSAGE = "This device has reached its offline limit."

    /** Times this close are treated as the same moment, to allow for the Engine rounding a stored time. */
    private const val TIME_TOLERANCE_MS = 1_000L
    private const val POSITION_TOLERANCE_SECONDS = 0.5
    private const val PERCENT_TOLERANCE = 0.5
    private const val DAY_MS = 24L * 60L * 60L * 1000L
    private const val HOUR_MS = 60L * 60L * 1000L

    private val allQualities = listOf(
        QualityChoice(QUALITY_SMALL, "Small (540p)"),
        QualityChoice(QUALITY_STANDARD, "Standard (720p)"),
        QualityChoice(QUALITY_AUDIO, "Audio (96 kbps)"),
    )

    // ---- Storage cap ----

    /** Bytes the downloads hold or have reserved: what is on disk, and for unfinished ones the size still to come. */
    fun committedBytes(records: List<DownloadRecord>): Long = records.sumOf { record ->
        if (record.isComplete) record.bytesDone.coerceAtLeast(0L)
        else maxOf(record.bytesDone, record.totalBytes).coerceAtLeast(0L)
    }

    /**
     * True when a new download may be queued. Blocked when the committed bytes already reach the cap, or when the
     * estimated size (0 when the Engine has not said yet) would take them past it.
     */
    fun canQueue(committedBytes: Long, capBytes: Long, estimatedBytes: Long = 0L): Boolean {
        if (capBytes <= 0L) return false
        if (committedBytes >= capBytes) return false
        return committedBytes + estimatedBytes.coerceAtLeast(0L) <= capBytes
    }

    // ---- Expiry ----

    /**
     * Compares the phone's finished downloads with the Engine's list. A finished download that was reported but whose
     * row is gone has expired on the Engine and is purged; one whose listed expiry has passed is purged too; one still
     * listed takes the Engine's expiry. A download not reported yet cannot be judged by the list and stays unless its own
     * expiry has passed. Downloads still in progress are never touched.
     */
    fun reconcileWithServer(
        local: List<DownloadRecord>,
        server: List<ServerDownloadEntry>,
        nowMs: Long,
    ): ExpiryResult {
        val keep = mutableListOf<DownloadRecord>()
        val purge = mutableListOf<DownloadRecord>()
        for (record in local) {
            // Downloads still in progress are never judged, and neither is a kind this app does not keep yet (books).
            if (!record.isComplete || record.kind == OfflineKind.Book) {
                keep.add(record)
                continue
            }
            val listed = server.firstOrNull { serverEntryMatches(it, record) }
            when {
                record.reportedToServer && listed == null -> purge.add(record)
                listed != null && listed.expiresAtMs != null && listed.expiresAtMs <= nowMs -> purge.add(record)
                !record.reportedToServer && record.expiresAtMs != null && record.expiresAtMs <= nowMs -> purge.add(record)
                listed != null -> keep.add(record.copy(expiresAtMs = listed.expiresAtMs))
                else -> keep.add(record)
            }
        }
        return ExpiryResult(keep, purge)
    }

    /** True when this row of the Engine's list is the one for this local record: same kind, then the photo item or the asset and variant. */
    fun serverEntryMatches(entry: ServerDownloadEntry, record: DownloadRecord): Boolean = when (record.kind) {
        OfflineKind.Photo -> entry.kind == OfflineKind.Photo && entry.photoItemId == record.assetId
        OfflineKind.Variant -> entry.kind == OfflineKind.Variant &&
            entry.assetId == record.assetId && entry.variantId == record.variantId
        OfflineKind.Book -> false
    }

    /** True when a finished download should no longer be played because its expiry has passed. */
    fun isExpired(record: DownloadRecord, nowMs: Long): Boolean =
        record.isComplete && record.expiresAtMs != null && record.expiresAtMs <= nowMs

    // ---- Reporting to the Engine's download list ----

    /**
     * Reads the HTTP status of a report. Any 2xx is success, and 204 is what the Engine really answers. A delete the
     * Engine does not know (404) is already done. A store the Engine refuses (404 unknown or expired variant, 409 the
     * list is full) will never succeed, so it is dropped. Everything else, such as 401, 429 or 5xx, is tried again later.
     */
    fun classifyReport(kind: ReportKind, statusCode: Int): ReportOutcome = when {
        statusCode in 200..299 -> ReportOutcome.Done
        kind == ReportKind.Delete && statusCode == 404 -> ReportOutcome.Done
        kind == ReportKind.Store && (statusCode == 404 || statusCode == 409) -> ReportOutcome.Drop
        statusCode in 400..499 && statusCode != 401 && statusCode != 408 && statusCode != 429 -> ReportOutcome.Drop
        else -> ReportOutcome.Retry
    }

    /** Adds a report to the queue; a newer report for the same variant or photo replaces the older one (store then delete = delete). */
    fun queueReport(queue: List<PendingReport>, report: PendingReport): List<PendingReport> =
        queue.filterNot { it.variantId == report.variantId && it.itemKind == report.itemKind } + report

    /**
     * How many entries to plan against: the phone's own records, or what the Engine's list showed on the last read if that
     * is more. The Engine's limit is per device across every person who used it and its list shows only the current
     * person's rows, so this is still a lower bound; a refusal (409) is the final word.
     */
    fun effectiveListedCount(localCount: Int, engineListCount: Int?): Int =
        maxOf(localCount, engineListCount ?: 0)

    /** True when the Engine refused a store report because the device's list is full (409). */
    fun isDeviceLimitRefusal(kind: ReportKind, statusCode: Int): Boolean =
        kind == ReportKind.Store && statusCode == 409

    /** What follows a full-list refusal for the kept photo [itemId]; only an unreported photo copy is dropped. */
    fun planDeviceLimitReached(records: List<DownloadRecord>, itemId: String): DeviceLimitPlan {
        val drop = records.firstOrNull { it.kind == OfflineKind.Photo && it.assetId == itemId && !it.reportedToServer }
        val stop = records.filter {
            it.kind == OfflineKind.Photo && it.assetId != itemId &&
                (it.state == DownloadState.Queued || it.state == DownloadState.Downloading)
        }
        return DeviceLimitPlan(drop, stop)
    }

    /**
     * The delete reports owed for kept photos that were purged because the Engine's list no longer shows them. The Engine may
     * still hold a hidden row for such a photo (it only hides what the person cannot read), and a row left there would keep
     * counting towards the device's limit; a delete the Engine does not know (404) is already done.
     */
    fun purgeDeleteReports(purged: List<DownloadRecord>, nowMs: Long): List<PendingReport> =
        purged.filter { it.kind == OfflineKind.Photo }.map {
            PendingReport(ReportKind.Delete, it.assetId, it.assetId, 0L, nowMs, OfflineKind.Photo)
        }

    /** True when this report is about this local record (the photo item for a photo, the variant for a variant). */
    fun reportMatches(report: PendingReport, record: DownloadRecord): Boolean = when (report.itemKind) {
        OfflineKind.Photo -> record.kind == OfflineKind.Photo && record.assetId == report.assetId
        OfflineKind.Variant -> record.kind == OfflineKind.Variant && record.variantId == report.variantId
        OfflineKind.Book -> false
    }

    // ---- Keeping photos ----

    /**
     * Chooses which photos of a gallery to keep. Videos are skipped (only a still frame is kept for a photo, which is not a
     * useful copy of a video), as are photos already held and repeats in the list. It stops at the storage cap, counting
     * [estimatePerPhoto] for each photo already chosen, and at the Engine's list limit ([maxListed] entries of any kind,
     * of which [listedCount] are already used). The gallery is read in order, so the newest photos are kept first.
     */
    fun planPhotoKeep(
        candidates: List<PhotoCandidate>,
        heldIds: Set<String>,
        committedBytes: Long,
        capBytes: Long,
        listedCount: Int,
        estimatePerPhoto: Long = PHOTO_ESTIMATE_BYTES,
        maxListed: Int = MAX_LISTED,
    ): PhotoKeepPlan {
        val chosen = LinkedHashSet<String>()
        var alreadyKept = 0
        var skippedVideos = 0
        var stop = PhotoKeepStop.None
        for (candidate in candidates) {
            if (candidate.isVideo) {
                skippedVideos += 1
                continue
            }
            if (candidate.id in heldIds) {
                alreadyKept += 1
                continue
            }
            if (candidate.id in chosen) continue
            if (listedCount + chosen.size >= maxListed) {
                stop = PhotoKeepStop.ListFull
                break
            }
            if (!canQueue(committedBytes + chosen.size * estimatePerPhoto.coerceAtLeast(0L), capBytes, estimatePerPhoto)) {
                stop = PhotoKeepStop.StorageCap
                break
            }
            chosen.add(candidate.id)
        }
        return PhotoKeepPlan(chosen.toList(), alreadyKept, skippedVideos, stop)
    }

    /**
     * True when a finished photo copy may be read straight from the phone: a complete record of a photo whose file is
     * [fileExists]. The viewer asks this before it goes to the network.
     */
    fun localPhotoPath(record: DownloadRecord?, fileExists: (String) -> Boolean): String? {
        if (record == null || record.kind != OfflineKind.Photo || !record.isComplete) return null
        val path = record.filePath ?: return null
        return if (fileExists(path)) path else null
    }

    // ---- Offline progress replay ----

    /** Inserts or replaces the progress for an asset, keeping the revision last seen for it. */
    fun recordProgress(entries: List<OfflineProgressEntry>, entry: OfflineProgressEntry): List<OfflineProgressEntry> {
        val old = entries.firstOrNull { it.assetId == entry.assetId }
        val merged = if (old != null) entry.copy(revision = old.revision) else entry
        return entries.filterNot { it.assetId == entry.assetId } + merged
    }

    /** The entries still to replay, oldest recording first, so the Engine sees them in the order they happened. */
    fun replayOrder(entries: List<OfflineProgressEntry>): List<OfflineProgressEntry> =
        entries.filter { it.pending }.sortedWith(compareBy({ it.recordedAtMs }, { it.assetId }))

    /**
     * True when the Engine kept its own record instead of the one just replayed. A write the Engine took is stamped
     * with the replayed time and carries a newer revision; so the server won when its record is later than the replay,
     * when its revision did not move past the one expected, or (a tie in time) when its position is not the one sent.
     */
    fun isServerWon(sent: OfflineProgressEntry, expectedRevision: Long, server: ServerProgress): Boolean {
        val last = server.lastAccessedMs
        if (last != null && last - sent.recordedAtMs > TIME_TOLERANCE_MS) return true
        if (server.revision <= expectedRevision) return true
        val position = server.positionSeconds
        if (position != null) return abs(position - sent.positionSeconds) > POSITION_TOLERANCE_SECONDS
        return abs(server.progressPct - sent.progressPct) > PERCENT_TOLERANCE
    }

    /** The phone's record after the Engine's own state won: it takes the server's position, percent, time and revision. */
    fun adoptServerState(entry: OfflineProgressEntry, server: ServerProgress, nowMs: Long): OfflineProgressEntry {
        val duration = entry.durationSeconds
        val position = server.positionSeconds
            ?: if (duration != null && duration > 0.0) duration * server.progressPct / 100.0 else entry.positionSeconds
        return entry.copy(
            positionSeconds = position,
            progressPct = server.progressPct,
            recordedAtMs = server.lastAccessedMs ?: nowMs,
            pending = false,
            revision = server.revision,
        )
    }

    /** The phone's record after the Engine took it. */
    fun markReplayed(entry: OfflineProgressEntry, server: ServerProgress): OfflineProgressEntry =
        entry.copy(pending = false, revision = server.revision)

    /** Where a downloaded file starts playing: where it was left, or the start once it was (almost) finished. */
    fun resumeStartMs(entry: OfflineProgressEntry?): Long {
        if (entry == null || entry.positionSeconds <= 0.0 || entry.progressPct >= 95.0) return 0L
        return (entry.positionSeconds * 1000.0).toLong()
    }

    fun progressPercent(positionSeconds: Double, durationSeconds: Double?): Double {
        if (durationSeconds == null || durationSeconds <= 0.0) return 0.0
        return (positionSeconds / durationSeconds * 100.0).coerceIn(0.0, 100.0)
    }

    // ---- Quality and kinds ----

    fun downloadKindFor(entityType: String): DownloadKind {
        val value = entityType.lowercase()
        return when {
            "audiobook" in value || "music" in value || "track" in value || "album" in value || "song" in value ->
                DownloadKind.Audio
            "photo" in value || "book" in value || "comic" in value -> DownloadKind.Excluded
            "movie" in value || "episode" in value || "tv" in value || "show" in value || "video" in value ->
                DownloadKind.Video
            else -> DownloadKind.Unknown
        }
    }

    fun isShowEntity(entityType: String): Boolean = when (entityType.lowercase()) {
        "tvshow", "show", "tv_show", "tv-show" -> true
        else -> false
    }

    fun qualityChoicesFor(kind: DownloadKind): List<QualityChoice> = when (kind) {
        DownloadKind.Video -> allQualities.filter { it.key != QUALITY_AUDIO }
        DownloadKind.Audio -> allQualities.filter { it.key == QUALITY_AUDIO }
        DownloadKind.Unknown -> allQualities
        DownloadKind.Excluded -> emptyList()
    }

    /** The quality to preselect: the person's default when it fits the kind, otherwise the first choice that does. */
    fun defaultQualityFor(kind: DownloadKind, preferred: String): String {
        val choices = qualityChoicesFor(kind)
        return choices.firstOrNull { it.key == preferred }?.key
            ?: choices.firstOrNull { it.key == QUALITY_STANDARD }?.key
            ?: choices.firstOrNull()?.key
            ?: preferred
    }

    fun qualityLabel(profileKey: String): String =
        if (profileKey == PHOTO_PROFILE_KEY) "Preview (2048 px)"
        else allQualities.firstOrNull { it.key == profileKey }?.label ?: profileKey

    /**
     * The offline variant to download: the known one when resuming, otherwise the best ready variant for the profile
     * (the one that lives longest). Null while the Engine is still preparing it.
     */
    fun chooseReadyVariant(variants: List<OfflineVariant>, profileKey: String, knownVariantId: String?): OfflineVariant? {
        val ready = variants.filter { it.status == "ready" }
        if (knownVariantId != null) return ready.firstOrNull { it.id == knownVariantId }
        val matching = ready.filter { it.profileKey == profileKey }.ifEmpty { ready.filter { it.profileKey == null } }
        return matching.maxByOrNull { it.expiresAtMs ?: 0L }
    }

    // ---- Words ----

    fun formatBytes(bytes: Long): String {
        val value = bytes.coerceAtLeast(0L).toDouble()
        val kb = 1024.0
        val mb = kb * 1024.0
        val gb = mb * 1024.0
        return when {
            value >= gb -> String.format(Locale.ROOT, "%.1f GB", value / gb)
            value >= mb -> String.format(Locale.ROOT, "%.0f MB", value / mb)
            value >= kb -> String.format(Locale.ROOT, "%.0f KB", value / kb)
            else -> "${value.toLong()} B"
        }
    }

    /** "Expires in 12 days", "Expires in 5 hours", "Expired"; null when the download never expires. */
    fun expiryLabel(expiresAtMs: Long?, nowMs: Long): String? {
        val expires = expiresAtMs ?: return null
        val remaining = expires - nowMs
        if (remaining <= 0L) return "Expired"
        val days = remaining / DAY_MS
        if (days >= 2L) return "Expires in $days days"
        if (days == 1L) return "Expires in 1 day"
        val hours = remaining / HOUR_MS
        return if (hours >= 1L) "Expires in $hours hours" else "Expires within the hour"
    }

    fun episodeLabel(seasonNumber: Int?, episodeNumber: Int?): String? = when {
        seasonNumber != null && seasonNumber > 0 && episodeNumber != null -> "Season $seasonNumber, episode $episodeNumber"
        seasonNumber == 0 && episodeNumber != null -> "Special $episodeNumber"
        episodeNumber != null -> "Episode $episodeNumber"
        else -> null
    }

    fun formatSeconds(seconds: Double): String = String.format(Locale.ROOT, "%.3f", seconds.coerceAtLeast(0.0))

    // ---- Times ----

    /** Parses an ISO 8601 time with or without an offset (no offset reads as UTC); null when blank or unreadable. */
    fun parseIsoMillis(text: String?): Long? {
        val value = text?.trim()?.takeIf { it.isNotEmpty() } ?: return null
        val withOffset = runCatching { OffsetDateTime.parse(value).toInstant().toEpochMilli() }.getOrNull()
        if (withOffset != null) return withOffset
        return runCatching { LocalDateTime.parse(value).toInstant(ZoneOffset.UTC).toEpochMilli() }.getOrNull()
    }

    /** UTC ISO 8601 with a trailing Z, the form the Engine's `client_timestamp` and `stored_at` read. */
    fun formatIsoUtc(epochMs: Long): String = Instant.ofEpochMilli(epochMs).toString()
}

/** The number of bytes in the storage cap choices shown in Settings. */
object StorageCap {
    const val DEFAULT_BYTES: Long = 10L * 1024L * 1024L * 1024L
    val optionsGb: List<Int> = listOf(2, 5, 10, 20, 50)

    fun bytesForGb(gb: Int): Long = gb.toLong() * 1024L * 1024L * 1024L

    fun gbFor(bytes: Long): Int = (bytes / (1024L * 1024L * 1024L)).toInt()
}
