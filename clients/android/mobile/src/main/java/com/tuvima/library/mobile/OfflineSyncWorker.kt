package com.tuvima.library.mobile

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import com.tuvima.library.core.OfflineKind
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.ReportKind
import com.tuvima.library.core.PendingReport
import com.tuvima.library.core.ReportOutcome
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TuvimaHttpException
import kotlinx.coroutines.CancellationException

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
 * Only the person using the phone is synced; another person's records wait until they are back. The phone never
 * writes History; the Engine does that when it applies a replayed play.
 */
class OfflineSyncWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val app = applicationContext as TuvimaMobileApplication
        val client = app.client ?: app.savedServer()?.let(app::connect) ?: return Result.success()
        if (!client.isPaired()) return Result.success()
        val store = app.offline.store()
        val reported = flushReports(client, store)
        val expiryChecked = purgeExpired(client, app.offline, store)
        val replayed = replayProgress(client, store)
        return if (reported && expiryChecked && replayed) Result.success() else Result.retry()
    }

    /** True when every queued report was answered. */
    private suspend fun flushReports(client: TuvimaClient, store: OfflineStore): Boolean {
        for (report in store.reports()) {
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
    private suspend fun purgeExpired(client: TuvimaClient, offline: OfflineDownloads, store: OfflineStore): Boolean {
        // Take the phone's side first: a download reported after this point cannot be mistaken for a missing row.
        val local = store.snapshot()
        val listing = try {
            client.deviceDownloads()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            return false
        }
        val result = OfflineLogic.reconcileWithServer(local, listing, System.currentTimeMillis())
        result.purge.forEach { offline.purge(store, it) }
        for (kept in result.keep) {
            val before = local.firstOrNull { it.assetId == kept.assetId }
            if (before != null && before.expiresAtMs != kept.expiresAtMs) {
                store.update(kept.assetId) { it.copy(expiresAtMs = kept.expiresAtMs) }
            }
        }
        return true
    }

    /** True when every pending entry was answered. */
    private suspend fun replayProgress(client: TuvimaClient, store: OfflineStore): Boolean {
        for (entry in OfflineLogic.replayOrder(store.progress())) {
            val answer = try {
                client.replayOfflineProgress(entry, entry.revision)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: TuvimaHttpException) {
                val permanent = failure.statusCode in 400..499 && failure.statusCode != 401 &&
                    failure.statusCode != 408 && failure.statusCode != 429
                if (!permanent) return false
                // The Engine refuses this asset for good (gone, or not allowed for this person): stop retrying it.
                store.completeReplay(entry, entry.copy(pending = false))
                continue
            } catch (failure: Exception) {
                return false
            }
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
