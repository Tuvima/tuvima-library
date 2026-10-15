package com.tuvima.library.mobile

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.tuvima.library.core.OfflineKind
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.ReportKind
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ReportOutcome
import com.tuvima.library.core.BoundProfile
import com.tuvima.library.core.SyncGuard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TuvimaHttpException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.withContext

/**
 * Brings the Engine and this phone back in step once there is a connection, in three parts:
 *
 * 1. Reports each stored and deleted download or kept photo to the Engine's device download list (204 is success; a
 *    report that cannot be sent yet stays queued).
 * 2. Reads that list back: a finished download whose row is gone has expired on the Engine and is removed here. The same
 *    goes for a kept photo whose row is gone (the photo was deleted, or this person can no longer see it).
 * 3. Replays progress recorded while playing downloads offline, oldest first, with `offline: true` and the time it
 *    was recorded. When the Engine kept its own newer record, the phone adopts it.
 *
 * Only the person using the phone is synced; another person's records wait until they are back. One run is bound to the
 * person who was using the phone when it started: if the profile changes mid-run, the run stops (and is scheduled again for
 * whoever is using the phone now) without reporting, marking, replaying or purging anything for the new person. The phone
 * never writes History; the Engine does that when it applies a replayed play.
 */
class OfflineSyncWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val app = applicationContext as TuvimaMobileApplication
        val client = app.client ?: app.savedServer()?.let(app::connect) ?: return Result.success()
        if (!client.isPaired()) return Result.success()
        // Bind this run to one profile; every client call below also refuses to run once another profile is signed in.
        val guard = SyncGuard.start(client::profileBinding) ?: return Result.success()
        val store = app.offline.storeFor(guard.profileId)
        return withContext(BoundProfile(guard.binding)) {
            val reported = flushReports(client, app.offline, store, guard)
            if (!guard.isBound()) return@withContext switched(app)
            val expiryChecked = purgeExpired(client, app.offline, store, guard)
            if (!guard.isBound()) return@withContext switched(app)
            val replayed = replayProgress(client, store, guard)
            if (!guard.isBound()) return@withContext switched(app)
            if (reported && expiryChecked && replayed) Result.success() else Result.retry()
        }
    }

    /** The profile changed mid-run: nothing more is sent for the old one, and the new one gets its own run. */
    private fun switched(app: TuvimaMobileApplication): Result {
        app.offline.scheduleSync()
        return Result.success()
    }

    /** True when every queued report was answered. */
    private suspend fun flushReports(
        client: TuvimaClient,
        offline: OfflineDownloads,
        store: OfflineStore,
        guard: SyncGuard,
    ): Boolean {
        for (report in store.reports()) {
            if (!guard.isBound()) return false
            val status = try {
                when {
                    report.itemKind == OfflineKind.Photo && report.kind == ReportKind.Store ->
                        client.reportPhotoStored(report.assetId, report.storedBytes, report.storedAtMs)
                    report.itemKind == OfflineKind.Photo -> client.reportPhotoDeleted(report.assetId)
                    report.kind == ReportKind.Store ->
                        client.reportDownloadStored(report.assetId, report.variantId, report.storedBytes, report.storedAtMs)
                    else -> client.reportDownloadDeleted(report.variantId)
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // No connection, or the Engine could not be reached: keep the report and try again later.
                return false
            }
            // The answer belongs to the person who was signed in when it was sent; if that changed, mark and purge nothing.
            if (!guard.isBound()) return false
            when (OfflineLogic.classifyReport(report.kind, status)) {
                ReportOutcome.Done -> {
                    store.removeReport(report)
                    if (report.kind == ReportKind.Store) markReported(store, report)
                }
                ReportOutcome.Drop -> {
                    store.removeReport(report)
                    // 404: the Engine no longer knows this variant or photo (it expired, was deleted or is no longer
                    // visible to this person), so the Engine's list will not show it and the check below removes the local copy.
                    if (report.kind == ReportKind.Store && status == 404) markReported(store, report)
                    // 409 for a photo: this device already lists as many entries as it may, so stop keeping and drop the copy.
                    if (report.itemKind == OfflineKind.Photo && OfflineLogic.isDeviceLimitRefusal(report.kind, status)) {
                        offline.onDeviceLimitReached(store, report.assetId)
                    }
                }
                ReportOutcome.Retry -> return false
            }
        }
        return true
    }

    private fun markReported(store: OfflineStore, report: PendingReport) {
        store.update(report.assetId) { if (OfflineLogic.reportMatches(report, it)) it.copy(reportedToServer = true) else it }
    }

    /** True when the Engine's list was read and applied. */
    private suspend fun purgeExpired(
        client: TuvimaClient,
        offline: OfflineDownloads,
        store: OfflineStore,
        guard: SyncGuard,
    ): Boolean {
        if (!guard.isBound()) return false
        // Take the phone's side first: a download reported after this point cannot be mistaken for a missing row.
        val local = store.snapshot()
        val listing = try {
            client.deviceDownloads()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            return false
        }
        // Another person's list must never be used to purge this person's downloads.
        if (!guard.isBound()) return false
        offline.knownEngineListCount = listing.size
        val result = OfflineLogic.reconcileWithServer(local, listing, System.currentTimeMillis())
        result.purge.forEach { offline.purge(store, it) }
        // A kept photo the Engine's list no longer shows is purged here; tell the Engine to forget any hidden row it still holds
        // (it would keep counting towards the device's limit). Tolerates 404, and a report that cannot be sent waits for the next sync.
        val deletes = OfflineLogic.purgeDeleteReports(result.purge, System.currentTimeMillis())
        if (deletes.isNotEmpty()) {
            deletes.forEach { store.addReport(it) }
            if (!flushReports(client, offline, store, guard)) return false
        }
        for (kept in result.keep) {
            val before = local.firstOrNull { it.assetId == kept.assetId }
            if (before != null && before.expiresAtMs != kept.expiresAtMs) {
                store.update(kept.assetId) { it.copy(expiresAtMs = kept.expiresAtMs) }
            }
        }
        return true
    }

    /** True when every pending entry was answered. */
    private suspend fun replayProgress(client: TuvimaClient, store: OfflineStore, guard: SyncGuard): Boolean {
        for (entry in OfflineLogic.replayOrder(store.progress())) {
            if (!guard.isBound()) return false
            val answer = try {
                client.replayOfflineProgress(entry, entry.revision)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: TuvimaHttpException) {
                if (!guard.isBound()) return false
                val permanent = failure.statusCode in 400..499 && failure.statusCode != 401 &&
                    failure.statusCode != 408 && failure.statusCode != 429
                if (!permanent) return false
                // The Engine refuses this asset for good (gone, or not allowed for this person): stop retrying it.
                store.completeReplay(entry, entry.copy(pending = false))
                continue
            } catch (failure: Exception) {
                return false
            }
            if (!guard.isBound()) return false
            val now = System.currentTimeMillis()
            val updated = if (OfflineLogic.isServerWon(entry, entry.revision, answer)) {
                OfflineLogic.adoptServerState(entry, answer, now)
            } else {
                OfflineLogic.markReplayed(entry, answer)
            }
            store.completeReplay(entry, updated)
        }
        return true
    }
}
