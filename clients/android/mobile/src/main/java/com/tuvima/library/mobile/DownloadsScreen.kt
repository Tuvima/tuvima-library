package com.tuvima.library.mobile

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Switch
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
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.DownloadsPageLogic
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.OfflineStorageLogic
import com.tuvima.library.core.StorageCap
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * What this phone holds for offline use. It opens from the phone's own list, so it works with no connection: a title that is
 * still on its way (progress, Pause, Resume, Cancel), the finished titles that play from this phone, and any whose copy has
 * expired. At the top: how much of the storage limit downloads use and how much room the phone has, Pause all and Resume all, and
 * (not for a Kids profile) the Wi-Fi-only choice and the storage limit. What an action did is said in a line of text, never a toast.
 * Photos kept on the phone are in the same list, with the same limit; View opens them in the photo viewer, which reads the local copy.
 */
@Composable
internal fun DownloadsScreen(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    play: (PlayRequest) -> Unit,
    openPhotos: (List<ViewItem>, Int) -> Unit,
    /** A Kids profile: no Wi-Fi-only or storage-limit settings, as on the Settings page. */
    limited: Boolean = false,
) {
    val scope = rememberCoroutineScope()
    val offline = application.offline
    val records by offline.store().records.collectAsState()
    val limitNotice by offline.limitNotice.collectAsState()
    val online by rememberIsOnline()
    var confirmDelete by remember { mutableStateOf<DownloadRecord?>(null) }
    var confirmRemoveAll by remember { mutableStateOf(false) }
    var note by remember { mutableStateOf<String?>(null) }
    var wifiOnly by remember { mutableStateOf(application.wifiOnlyDownloads()) }
    var capBytes by remember { mutableStateOf(application.storageCapBytes()) }
    val now = System.currentTimeMillis()

    LaunchedEffect(Unit) {
        // Drop copies that have expired, and bring the Engine's list and any offline progress up to date.
        offline.purgeLocallyExpired()
        offline.scheduleSync()
    }

    val committed = OfflineLogic.committedBytes(records)
    val free = remember(records.size, online) { offline.freeBytes() }
    val sections = DownloadsPageLogic.sections(records, now)
    val hasReady = records.any { it.isComplete && !OfflineLogic.isExpired(it, now) }

    LazyColumn(
        Modifier.fillMaxSize().padding(horizontal = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Column(verticalArrangement = Arrangement.spacedBy(4.dp), modifier = Modifier.padding(top = 16.dp)) {
                Text("Downloads", style = MaterialTheme.typography.headlineMedium)
                DownloadsPageLogic.offlineNote(online, hasReady)?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                DownloadsPageLogic.countLine(records, now)?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                Text(OfflineStorageLogic.usageLine(committed, capBytes, free), style = MaterialTheme.typography.bodySmall)
                if (capBytes > 0L) {
                    LinearProgressIndicator(
                        progress = { (committed.toDouble() / capBytes.toDouble()).toFloat().coerceIn(0f, 1f) },
                        modifier = Modifier.fillMaxWidth().padding(vertical = 8.dp),
                    )
                }
                OfflineStorageLogic.lowSpaceNote(free)?.let {
                    Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall)
                }
                limitNotice?.let { notice ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            notice,
                            color = MaterialTheme.colorScheme.error,
                            style = MaterialTheme.typography.bodySmall,
                            modifier = Modifier.weight(1f),
                        )
                        TextButton(onClick = { offline.limitNotice.value = null }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) {
                            Text("OK")
                        }
                    }
                }
                if (!OfflineLogic.canQueue(committed, capBytes)) {
                    Text(
                        "Storage limit reached. Delete a download or raise the limit.",
                        color = MaterialTheme.colorScheme.error,
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                note?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
            }
        }

        if (!limited) {
            item {
                DownloadSettings(
                    wifiOnly = wifiOnly,
                    capBytes = capBytes,
                    onWifiOnly = { value ->
                        wifiOnly = value
                        application.setWifiOnlyDownloads(value)
                        offline.onNetworkChoiceChanged()
                        note = if (value) "Downloads now wait for Wi-Fi." else "Downloads may use mobile data."
                    },
                    onCap = { bytes ->
                        capBytes = bytes
                        application.setStorageCapBytes(bytes)
                    },
                )
            }
        }

        if (records.isNotEmpty()) {
            item {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp), verticalAlignment = Alignment.CenterVertically) {
                    DownloadsPageLogic.pauseAllLabel(records)?.let { label ->
                        TextButton(onClick = { offline.pauseAll() }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) { Text(label) }
                    }
                    DownloadsPageLogic.resumeAllLabel(records)?.let { label ->
                        TextButton(onClick = { offline.resumeAll() }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) { Text(label) }
                    }
                    TextButton(onClick = { confirmRemoveAll = true }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) {
                        Text("Remove all")
                    }
                }
            }
        } else {
            item { Text(DownloadsPageLogic.EMPTY_NOTE, modifier = Modifier.padding(top = 8.dp)) }
        }

        sections.forEach { section ->
            item(key = "section-${section.section.name}") {
                Text(
                    "${section.section.title} (${section.records.size})",
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(top = 8.dp),
                )
            }
            items(section.records, key = { it.assetId }) { record ->
                DownloadRow(
                    client = client,
                    record = record,
                    nowMs = now,
                    wifiOnly = wifiOnly,
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
                                note = try {
                                    nextEpisodesMessage(offline.downloadNextEpisodes(client, showId, record.assetId, record.profileKey))
                                } catch (cancelled: CancellationException) {
                                    throw cancelled
                                } catch (failure: Exception) {
                                    userMessage(failure)
                                }
                            }
                        }
                    },
                )
            }
        }
        item { Box(Modifier.padding(bottom = 24.dp)) {} }
    }

    confirmDelete?.let { target ->
        AlertDialog(
            onDismissRequest = { confirmDelete = null },
            title = { Text(if (target.isComplete) "Delete this download?" else "Cancel this download?") },
            text = { Text("\"${target.title}\" is removed from this phone. You can download it again later.") },
            confirmButton = {
                Button(
                    onClick = {
                        confirmDelete = null
                        offline.delete(target.assetId)
                    },
                    modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight),
                ) { Text(if (target.isComplete) "Delete" else "Cancel download") }
            },
            dismissButton = {
                TextButton(onClick = { confirmDelete = null }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) { Text("Keep") }
            },
        )
    }

    if (confirmRemoveAll) {
        AlertDialog(
            onDismissRequest = { confirmRemoveAll = false },
            title = { Text("Remove all downloads?") },
            text = { Text(DownloadsPageLogic.removeAllWarning(records.size)) },
            confirmButton = {
                Button(
                    onClick = {
                        confirmRemoveAll = false
                        offline.deleteAll()
                        note = "All downloads were removed from this phone."
                    },
                    modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight),
                ) { Text("Remove all") }
            },
            dismissButton = {
                TextButton(onClick = { confirmRemoveAll = false }, modifier = Modifier.heightIn(min = TuvimaTokens.controlHeight)) { Text("Keep") }
            },
        )
    }
}

/** Wi-Fi only and the storage limit, kept on the Downloads page so a person changes them where they see what they hold. */
@Composable
private fun DownloadSettings(
    wifiOnly: Boolean,
    capBytes: Long,
    onWifiOnly: (Boolean) -> Unit,
    onCap: (Long) -> Unit,
) {
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(
            Modifier.fillMaxWidth().heightIn(min = TuvimaTokens.controlHeight),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Column(Modifier.weight(1f)) {
                Text("Download on Wi-Fi only", style = MaterialTheme.typography.titleSmall)
                Text("Wait for Wi-Fi instead of using mobile data.", style = MaterialTheme.typography.bodySmall)
            }
            Switch(checked = wifiOnly, onCheckedChange = onWifiOnly)
        }
        Text("Storage limit", style = MaterialTheme.typography.titleSmall)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            StorageCap.optionsGb.forEach { gb ->
                FilterChip(
                    selected = StorageCap.gbFor(capBytes) == gb,
                    onClick = { onCap(StorageCap.bytesForGb(gb)) },
                    label = { Text("$gb GB") },
                )
            }
        }
    }
}

@Composable
private fun DownloadRow(
    client: TuvimaClient,
    record: DownloadRecord,
    nowMs: Long,
    wifiOnly: Boolean,
    onPlay: () -> Unit,
    onView: () -> Unit,
    onPause: () -> Unit,
    onResume: () -> Unit,
    onDelete: () -> Unit,
    onNextEpisodes: () -> Unit,
) {
    val buttonHeight = Modifier.heightIn(min = TuvimaTokens.controlHeight)
    Card(Modifier.fillMaxWidth()) {
        Row(Modifier.padding(12.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            DownloadArtwork(
                client,
                record,
                Modifier.width(72.dp).aspectRatio(if (record.isPhoto || record.entityType.contains("track", true)) 1f else 2f / 3f)
                    .clip(RoundedCornerShape(8.dp)),
            )
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                Text(
                    record.title,
                    style = MaterialTheme.typography.titleSmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(
                    listOfNotNull(DownloadsPageLogic.typeLabel(record), record.subtitle).joinToString(" · "),
                    style = MaterialTheme.typography.bodySmall,
                    maxLines = 2,
                    overflow = TextOverflow.Ellipsis,
                )
                Text(DownloadsPageLogic.statusLine(record, nowMs, wifiOnly), style = MaterialTheme.typography.bodySmall)
                if (!record.isComplete) {
                    val fraction = record.fraction()
                    if (fraction != null) {
                        LinearProgressIndicator(progress = { fraction }, modifier = Modifier.fillMaxWidth())
                    } else if (record.state != DownloadState.Paused && record.state != DownloadState.Failed) {
                        LinearProgressIndicator(modifier = Modifier.fillMaxWidth())
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    when {
                        record.state == DownloadState.Complete && record.isPhoto ->
                            Button(onClick = onView, modifier = buttonHeight) { Text("View") }
                        record.state == DownloadState.Complete && !OfflineLogic.isExpired(record, nowMs) ->
                            Button(onClick = onPlay, modifier = buttonHeight) { Text("Play") }
                        DownloadsPageLogic.canPause(record) ->
                            OutlinedButton(onClick = onPause, modifier = buttonHeight) { Text("Pause") }
                        record.state == DownloadState.Paused ->
                            Button(onClick = onResume, modifier = buttonHeight) { Text("Resume") }
                        record.state == DownloadState.Failed ->
                            Button(onClick = onResume, modifier = buttonHeight) { Text("Retry") }
                        else -> Unit
                    }
                    TextButton(onClick = onDelete, modifier = buttonHeight) { Text(if (record.isComplete) "Delete" else "Cancel") }
                }
                if (record.isComplete && record.showId != null) {
                    TextButton(onClick = onNextEpisodes, modifier = buttonHeight) { Text("Download next episodes") }
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
