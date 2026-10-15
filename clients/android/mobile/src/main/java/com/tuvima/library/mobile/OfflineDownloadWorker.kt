package com.tuvima.library.mobile

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.tuvima.library.core.BoundProfile
import com.tuvima.library.core.ClientRoutes
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.OfflineProgressEntry
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ProfileChangedException
import com.tuvima.library.core.ReportKind
import com.tuvima.library.core.SyncGuard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TuvimaHttpException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext
import java.io.File

/**
 * Downloads one offline copy. It asks the Engine to prepare the variant (once), waits until it is ready, then
 * downloads the file from `/api/v1/playback/{assetId}/offline/{variantId}` with HTTP range requests, so a stopped
 * or interrupted download continues from the bytes already on disk. When the file is complete it is recorded and
 * reported to the Engine's device download list by the sync worker.
 */
class OfflineDownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val assetId = inputData.getString(KEY_ASSET_ID) ?: return Result.failure()
        val profileId = inputData.getString(KEY_PROFILE_ID) ?: return Result.failure()
        val app = applicationContext as TuvimaMobileApplication
        val client = app.client ?: app.savedServer()?.let(app::connect) ?: return Result.failure()
        val store = app.offline.storeFor(profileId)
        val record = store.record(assetId) ?: return Result.success()
        if (record.state == DownloadState.Paused) return Result.success()
        // A download belongs to the person who asked for it; it waits until they are using the phone again.
        val guard = SyncGuard.startFor(profileId, client::profileBinding)
        if (guard == null) {
            store.update(assetId) { it.copy(state = DownloadState.Paused, error = "Switch back to this profile to continue.") }
            return Result.success()
        }
        return try {
            // Bound to this person: a profile switch mid-download stops every further request instead of sending it with
            // the next person's sign-in. What is on disk stays in this person's own store.
            withContext(BoundProfile(guard.binding)) { download(app, client, store, record) }
        } catch (cancelled: CancellationException) {
            // Stopped by Pause, Delete or the system (for example the connection dropped). Keep the partial file.
            val current = store.record(assetId)
            if (current == null) {
                record.filePath?.let { File(it).delete() }
                record.artworkPath?.let { File(it).delete() }
            } else if (current.state == DownloadState.Preparing || current.state == DownloadState.Downloading) {
                store.update(assetId) { it.copy(state = DownloadState.Queued) }
            }
            throw cancelled
        } catch (switched: ProfileChangedException) {
            store.update(assetId) { it.copy(state = DownloadState.Paused, error = "Switch back to this profile to continue.") }
            Result.success()
        } catch (failure: Exception) {
            val permanent = failure is TuvimaHttpException && failure.statusCode in 400..499 &&
                failure.statusCode != 401 && failure.statusCode != 408 && failure.statusCode != 429
            if (!permanent && runAttemptCount < MAX_ATTEMPTS) {
                store.update(assetId) { it.copy(state = DownloadState.Queued, error = "Waiting to retry: ${userMessage(failure)}") }
                Result.retry()
            } else {
                fail(store, assetId, userMessage(failure))
            }
        }
    }

    private suspend fun download(
        app: TuvimaMobileApplication,
        client: TuvimaClient,
        store: OfflineStore,
        start: DownloadRecord,
    ): Result {
        val assetId = start.assetId
        store.update(assetId) { it.copy(state = DownloadState.Preparing, error = null) }

        // Keep the artwork on the phone so the list still shows it without a connection.
        val artworkUrl = start.artworkUrl
        if (start.artworkPath == null && artworkUrl != null) {
            val bytes = tryOrNull { client.authenticatedBytes(artworkUrl) }
            if (bytes != null && bytes.isNotEmpty()) {
                val file = store.artworkFile(assetId)
                file.parentFile?.mkdirs()
                file.writeBytes(bytes)
                store.update(assetId) { it.copy(artworkPath = file.absolutePath) }
            }
        }

        var current = store.record(assetId) ?: return Result.success()
        var variants = client.playbackManifest(assetId, "local").offlineVariants
        var variant = OfflineLogic.chooseReadyVariant(variants, current.profileKey, current.variantId)

        if (variant == null && current.variantId != null) {
            // The variant this download was using is no longer ready on the Engine: start over with a new one.
            current.filePath?.let { File(it).delete() }
            current = store.update(assetId) {
                it.copy(variantId = null, filePath = null, bytesDone = 0L, totalBytes = 0L, variantRequested = false)
            } ?: return Result.success()
        }

        if (variant == null) {
            if (!current.variantRequested) {
                client.requestOfflineVariant(assetId, current.profileKey)
                current = store.update(assetId) { it.copy(variantRequested = true) } ?: return Result.success()
            }
            // The Engine prepares the copy in the background. Wait a while here, then let WorkManager come back later.
            for (attempt in 0 until READY_POLLS) {
                delay(READY_POLL_MS)
                variants = client.playbackManifest(assetId, "local").offlineVariants
                variant = OfflineLogic.chooseReadyVariant(variants, current.profileKey, current.variantId)
                if (variant != null) break
            }
        }
        val ready = variant
        if (ready == null) {
            if (runAttemptCount >= MAX_WAIT_ATTEMPTS) {
                return fail(store, assetId, "The Engine did not finish preparing the offline copy.")
            }
            store.update(assetId) { it.copy(state = DownloadState.Queued, error = "The Engine is still preparing this copy.") }
            return Result.retry()
        }

        val extension = if (current.profileKey == OfflineLogic.QUALITY_AUDIO) "m4a" else "mp4"
        val file = store.mediaFile(ready.id, extension)
        file.parentFile?.mkdirs()
        val expected = ready.fileSizeBytes ?: 0L
        if (expected > 0L && file.length() > expected) file.delete()
        val onDisk = if (file.isFile) file.length() else 0L

        // The storage cap: what the other downloads hold, plus this one's partial file, plus the bytes still to come.
        val others = store.snapshot().filter { it.assetId != assetId }
        val committed = OfflineLogic.committedBytes(others) + onDisk
        val remaining = if (expected > 0L) (expected - onDisk).coerceAtLeast(0L) else 0L
        if (remaining > 0L && !OfflineLogic.canQueue(committed, app.storageCapBytes(), remaining)) {
            file.delete()
            store.update(assetId) { it.copy(bytesDone = 0L) }
            return fail(store, assetId, "Storage limit reached. Delete a download or raise the limit in Settings.")
        }

        store.update(assetId) {
            it.copy(
                state = DownloadState.Downloading,
                variantId = ready.id,
                totalBytes = expected,
                bytesDone = onDisk,
                expiresAtMs = ready.expiresAtMs,
                filePath = file.absolutePath,
                error = null,
            )
        }

        if (expected <= 0L || onDisk < expected) {
            var lastUpdate = 0L
            client.downloadToFile(ClientRoutes.offlineFile(assetId, ready.id), file) { copied, total ->
                val now = System.currentTimeMillis()
                if (now - lastUpdate >= PROGRESS_INTERVAL_MS) {
                    lastUpdate = now
                    store.updateTransient(assetId) {
                        it.copy(bytesDone = copied, totalBytes = total ?: it.totalBytes)
                    }
                }
            }
        }

        val length = file.length()
        if (expected > 0L && length < expected) {
            // Cut short: the next attempt continues from these bytes.
            store.update(assetId) { it.copy(state = DownloadState.Queued, bytesDone = length) }
            return Result.retry()
        }

        val storedAt = System.currentTimeMillis()
        val done = store.update(assetId) {
            it.copy(
                state = DownloadState.Complete,
                bytesDone = length,
                totalBytes = length,
                storedAtMs = storedAt,
                expiresAtMs = ready.expiresAtMs,
                filePath = file.absolutePath,
                reportedToServer = false,
                error = null,
            )
        }
        if (done == null) {
            // Deleted while it was finishing.
            file.delete()
            return Result.success()
        }
        store.addReport(PendingReport(ReportKind.Store, assetId, ready.id, length, storedAt))

        // Start the file where the Engine says the person left off, when nothing was recorded here yet.
        val server = tryOrNull { client.serverProgress(assetId) }
        if (server != null) {
            store.seedProgress(
                OfflineProgressEntry(
                    assetId = assetId,
                    positionSeconds = server.positionSeconds ?: 0.0,
                    durationSeconds = null,
                    progressPct = server.progressPct,
                    recordedAtMs = server.lastAccessedMs ?: storedAt,
                    pending = false,
                    revision = server.revision,
                ),
            )
        }
        app.offline.scheduleSync()
        return Result.success()
    }

    private fun fail(store: OfflineStore, assetId: String, message: String): Result {
        store.update(assetId) { it.copy(state = DownloadState.Failed, error = message) }
        return Result.failure()
    }

    companion object {
        const val KEY_ASSET_ID = "asset_id"
        const val KEY_PROFILE_KEY = "profile_key"
        const val KEY_PROFILE_ID = "profile_id"
        private const val MAX_ATTEMPTS = 8
        private const val MAX_WAIT_ATTEMPTS = 20
        private const val READY_POLLS = 45
        private const val READY_POLL_MS = 2_000L
        private const val PROGRESS_INTERVAL_MS = 400L
    }
}

/** Runs [block] and returns null when it fails for any reason except being cancelled. */
internal suspend fun <T> tryOrNull(block: suspend () -> T): T? = try {
    block()
} catch (cancelled: CancellationException) {
    throw cancelled
} catch (failure: Exception) {
    null
}
