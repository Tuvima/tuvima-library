package com.tuvima.library.mobile

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.tuvima.library.core.BoundProfile
import com.tuvima.library.core.ClientRoutes
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineKind
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ProfileChangedException
import com.tuvima.library.core.ReportKind
import com.tuvima.library.core.SyncGuard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TuvimaHttpException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.withContext

/**
 * Keeps the queued photos of one person on the phone, one after another. Each is the 2048 px preview downloaded from
 * `/api/v1/view/items/{id}/preview` (a fraction of the original's size), written under the person's own offline folder and
 * reported to the Engine's device download list by the sync worker once it is complete. The same storage cap as every
 * other download applies. A photo that fails for good (it was deleted, or this person cannot see it) is marked failed;
 * connection problems are retried.
 */
class OfflinePhotoWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    private enum class Outcome { Stored, Retry, Failed, Gone, Switched }

    override suspend fun doWork(): Result {
        val profileId = inputData.getString(KEY_PROFILE_ID) ?: return Result.failure()
        val app = applicationContext as TuvimaMobileApplication
        val client = app.client ?: app.savedServer()?.let(app::connect) ?: return Result.success()
        val store = app.offline.storeFor(profileId)
        // A photo belongs to the person who asked for it; it waits until they are using the phone again.
        val guard = SyncGuard.startFor(profileId, client::profileBinding)
        if (guard == null) {
            pauseWaiting(store)
            return Result.success()
        }
        // Bound to this person: a profile switch mid-run stops every further request instead of sending it with the next
        // person's sign-in, and the photos still waiting are held for them.
        return withContext(BoundProfile(guard.binding)) { keepQueued(app, client, store) }
    }

    private fun pauseWaiting(store: OfflineStore) {
        store.snapshot().filter { it.isPhoto && it.state == DownloadState.Queued }.forEach { waiting ->
            store.update(waiting.assetId) {
                it.copy(state = DownloadState.Paused, error = "Switch back to this profile to continue.")
            }
        }
    }

    private suspend fun keepQueued(app: TuvimaMobileApplication, client: TuvimaClient, store: OfflineStore): Result {
        val deferred = mutableSetOf<String>()
        var stored = false
        while (true) {
            val next = store.snapshot()
                .filter { it.isPhoto && it.state == DownloadState.Queued && it.assetId !in deferred }
                .minByOrNull { it.queuedAtMs } ?: break
            when (keepOne(app, client, store, next)) {
                Outcome.Stored -> stored = true
                Outcome.Retry -> deferred.add(next.assetId)
                Outcome.Failed, Outcome.Gone -> Unit
                Outcome.Switched -> {
                    pauseWaiting(store)
                    if (stored) app.offline.scheduleSync()
                    return Result.success()
                }
            }
        }
        if (stored) app.offline.scheduleSync()
        if (deferred.isEmpty()) return Result.success()
        if (runAttemptCount < MAX_ATTEMPTS) return Result.retry()
        deferred.forEach { id ->
            store.update(id) { it.copy(state = DownloadState.Failed, error = "The photo could not be downloaded.") }
        }
        return Result.success()
    }

    private suspend fun keepOne(
        app: TuvimaMobileApplication,
        client: TuvimaClient,
        store: OfflineStore,
        start: DownloadRecord,
    ): Outcome {
        val id = start.assetId
        store.update(id) { it.copy(state = DownloadState.Downloading, error = null) } ?: return Outcome.Gone
        val file = store.photoFile(id)
        // A preview is small and is not served in ranges, so a leftover file is replaced rather than continued.
        file.delete()
        try {
            client.downloadToFile(ClientRoutes.viewPreview(id, OfflineLogic.PHOTO_PREVIEW_EDGE), file)
        } catch (cancelled: CancellationException) {
            // Stopped by the system: nothing partial is kept, and the photo goes back in the queue.
            file.delete()
            store.update(id) { if (it.state == DownloadState.Downloading) it.copy(state = DownloadState.Queued) else it }
            throw cancelled
        } catch (switched: ProfileChangedException) {
            // Another person is using the phone now: keep nothing partial; this photo waits in the queue for its owner.
            file.delete()
            store.update(id) { if (it.state == DownloadState.Downloading) it.copy(state = DownloadState.Queued) else it }
            return Outcome.Switched
        } catch (failure: Exception) {
            file.delete()
            val permanent = failure is TuvimaHttpException && failure.statusCode in 400..499 &&
                failure.statusCode != 401 && failure.statusCode != 408 && failure.statusCode != 429
            if (permanent) {
                store.update(id) { it.copy(state = DownloadState.Failed, error = userMessage(failure)) }
                return Outcome.Failed
            }
            store.update(id) { it.copy(state = DownloadState.Queued, error = "Waiting to retry: ${userMessage(failure)}") }
            return Outcome.Retry
        }

        val length = file.length()
        if (length <= 0L) {
            file.delete()
            store.update(id) { it.copy(state = DownloadState.Queued, error = "Waiting to retry: empty answer.") }
            return Outcome.Retry
        }

        // The storage cap: what the other downloads hold or have reserved, plus this finished preview.
        val others = store.snapshot().filter { it.assetId != id }
        if (OfflineLogic.committedBytes(others) + length > app.storageCapBytes()) {
            file.delete()
            store.update(id) {
                it.copy(
                    state = DownloadState.Failed,
                    bytesDone = 0L,
                    error = "Storage limit reached. Delete a download or raise the limit in Settings.",
                )
            }
            return Outcome.Failed
        }

        val storedAt = System.currentTimeMillis()
        val done = store.update(id) {
            it.copy(
                state = DownloadState.Complete,
                bytesDone = length,
                totalBytes = length,
                storedAtMs = storedAt,
                filePath = file.absolutePath,
                reportedToServer = false,
                error = null,
            )
        }
        if (done == null) {
            // Deleted while it was finishing.
            file.delete()
            return Outcome.Gone
        }
        store.addReport(PendingReport(ReportKind.Store, id, id, length, storedAt, OfflineKind.Photo))
        return Outcome.Stored
    }

    companion object {
        const val KEY_PROFILE_ID = "profile_id"
        private const val MAX_ATTEMPTS = 8
    }
}
