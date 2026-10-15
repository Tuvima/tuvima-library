package com.tuvima.library.mobile

import android.content.Context
import androidx.work.CoroutineWorker
import androidx.work.Data
import androidx.work.WorkerParameters
import kotlinx.coroutines.delay
import java.io.File

class OfflineDownloadWorker(context: Context, parameters: WorkerParameters) : CoroutineWorker(context, parameters) {
    override suspend fun doWork(): Result {
        val assetId = inputData.getString(KEY_ASSET_ID) ?: return Result.failure()
        val app = applicationContext as TuvimaMobileApplication
        val client = app.client ?: app.savedServer()?.let(app::connect) ?: return Result.failure()
        return runCatching {
            client.requestOfflineVariant(
                assetId,
                inputData.getString(KEY_PROFILE_KEY) ?: TuvimaMobileApplication.DEFAULT_DOWNLOAD_QUALITY,
            )
            var readyUrl: String? = null
            for (attempt in 0 until 180) {
                val manifest = client.playbackManifest(assetId, "local")
                readyUrl = manifest.offlineVariants.firstOrNull { it.status == "ready" }?.downloadUrl
                if (readyUrl != null) break
                delay(2_000)
            }
            val url = readyUrl ?: error("The offline copy was not prepared in time.")
            val destination = File(applicationContext.noBackupFilesDir, "downloads/$assetId.media")
            client.downloadToFile(url, destination) { copied, total ->
                setProgressAsync(
                    Data.Builder().putLong("bytes", copied).putLong("total", total ?: -1).build(),
                )
            }
            Result.success(Data.Builder().putString("path", destination.absolutePath).build())
        }.getOrElse { if (runAttemptCount < 3) Result.retry() else Result.failure() }
    }

    companion object {
        const val KEY_ASSET_ID = "asset_id"
        const val KEY_PROFILE_KEY = "profile_key"
    }
}
