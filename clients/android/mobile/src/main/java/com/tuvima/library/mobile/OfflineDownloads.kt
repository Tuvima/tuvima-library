package com.tuvima.library.mobile

import androidx.work.BackoffPolicy
import androidx.work.Constraints
import androidx.work.ExistingWorkPolicy
import androidx.work.NetworkType
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
import com.tuvima.library.core.DownloadKind
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ReportKind
import com.tuvima.library.core.TuvimaClient
import java.io.File
import java.util.concurrent.TimeUnit

/** What a person asked to download. The title and artwork are kept so the Downloads list reads without the Engine. */
internal data class DownloadRequest(
    val assetId: String,
    val title: String,
    val subtitle: String?,
    val artworkUrl: String?,
    val entityType: String,
    val showId: String?,
    val profileKey: String,
)

internal sealed interface EnqueueResult {
    data object Queued : EnqueueResult
    data class AlreadyHeld(val state: DownloadState) : EnqueueResult
    data class OverCap(val committedBytes: Long, val capBytes: Long) : EnqueueResult
    data object NotOffered : EnqueueResult
    data object NotPaired : EnqueueResult
}

internal data class NextEpisodesResult(
    val found: Int,
    val queued: Int,
    val alreadyHeld: Int,
    val overCap: Boolean,
)

/**
 * The offline downloads of the person using this phone. It owns the queue (WorkManager does the transfers, so they
 * pause, resume with HTTP ranges and survive the app closing), the storage cap, and the reports back to the Engine's
 * device download list. Each person has their own folder, so switching profile never shows or plays another
 * person's downloads. Books and photos are not offered here (they have their own offline work).
 */
internal class OfflineDownloads(private val app: TuvimaMobileApplication) {
    private val stores = mutableMapOf<String, OfflineStore>()
    private val workManager: WorkManager get() = WorkManager.getInstance(app)

    private fun activeProfileId(): String? = app.client?.currentProfileId()

    /** The store of the person using the phone now. Before pairing it is an empty store of its own. */
    fun store(): OfflineStore = storeFor(activeProfileId() ?: UNPAIRED)

    fun storeFor(profileId: String): OfflineStore = synchronized(stores) {
        stores.getOrPut(profileId) {
            OfflineStore(File(File(app.noBackupFilesDir, "offline"), safeName(profileId)))
        }
    }

    /** Queues a download unless it is already held or the storage cap is reached. */
    fun enqueue(request: DownloadRequest): EnqueueResult {
        val profileId = activeProfileId() ?: return EnqueueResult.NotPaired
        if (OfflineLogic.downloadKindFor(request.entityType) == DownloadKind.Excluded) return EnqueueResult.NotOffered
        val store = storeFor(profileId)
        val now = System.currentTimeMillis()
        synchronized(this) {
            val existing = store.record(request.assetId)
            if (existing != null) {
                if (existing.state != DownloadState.Failed && !OfflineLogic.isExpired(existing, now)) {
                    return EnqueueResult.AlreadyHeld(existing.state)
                }
                // A failed or expired download starts fresh.
                workManager.cancelUniqueWork(workName(profileId, existing.assetId))
                store.remove(existing.assetId)
                store.deleteFiles(existing)
            }
            val committed = OfflineLogic.committedBytes(store.snapshot())
            val cap = app.storageCapBytes()
            if (!OfflineLogic.canQueue(committed, cap)) return EnqueueResult.OverCap(committed, cap)
            store.upsert(
                DownloadRecord(
                    assetId = request.assetId,
                    variantId = null,
                    title = request.title,
                    subtitle = request.subtitle,
                    artworkUrl = request.artworkUrl,
                    artworkPath = null,
                    entityType = request.entityType,
                    showId = request.showId,
                    profileKey = request.profileKey,
                    state = DownloadState.Queued,
                    bytesDone = 0L,
                    totalBytes = 0L,
                    storedAtMs = null,
                    expiresAtMs = null,
                    filePath = null,
                    reportedToServer = false,
                    variantRequested = false,
                    error = null,
                    queuedAtMs = now,
                ),
            )
        }
        scheduleDownload(profileId, request.assetId, request.profileKey, ExistingWorkPolicy.REPLACE)
        return EnqueueResult.Queued
    }

    /** Queues the next owned episodes after [afterAssetId] (the Engine decides which, up to three). */
    suspend fun downloadNextEpisodes(
        client: TuvimaClient,
        showId: String,
        afterAssetId: String,
        profileKey: String,
    ): NextEpisodesResult {
        val next = client.nextEpisodes(showId, afterAssetId, 3)
        var queued = 0
        var held = 0
        var overCap = false
        loop@ for (episode in next.episodes) {
            val result = enqueue(
                DownloadRequest(
                    assetId = episode.assetId,
                    title = episode.title.ifBlank { "Episode" },
                    subtitle = OfflineLogic.episodeLabel(episode.seasonNumber, episode.episodeNumber),
                    artworkUrl = episode.stillUrl,
                    entityType = "tvEpisode",
                    showId = showId,
                    profileKey = profileKey,
                ),
            )
            when (result) {
                is EnqueueResult.Queued -> queued += 1
                is EnqueueResult.AlreadyHeld -> held += 1
                is EnqueueResult.OverCap -> {
                    overCap = true
                    break@loop
                }
                is EnqueueResult.NotOffered, is EnqueueResult.NotPaired -> Unit
            }
        }
        return NextEpisodesResult(next.episodes.size, queued, held, overCap)
    }

    /** Stops the transfer and keeps the partial file; Resume continues it with an HTTP range request. */
    fun pause(assetId: String) {
        val profileId = activeProfileId() ?: return
        storeFor(profileId).update(assetId) { record ->
            if (record.state == DownloadState.Queued || record.state == DownloadState.Preparing ||
                record.state == DownloadState.Downloading
            ) {
                record.copy(state = DownloadState.Paused, error = null)
            } else {
                record
            }
        }
        workManager.cancelUniqueWork(workName(profileId, assetId))
    }

    fun resume(assetId: String) {
        val profileId = activeProfileId() ?: return
        val store = storeFor(profileId)
        val record = store.record(assetId) ?: return
        if (record.state != DownloadState.Paused && record.state != DownloadState.Failed) return
        store.update(assetId) { it.copy(state = DownloadState.Queued, error = null) }
        scheduleDownload(profileId, assetId, record.profileKey, ExistingWorkPolicy.REPLACE)
    }

    /** Deletes the file, forgets the progress kept for it and tells the Engine's list (retried later when offline). */
    fun delete(assetId: String) {
        val profileId = activeProfileId() ?: return
        val store = storeFor(profileId)
        workManager.cancelUniqueWork(workName(profileId, assetId))
        val record = store.remove(assetId) ?: return
        store.deleteFiles(record)
        store.removeProgress(assetId)
        val variantId = record.variantId
        if (variantId != null) {
            store.addReport(PendingReport(ReportKind.Delete, assetId, variantId, 0L, System.currentTimeMillis()))
        }
        scheduleSync()
    }

    /** Removes a finished download that has expired. The Engine already dropped it, so there is nothing to report. */
    fun purge(store: OfflineStore, record: DownloadRecord) {
        store.remove(record.assetId)
        store.deleteFiles(record)
    }

    fun purgeLocallyExpired() {
        val store = store()
        val now = System.currentTimeMillis()
        store.snapshot().filter { OfflineLogic.isExpired(it, now) }.forEach { purge(store, it) }
    }

    /** Starts the work again for downloads a closed app or a restart left half done. Running work is left alone. */
    fun resumeInterrupted() {
        val profileId = activeProfileId() ?: return
        store().snapshot()
            .filter {
                it.state == DownloadState.Queued || it.state == DownloadState.Preparing ||
                    it.state == DownloadState.Downloading
            }
            .forEach { scheduleDownload(profileId, it.assetId, it.profileKey, ExistingWorkPolicy.KEEP) }
    }

    /** Called when the app opens and when the person switches profile. */
    fun onProfileActivated() {
        purgeLocallyExpired()
        resumeInterrupted()
        scheduleSync()
    }

    /**
     * Reports stores and deletes to the Engine's list, drops downloads the Engine expired, and replays progress
     * recorded offline. It waits for a connection, so calling it while offline simply schedules it.
     */
    fun scheduleSync() {
        val request = OneTimeWorkRequestBuilder<OfflineSyncWorker>()
            .setConstraints(connected())
            .setBackoffCriteria(BackoffPolicy.EXPONENTIAL, 1L, TimeUnit.MINUTES)
            .addTag(TAG)
            .build()
        workManager.enqueueUniqueWork(SYNC_WORK, ExistingWorkPolicy.REPLACE, request)
    }

    /** Removes every download and every unsent record from this phone; used when it signs out. */
    fun clearAll() {
        workManager.cancelAllWorkByTag(TAG)
        synchronized(stores) {
            stores.values.forEach { it.clearAll() }
            stores.clear()
        }
        File(app.noBackupFilesDir, "offline").deleteRecursively()
    }

    private fun scheduleDownload(profileId: String, assetId: String, profileKey: String, policy: ExistingWorkPolicy) {
        val request = OneTimeWorkRequestBuilder<OfflineDownloadWorker>()
            .setInputData(
                workDataOf(
                    OfflineDownloadWorker.KEY_ASSET_ID to assetId,
                    OfflineDownloadWorker.KEY_PROFILE_KEY to profileKey,
                    OfflineDownloadWorker.KEY_PROFILE_ID to profileId,
                ),
            )
            .setConstraints(connected())
            .setBackoffCriteria(BackoffPolicy.LINEAR, 30L, TimeUnit.SECONDS)
            .addTag(TAG)
            .build()
        workManager.enqueueUniqueWork(workName(profileId, assetId), policy, request)
    }

    private fun connected(): Constraints =
        Constraints.Builder().setRequiredNetworkType(NetworkType.CONNECTED).build()

    private fun workName(profileId: String, assetId: String): String = "download-${safeName(profileId)}-$assetId"

    private fun safeName(value: String): String = value.filter { it.isLetterOrDigit() || it == '-' }.ifEmpty { "profile" }

    companion object {
        const val TAG = "tuvima-offline"
        const val SYNC_WORK = "tuvima-offline-sync"
        private const val UNPAIRED = "unpaired"
    }
}

/** The words shown after a download was asked for. */
internal fun enqueueMessage(result: EnqueueResult): String = when (result) {
    is EnqueueResult.Queued -> "Download started. Track it under Downloads."
    is EnqueueResult.AlreadyHeld -> when (result.state) {
        DownloadState.Complete -> "This is already downloaded."
        else -> "This is already in your downloads."
    }
    is EnqueueResult.OverCap ->
        "Storage limit reached (${OfflineLogic.formatBytes(result.committedBytes)} of " +
            "${OfflineLogic.formatBytes(result.capBytes)}). Delete a download or raise the limit in Settings."
    is EnqueueResult.NotOffered -> "Books and photos are not downloaded by this app yet."
    is EnqueueResult.NotPaired -> "Connect this phone to a server first."
}

internal fun nextEpisodesMessage(result: NextEpisodesResult): String = when {
    result.found == 0 -> "There are no more episodes after this one in your library."
    result.overCap && result.queued == 0 -> "Storage limit reached. Delete a download or raise the limit in Settings."
    result.overCap -> "Started ${result.queued} download(s); stopped at the storage limit."
    result.queued == 0 -> "The next episodes are already in your downloads."
    result.queued == 1 -> "Started downloading the next episode."
    else -> "Started downloading the next ${result.queued} episodes."
}
