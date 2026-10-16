package com.tuvima.library.mobile

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * What this phone holds for offline use: artwork, title, size, quality and expiry for each copy, with pause,
 * resume and delete, and a total against the storage limit set in Settings. The list is read from the phone, so it
 * opens without a connection. Tapping Play uses the downloaded file. Photos kept on the phone are in the same list,
 * with the same limit; View opens them in the photo viewer, which reads the local copy.
 */
@Composable
internal fun DownloadsScreen(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    play: (PlayRequest) -> Unit,
    openPhotos: (List<ViewItem>, Int) -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val offline = application.offline
    val records by offline.store().records.collectAsState()
    val limitNotice by offline.limitNotice.collectAsState()
    var confirmDelete by remember { mutableStateOf<DownloadRecord?>(null) }
    val now = System.currentTimeMillis()

    LaunchedEffect(Unit) {
        // Drop copies that have expired, and bring the Engine's list and any offline progress up to date.
        offline.purgeLocallyExpired()
        offline.scheduleSync()
    }

    val committed = OfflineLogic.committedBytes(records)
    val cap = application.storageCapBytes()

    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Text("Downloads", style = MaterialTheme.typography.headlineMedium)
        Text(
            "Using ${OfflineLogic.formatBytes(committed)} of ${OfflineLogic.formatBytes(cap)}",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(top = 4.dp),
        )
        if (cap > 0L) {
            LinearProgressIndicator(
                progress = { (committed.toDouble() / cap.toDouble()).toFloat().coerceIn(0f, 1f) },
                modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
            )
        }
        limitNotice?.let { notice ->
            Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                Text(
                    notice,
                    color = MaterialTheme.colorScheme.error,
                    style = MaterialTheme.typography.bodySmall,
                    modifier = Modifier.weight(1f),
                )
                TextButton(onClick = { offline.limitNotice.value = null }) { Text("OK") }
            }
        }
        if (!OfflineLogic.canQueue(committed, cap)) {
            Text(
                "Storage limit reached. Delete a download or raise the limit in Settings.",
                color = MaterialTheme.colorScheme.error,
                style = MaterialTheme.typography.bodySmall,
            )
        }
        if (records.isEmpty()) {
            Text(
                "Nothing is downloaded yet. Open a movie, episode, audiobook or album and choose Download to watch " +
                    "or listen without a connection, or keep a photo or gallery from View on this phone.",
                modifier = Modifier.padding(top = 16.dp),
            )
        } else {
            LazyColumn(
                Modifier.fillMaxSize().padding(top = 8.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                items(records.sortedByDescending { it.queuedAtMs }, key = { it.assetId }) { record ->
                    DownloadRow(
                        client = client,
                        record = record,
                        nowMs = now,
                        onPlay = { play(PlayRequest(record.assetId, record.entityType, record.title, record.subtitle, showId = record.showId)) },
                        onView = {
                            val kept = keptPhotoItems(records)
                            openPhotos(kept, kept.indexOfFirst { it.id == record.assetId }.coerceAtLeast(0))
                        },
                        onPause = { offline.pause(record.assetId) },
                        onResume = { offline.resume(record.assetId) },
                        onDelete = { confirmDelete = record },
                        onNextEpisodes = {
                            val showId = record.showId
                            if (showId != null) {
                                scope.launch {
                                    val message = try {
                                        nextEpisodesMessage(
                                            offline.downloadNextEpisodes(client, showId, record.assetId, record.profileKey),
                                        )
                                    } catch (cancelled: CancellationException) {
                                        throw cancelled
                                    } catch (failure: Exception) {
                                        userMessage(failure)
                                    }
                                    Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                                }
                            }
                        },
                    )
                }
            }
        }
    }

    confirmDelete?.let { target ->
        AlertDialog(
            onDismissRequest = { confirmDelete = null },
            title = { Text(if (target.isComplete) "Delete this download?" else "Cancel this download?") },
            text = { Text("\"${target.title}\" is removed from this phone. You can download it again later.") },
            confirmButton = {
                Button(onClick = {
                    confirmDelete = null
                    offline.delete(target.assetId)
                }) { Text(if (target.isComplete) "Delete" else "Cancel download") }
            },
            dismissButton = { TextButton(onClick = { confirmDelete = null }) { Text("Keep") } },
        )
    }
}

@Composable
private fun DownloadRow(
    client: TuvimaClient,
    record: DownloadRecord,
    nowMs: Long,
    onPlay: () -> Unit,
    onView: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onDelete: () -> Unit,
    onNextEpisodes: () -> Unit,
) {
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            DownloadArtwork(
                client,
                record,
                Modifier.width(72.dp).aspectRatio(if (record.isPhoto) 1f else 2f / 3f).clip(RoundedCornerShape(8.dp)),
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    record.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                record.subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                Text(downloadStatusLine(record, nowMs), style = MaterialTheme.typography.bodySmall)
                if (!record.isComplete) {
                    val fraction = record.fraction()
                    if (fraction != null) {
                        LinearProgressIndicator(progress = { fraction }, modifier = Modifier.fillMaxWidth())
                    } else if (record.state != DownloadState.Paused && record.state != DownloadState.Failed) {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    when (record.state) {
                        DownloadState.Complete ->
                            if (record.isPhoto) {
                                Button(onClick = onView) { Text("View") }
                            } else if (!OfflineLogic.isExpired(record, nowMs)) {
                                Button(onClick = onPlay) { Text("Play") }
                            }
                        DownloadState.Queued, DownloadState.Preparing, DownloadState.Downloading ->
                            OutlinedButton(onClick = onPause) { Text("Pause") }
                        DownloadState.Paused -> Button(onClick = onResume) { Text("Resume") }
                        DownloadState.Failed -> Button(onClick = onResume) { Text("Retry") }
                    }
                    TextButton(onClick = onDelete) { Text(if (record.isComplete) "Delete" else "Cancel") }
                }
                if (record.isComplete && record.showId != null) {
                    TextButton(onClick = onNextEpisodes) { Text("Download next episodes") }
                }
            }
        }
    }
}

/** The saved artwork when the phone has it; otherwise the Engine's, which needs a connection. */
@Composable
private fun DownloadArtwork(client: TuvimaClient, record: DownloadRecord, modifier: Modifier) {
    // A kept photo is its own picture: read from the phone, never from the Engine.
    val photoPath = record.filePath?.takeIf { record.isPhoto && record.isComplete }
    if (photoPath != null) {
        LocalImage(photoPath, 256, modifier)
        return
    }
    val path = record.artworkPath
    val bitmap by produceState<Bitmap?>(initialValue = null, key1 = path) {
        value = if (path == null) null else withContext(Dispatchers.IO) { BitmapFactory.decodeFile(path) }
    }
    val image = bitmap
    if (image != null) {
        Box(modifier.background(MaterialTheme.colorScheme.surfaceVariant)) {
            Image(
                bitmap = image.asImageBitmap(),
                contentDescription = null,
                modifier = Modifier.fillMaxSize(),
                contentScale = ContentScale.Crop,
            )
        }
    } else {
        RemoteImage(client, record.artworkUrl, modifier)
    }
}

/** One line that says where a download is: its size, quality and expiry once finished, otherwise its progress. */
internal fun downloadStatusLine(record: DownloadRecord, nowMs: Long): String {
    val separator = " · "
    val done = OfflineLogic.formatBytes(record.bytesDone)
    val progress = if (record.totalBytes > 0L) "$done of ${OfflineLogic.formatBytes(record.totalBytes)}" else done
    return when (record.state) {
        DownloadState.Complete -> listOfNotNull(
            OfflineLogic.formatBytes(record.bytesDone),
            OfflineLogic.qualityLabel(record.profileKey),
            OfflineLogic.expiryLabel(record.expiresAtMs, nowMs),
        ).joinToString(separator)
        DownloadState.Queued -> record.error ?: "Waiting for a connection"
        DownloadState.Preparing -> "Preparing the copy on your server"
        DownloadState.Downloading -> "Downloading $progress"
        DownloadState.Paused -> "Paused" + separator + progress
        DownloadState.Failed -> record.error ?: "The download failed"
    }
}
