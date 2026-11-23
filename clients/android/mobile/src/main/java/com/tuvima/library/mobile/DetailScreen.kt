package com.tuvima.library.mobile

import android.content.ActivityNotFoundException
import android.content.Context
import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.clickable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DetailMenuAction
import com.tuvima.library.core.DetailMenuLogic
import com.tuvima.library.core.DetailModel
import com.tuvima.library.core.BatchScope
import com.tuvima.library.core.DownloadBadgeLogic
import com.tuvima.library.core.DownloadKind
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.EditInBrowser
import com.tuvima.library.core.OfflineBatchLogic
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.playShowId
import com.tuvima.library.core.savedKeyForEntity
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/**
 * A title's details: poster, title, year, length, synopsis, cast, editions, and Play or Resume with Download.
 * [assetId] is the file to play when the person arrived from a card that knows it; without one the buttons are
 * hidden because API v1's details answer does not name a file. Download asks for a quality first (books, comics and
 * photos are not offered), and a show also offers the next episodes. A title already on the phone shows its state.
 */
@Composable
internal fun DetailScreen(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    entityType: String,
    id: String,
    assetId: String?,
    onBack: () -> Unit,
    play: (PlayRequest) -> Unit,
    /** True when Edit is offered: not a Kids profile (the shell fails closed to Kids until the profile is known). */
    canEdit: Boolean = false,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val offline = application.offline
    val downloads by offline.store().records.collectAsState()
    val kind = remember(entityType) { OfflineLogic.downloadKindFor(entityType) }
    val isShow = remember(entityType) { OfflineLogic.isShowEntity(entityType) }
    var showQualityPicker by remember { mutableStateOf(false) }
    var showAllPicker by remember { mutableStateOf(false) }
    // What the last Download press did, shown in words under the top line (no toast).
    var downloadNote by remember { mutableStateOf<String?>(null) }
    var selectedQuality by remember(kind) {
        mutableStateOf(OfflineLogic.defaultQualityFor(kind, application.downloadQuality()))
    }
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<DetailModel>>(Load.Loading) }

    fun startDownload(target: String, quality: String) {
        val current = state
        val detail = if (current is Load.Ready) current.value else null
        val result = offline.enqueue(
            DownloadRequest(
                assetId = target,
                title = detail?.title ?: "Download",
                subtitle = detail?.subtitle,
                artworkUrl = detail?.artworkUrl,
                // A show page's play file is an episode and an album page's a song; keep it as that so it gets the Downloaded mark.
                entityType = DownloadBadgeLogic.recordedType(entityType),
                showId = if (isShow) id else null,
                profileKey = quality,
                // The Details id is the title's own work id only when this page is one file (not a show, season or album).
                workId = if (DownloadBadgeLogic.isSingleFile(entityType)) id else null,
            ),
        )
        downloadNote = enqueueMessage(result)
    }

    // A whole show, season, album, playlist or audiobook: the files this page lists, from the page itself (no new Engine route).
    fun startBatch(choice: BatchScope, quality: String) {
        val current = state
        val detail = if (current is Load.Ready) current.value else null
        downloadNote = "Starting the downloads…"
        // Queuing writes one record per file to disk, so it is done off the main thread.
        scope.launch {
            val outcome = withContext(Dispatchers.IO) {
                offline.enqueueBatch(detail?.title, detail?.showId ?: if (isShow) id else null, choice.children, quality)
            }
            downloadNote = if (outcome.notPaired) {
                enqueueMessage(EnqueueResult.NotPaired)
            } else {
                OfflineBatchLogic.message(outcome.plan, OfflineBatchLogic.noun(choice.children, outcome.plan.toQueue.size))
            }
        }
    }

    // A download that failed keeps the part already on the phone, like Retry on the Downloads page: it continues instead of starting over.
    fun retryDownload(target: String) {
        offline.resume(target)
        downloadNote = "Trying this download again. Track it under Downloads."
    }

    fun downloadNextEpisodes(after: String) {
        val quality = OfflineLogic.defaultQualityFor(DownloadKind.Video, application.downloadQuality())
        scope.launch {
            val message = try {
                nextEpisodesMessage(offline.downloadNextEpisodes(client, id, after, quality))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                userMessage(failure)
            }
            downloadNote = message
        }
    }

    LaunchedEffect(client, entityType, id, attempt) {
        state = Load.Loading
        state = try {
            Load.Ready(client.detailPage(entityType, id))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            Load.Failed(userMessage(failure))
        }
    }
    // The top line: Back on the left; on the right the My List plus or tick and the "..." menu (no Edit or Add to collection in version 1).
    val savedItems = LocalSavedItems.current
    val loaded = (state as? Load.Ready)?.value
    val savedKey = loaded?.let { savedKeyForEntity(entityType, id, it.showId) }
    val savedTitles = savedItems?.titles
    val canSave = savedKey != null && savedTitles != null
    val isSaved = canSave && savedKey != null && savedTitles != null && savedKey in savedTitles
    val heldCopy = assetId?.let { target -> downloads.firstOrNull { it.assetId == target } }
    // Edit opens the Dashboard in the phone's browser on this title's own page (spec native-apps D7). The link is only the page: it never opens the editor.
    val editUrl = if (canEdit) {
        EditInBrowser.pageUrl(client.currentOrigin, entityType, id, showId = playShowId(entityType, id, loaded?.showId))
    } else {
        null
    }
    val menuItems = DetailMenuLogic.items(
        canSave = canSave,
        isSaved = isSaved,
        canDownload = assetId != null && kind != DownloadKind.Excluded,
        downloadLabel = when {
            heldCopy == null -> "Download"
            heldCopy.state == DownloadState.Complete -> "Downloaded"
            heldCopy.state == DownloadState.Failed -> "Retry download"
            else -> "Downloading"
        },
        downloadEnabled = heldCopy == null || heldCopy.state == DownloadState.Failed,
        canDownloadNextEpisodes = isShow,
        canDownloadAll = OfflineBatchLogic.offersBatch(loaded?.children.orEmpty()),
        canEdit = editUrl != null,
    )
    var menuOpen by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("Back") }
            Spacer(Modifier.weight(1f))
            SavedToggle(savedKey)
            if (DetailMenuLogic.showsButton(menuItems)) {
                Box {
                    BareIconButton("More actions", { menuOpen = true }) {
                        Text("⋯", color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleLarge)
                    }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        menuItems.forEach { item ->
                            DropdownMenuItem(
                                text = { Text(item.label) },
                                enabled = item.enabled,
                                onClick = {
                                    menuOpen = false
                                    when (item.action) {
                                        DetailMenuAction.ToggleMyList -> {
                                            if (savedKey != null) savedItems?.toggle(savedKey)
                                        }
                                        DetailMenuAction.Download -> {
                                            if (assetId != null && heldCopy?.state == DownloadState.Failed) {
                                                retryDownload(assetId)
                                            } else {
                                                showQualityPicker = true
                                            }
                                        }
                                        DetailMenuAction.DownloadNextEpisodes -> {
                                            if (assetId != null) downloadNextEpisodes(assetId)
                                        }
                                        DetailMenuAction.DownloadAll -> {
                                            showAllPicker = true
                                        }
                                        DetailMenuAction.EditInBrowser -> {
                                            downloadNote = editUrl?.let { openInBrowser(context, it) }
                                        }
                                    }
                                },
                            )
                        }
                    }
                }
            }
        }
        if (DownloadBadgeLogic.pageShowsBadge(entityType, assetId, heldCopy?.entityType, LocalDownloadedAssets.current)) {
            DownloadedBadge(Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        }
        downloadNote?.let { note ->
            Text(
                note,
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            )
        }
        LoadView(state, retry = { attempt += 1 }) { detail ->
            LazyColumn(
                Modifier.fillMaxSize().padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item {
                    Row(horizontalArrangement = Arrangement.spacedBy(16.dp)) {
                        RemoteImage(
                            client,
                            detail.artworkUrl,
                            Modifier.width(120.dp).aspectRatio(2f / 3f).clip(RoundedCornerShape(10.dp)),
                        )
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            Text(detail.title, style = MaterialTheme.typography.headlineSmall)
                            detail.subtitle?.let { Text(it, style = MaterialTheme.typography.titleMedium) }
                            val facts = detail.factLine()
                            if (facts.isNotBlank()) Text(facts, style = MaterialTheme.typography.bodyMedium)
                            if (detail.genres.isNotEmpty()) {
                                Text(detail.genres.joinToString(" · "), style = MaterialTheme.typography.bodySmall)
                            }
                        }
                    }
                }
                item {
                    if (assetId != null) {
                      Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            val held = downloads.firstOrNull { it.assetId == assetId }
                            Button(onClick = { play(PlayRequest(assetId, entityType, detail.title, detail.subtitle, showId = playShowId(entityType, id, detail.showId), workId = PhonePlayerLogic.workIdFor(entityType, id))) }) { Text(if (detail.isInProgress) "Resume" else "Play") }
                            if (kind != DownloadKind.Excluded) {
                                OutlinedButton(
                                    onClick = {
                                        if (held?.state == DownloadState.Failed) {
                                            retryDownload(assetId)
                                        } else {
                                            showQualityPicker = true
                                        }
                                    },
                                    enabled = held == null || held.state == DownloadState.Failed,
                                ) {
                                    Text(
                                        when {
                                            held == null -> "Download"
                                            held.state == DownloadState.Complete -> "Downloaded"
                                            held.state == DownloadState.Failed -> "Retry download"
                                            else -> "Downloading"
                                        },
                                    )
                                }
                            }
                        }
                        if (isShow && kind != DownloadKind.Excluded) {
                            OutlinedButton(onClick = { downloadNextEpisodes(assetId) }) { Text("Download next episodes") }
                        }
                        if (OfflineBatchLogic.offersBatch(detail.children)) {
                            OutlinedButton(onClick = { showAllPicker = true }) { Text(DetailMenuLogic.DOWNLOAD_ALL_LABEL) }
                        }
                      }
                    } else {
                        if (OfflineBatchLogic.offersBatch(detail.children)) {
                            OutlinedButton(onClick = { showAllPicker = true }) { Text(DetailMenuLogic.DOWNLOAD_ALL_LABEL) }
                        } else {
                            Text(
                                "Play and Download appear when you open a title from Home, a Library or a Collection.",
                                style = MaterialTheme.typography.bodySmall,
                            )
                        }
                    }
                }
                detail.tagline?.let { tagline -> item { Text(tagline, style = MaterialTheme.typography.titleSmall) } }
                detail.description?.let { text -> item { Text(text, style = MaterialTheme.typography.bodyMedium) } }
                if (detail.cast.isNotEmpty()) {
                    item { Text("Cast", style = MaterialTheme.typography.titleMedium) }
                    item {
                        Column(verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            detail.cast.forEach { credit ->
                                Text(
                                    if (credit.role != null) "${credit.name} · ${credit.role}" else credit.name,
                                    style = MaterialTheme.typography.bodyMedium,
                                )
                            }
                        }
                    }
                }
                if (detail.editions.isNotEmpty()) {
                    item { Text("Editions", style = MaterialTheme.typography.titleMedium) }
                    detail.editions.forEach { edition ->
                        item {
                            Card(Modifier.fillMaxWidth()) {
                                Column(Modifier.padding(12.dp)) {
                                    Text(edition.title, style = MaterialTheme.typography.titleSmall)
                                    edition.details?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                                }
                            }
                        }
                    }
                }
                item { Row(Modifier.padding(bottom = 24.dp)) {} }
            }
        }
    }

    if (showAllPicker && loaded != null) {
        DownloadAllDialog(
            groups = loaded.children,
            preferredQuality = application.downloadQuality(),
            onStart = { choice, quality ->
                showAllPicker = false
                startBatch(choice, quality)
            },
            onDismiss = { showAllPicker = false },
        )
    }

    if (showQualityPicker && assetId != null) {
        AlertDialog(
            onDismissRequest = { showQualityPicker = false },
            title = { Text("Download quality") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                    OfflineLogic.qualityChoicesFor(kind).forEach { choice ->
                        Row(
                            Modifier.fillMaxWidth().clickable { selectedQuality = choice.key },
                            verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = selectedQuality == choice.key, onClick = { selectedQuality = choice.key })
                            Text(choice.label)
                        }
                    }
                    Text(
                        "Larger files use more of your storage limit. Copies expire after a while and can be downloaded again.",
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
            },
            confirmButton = {
                Button(onClick = {
                    showQualityPicker = false
                    startDownload(assetId, selectedQuality)
                }) { Text("Download") }
            },
            dismissButton = { TextButton(onClick = { showQualityPicker = false }) { Text("Cancel") } },
        )
    }
}

/** Opens [url] in the phone's browser. Returns the words to show when no browser could be opened, or null when it was. */
private fun openInBrowser(context: Context, url: String): String? = try {
    context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
    null
} catch (missing: ActivityNotFoundException) {
    "No browser app was found on this phone to open the Dashboard."
}
