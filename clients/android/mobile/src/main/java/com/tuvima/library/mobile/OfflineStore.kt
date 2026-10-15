package com.tuvima.library.mobile

import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.OfflineProgressEntry
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ReportKind
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * What one person's phone holds for offline use, kept in three small JSON files under [root]: the downloads, the
 * progress recorded while playing them, and the reports the Engine's device download list has not received yet.
 * The files live in the app's private no-backup folder. Nothing is ever written to History from here; offline
 * progress is only kept until it can be replayed.
 */
internal class OfflineStore(val root: File) {
    private val lock = Any()
    private val indexFile = File(root, "index.json")
    private val progressFile = File(root, "progress.json")
    private val reportsFile = File(root, "reports.json")
    private val recordState = MutableStateFlow(readRecords())
    private var progressEntries: List<OfflineProgressEntry> = readProgress()
    private var reportQueue: List<PendingReport> = readReports()

    /** The downloads, observable by the Downloads screen. */
    val records: StateFlow<List<DownloadRecord>> get() = recordState

    fun snapshot(): List<DownloadRecord> = recordState.value

    fun record(assetId: String): DownloadRecord? = recordState.value.firstOrNull { it.assetId == assetId }

    fun upsert(record: DownloadRecord) {
        synchronized(lock) {
            recordState.value = recordState.value.filterNot { it.assetId == record.assetId } + record
            persistRecords()
        }
    }

    /** Changes one record and saves. Returns the new record, or null when it is gone. */
    fun update(assetId: String, transform: (DownloadRecord) -> DownloadRecord): DownloadRecord? = synchronized(lock) {
        val current = recordState.value.firstOrNull { it.assetId == assetId } ?: return@synchronized null
        val next = transform(current)
        recordState.value = recordState.value.map { if (it.assetId == assetId) next else it }
        persistRecords()
        next
    }

    /** Changes one record for the screen only; used for download progress, which is rebuilt from the file on resume. */
    fun updateTransient(assetId: String, transform: (DownloadRecord) -> DownloadRecord) {
        synchronized(lock) {
            val current = recordState.value.firstOrNull { it.assetId == assetId } ?: return
            val next = transform(current)
            recordState.value = recordState.value.map { if (it.assetId == assetId) next else it }
        }
    }

    fun remove(assetId: String): DownloadRecord? = synchronized(lock) {
        val current = recordState.value.firstOrNull { it.assetId == assetId } ?: return@synchronized null
        recordState.value = recordState.value.filterNot { it.assetId == assetId }
        persistRecords()
        current
    }

    // ---- Offline progress ----

    fun progress(): List<OfflineProgressEntry> = synchronized(lock) { progressEntries }

    fun progress(assetId: String): OfflineProgressEntry? =
        synchronized(lock) { progressEntries.firstOrNull { it.assetId == assetId } }

    fun saveProgress(entry: OfflineProgressEntry) {
        synchronized(lock) {
            progressEntries = OfflineLogic.recordProgress(progressEntries, entry)
            persistProgress()
        }
    }

    /** Keeps a position read from the Engine as the resume point when nothing was recorded here yet. */
    fun seedProgress(entry: OfflineProgressEntry) {
        synchronized(lock) {
            if (progressEntries.any { it.assetId == entry.assetId }) return
            progressEntries = progressEntries + entry
            persistProgress()
        }
    }

    /**
     * Stores the result of replaying [sent]. When more was played since (a newer recording), that newer recording
     * stays pending and only takes the revision the Engine now holds.
     */
    fun completeReplay(sent: OfflineProgressEntry, updated: OfflineProgressEntry) {
        synchronized(lock) {
            val current = progressEntries.firstOrNull { it.assetId == sent.assetId } ?: return
            val next = if (current.recordedAtMs == sent.recordedAtMs) updated else current.copy(revision = updated.revision)
            progressEntries = progressEntries.map { if (it.assetId == sent.assetId) next else it }
            persistProgress()
        }
    }

    fun removeProgress(assetId: String) {
        synchronized(lock) {
            progressEntries = progressEntries.filterNot { it.assetId == assetId }
            persistProgress()
        }
    }

    // ---- Reports for the Engine's device download list ----

    fun reports(): List<PendingReport> = synchronized(lock) { reportQueue }

    fun addReport(report: PendingReport) {
        synchronized(lock) {
            reportQueue = OfflineLogic.queueReport(reportQueue, report)
            persistReports()
        }
    }

    fun removeReport(report: PendingReport) {
        synchronized(lock) {
            reportQueue = reportQueue.filterNot { it == report }
            persistReports()
        }
    }

    // ---- Files ----

    fun mediaFile(variantId: String, extension: String): File = File(File(root, "media"), "$variantId.$extension")

    fun artworkFile(assetId: String): File = File(File(root, "art"), "$assetId.img")

    /** Deletes the media (finished or partial) and the saved artwork of a download. */
    fun deleteFiles(record: DownloadRecord) {
        record.filePath?.let { File(it).delete() }
        record.artworkPath?.let { File(it).delete() }
    }

    /** Removes everything this store holds: used when the phone signs out. */
    fun clearAll() {
        synchronized(lock) {
            root.deleteRecursively()
            recordState.value = emptyList()
            progressEntries = emptyList()
            reportQueue = emptyList()
        }
    }

    // ---- Reading and writing ----

    private fun persistRecords() = writeAtomically(indexFile, JSONArray().also { array ->
        recordState.value.forEach { array.put(recordToJson(it)) }
    }.toString())

    private fun persistProgress() = writeAtomically(progressFile, JSONArray().also { array ->
        progressEntries.forEach { array.put(progressToJson(it)) }
    }.toString())

    private fun persistReports() = writeAtomically(reportsFile, JSONArray().also { array ->
        reportQueue.forEach { array.put(reportToJson(it)) }
    }.toString())

    private fun writeAtomically(target: File, text: String) {
        root.mkdirs()
        val temporary = File(root, target.name + ".tmp")
        temporary.writeText(text, Charsets.UTF_8)
        if (!temporary.renameTo(target)) {
            target.delete()
            temporary.renameTo(target)
        }
    }

    // A missing or unreadable file reads as empty: the phone then simply holds no downloads it can vouch for, and the
    // Engine's list is the record that matters; the media files stay on disk until a person deletes them.
    private fun readRecords(): List<DownloadRecord> = readArray(indexFile) { json ->
        DownloadRecord(
            assetId = json.getString("asset_id"),
            variantId = json.readString("variant_id"),
            title = json.optString("title"),
            subtitle = json.readString("subtitle"),
            artworkUrl = json.readString("artwork_url"),
            artworkPath = json.readString("artwork_path"),
            entityType = json.optString("entity_type", "work"),
            showId = json.readString("show_id"),
            profileKey = json.optString("profile_key", OfflineLogic.QUALITY_STANDARD),
            state = runCatching { DownloadState.valueOf(json.optString("state")) }.getOrDefault(DownloadState.Failed),
            bytesDone = json.optLong("bytes_done"),
            totalBytes = json.optLong("total_bytes"),
            storedAtMs = json.readLong("stored_at_ms"),
            expiresAtMs = json.readLong("expires_at_ms"),
            filePath = json.readString("file_path"),
            reportedToServer = json.optBoolean("reported"),
            variantRequested = json.optBoolean("variant_requested"),
            error = json.readString("error"),
            queuedAtMs = json.optLong("queued_at_ms"),
        )
    }

    private fun readProgress(): List<OfflineProgressEntry> = readArray(progressFile) { json ->
        OfflineProgressEntry(
            assetId = json.getString("asset_id"),
            positionSeconds = json.optDouble("position_seconds", 0.0),
            durationSeconds = if (json.has("duration_seconds")) json.optDouble("duration_seconds") else null,
            progressPct = json.optDouble("progress_pct", 0.0),
            recordedAtMs = json.optLong("recorded_at_ms"),
            pending = json.optBoolean("pending"),
            revision = json.optLong("revision"),
        )
    }

    private fun readReports(): List<PendingReport> = readArray(reportsFile) { json ->
        PendingReport(
            kind = runCatching { ReportKind.valueOf(json.optString("kind")) }.getOrDefault(ReportKind.Store),
            assetId = json.getString("asset_id"),
            variantId = json.getString("variant_id"),
            storedBytes = json.optLong("stored_bytes"),
            storedAtMs = json.optLong("stored_at_ms"),
        )
    }

    private fun <T> readArray(file: File, mapper: (JSONObject) -> T): List<T> {
        if (!file.isFile) return emptyList()
        return runCatching {
            val array = JSONArray(file.readText(Charsets.UTF_8))
            buildList { for (index in 0 until array.length()) array.optJSONObject(index)?.let { add(mapper(it)) } }
        }.getOrDefault(emptyList())
    }

    private fun recordToJson(record: DownloadRecord): JSONObject = JSONObject()
        .put("asset_id", record.assetId)
        .putOpt("variant_id", record.variantId)
        .put("title", record.title)
        .putOpt("subtitle", record.subtitle)
        .putOpt("artwork_url", record.artworkUrl)
        .putOpt("artwork_path", record.artworkPath)
        .put("entity_type", record.entityType)
        .putOpt("show_id", record.showId)
        .put("profile_key", record.profileKey)
        .put("state", record.state.name)
        .put("bytes_done", record.bytesDone)
        .put("total_bytes", record.totalBytes)
        .putOpt("stored_at_ms", record.storedAtMs)
        .putOpt("expires_at_ms", record.expiresAtMs)
        .putOpt("file_path", record.filePath)
        .put("reported", record.reportedToServer)
        .put("variant_requested", record.variantRequested)
        .putOpt("error", record.error)
        .put("queued_at_ms", record.queuedAtMs)

    private fun progressToJson(entry: OfflineProgressEntry): JSONObject = JSONObject()
        .put("asset_id", entry.assetId)
        .put("position_seconds", entry.positionSeconds)
        .putOpt("duration_seconds", entry.durationSeconds)
        .put("progress_pct", entry.progressPct)
        .put("recorded_at_ms", entry.recordedAtMs)
        .put("pending", entry.pending)
        .put("revision", entry.revision)

    private fun reportToJson(report: PendingReport): JSONObject = JSONObject()
        .put("kind", report.kind.name)
        .put("asset_id", report.assetId)
        .put("variant_id", report.variantId)
        .put("stored_bytes", report.storedBytes)
        .put("stored_at_ms", report.storedAtMs)
}

private fun JSONObject.readString(name: String): String? = if (has(name) && !isNull(name)) getString(name) else null

private fun JSONObject.readLong(name: String): Long? = if (has(name) && !isNull(name)) getLong(name) else null
