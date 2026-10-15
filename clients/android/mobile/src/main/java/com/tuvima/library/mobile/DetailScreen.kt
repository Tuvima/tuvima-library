package com.tuvima.library.mobile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import android.widget.Toast
import androidx.compose.foundation.clickable
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Card
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
import com.tuvima.library.core.DetailModel
import com.tuvima.library.core.DownloadKind
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

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
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val offline = application.offline
    val downloads by offline.store().records.collectAsState()
    val kind = remember(entityType) { OfflineLogic.downloadKindFor(entityType) }
    val isShow = remember(entityType) { OfflineLogic.isShowEntity(entityType) }
    var showQualityPicker by remember { mutableStateOf(false) }
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
                entityType = entityType,
                showId = if (isShow) id else null,
                profileKey = quality,
            ),
        )
        Toast.makeText(context, enqueueMessage(result), Toast.LENGTH_LONG).show()
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
            Toast.makeText(context, message, Toast.LENGTH_LONG).show()
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
    Column(Modifier.fillMaxSize()) {
        TextButton(onClick = onBack) { Text("Back") }
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
                            Button(onClick = { play(PlayRequest(assetId, entityType, detail.title, detail.subtitle)) }) { Text(if (detail.isInProgress) "Resume" else "Play") }
                            if (kind != DownloadKind.Excluded) {
                                OutlinedButton(
                                    onClick = { showQualityPicker = true },
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
                      }
                    } else {
                        Text(
                            "Play and Download appear when you open a title from Home, a Library or a Collection.",
                            style = MaterialTheme.typography.bodySmall,
                        )
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
