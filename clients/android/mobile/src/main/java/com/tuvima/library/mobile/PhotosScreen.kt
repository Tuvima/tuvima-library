package com.tuvima.library.mobile

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.widget.Toast
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectTransformGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.ClientRoutes
import com.tuvima.library.core.DownloadRecord
import com.tuvima.library.core.DownloadState
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.PHOTO_ACCESS_DENIED_MESSAGE
import com.tuvima.library.core.TimelineRow
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewGalleries
import com.tuvima.library.core.ViewGallery
import com.tuvima.library.core.ViewItem
import com.tuvima.library.core.ViewTimelineMonth
import com.tuvima.library.core.buildFlatRows
import com.tuvima.library.core.buildTimelineRows
import com.tuvima.library.core.mergeTimelineMonths
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.io.File

/** The offline downloads of this phone: kept photos are read from here before the network is asked. */
@Composable
private fun offlineDownloads(): OfflineDownloads =
    (LocalContext.current.applicationContext as TuvimaMobileApplication).offline

/** The kept photos that are finished and on disk, by View item id, as the file to read. */
private fun keptPhotoPaths(records: List<DownloadRecord>): Map<String, String> =
    records.filter { it.isPhoto && it.isComplete }
        .mapNotNull { record -> record.filePath?.let { record.assetId to it } }
        .toMap()

/** View in Libraries: the photo and video timeline (month by month) and the person's galleries. */
@Composable
internal fun PhotosScreen(
    client: TuvimaClient,
    openViewer: (List<ViewItem>, Int) -> Unit,
    openGallery: (ViewGallery) -> Unit,
) {
    var showGalleries by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.padding(horizontal = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(selected = !showGalleries, onClick = { showGalleries = false }, label = { Text("Timeline") })
            FilterChip(selected = showGalleries, onClick = { showGalleries = true }, label = { Text("Galleries") })
        }
        if (showGalleries) GalleriesList(client, openGallery) else TimelineGrid(client, openViewer)
    }
}

/** Reads the timeline a page at a time and merges months that continue across pages. */
@Stable
private class TimelineController(private val client: TuvimaClient, private val scope: CoroutineScope) {
    var months by mutableStateOf<List<ViewTimelineMonth>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var loaded by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    private var cursor: String? = null
    private var hasMore = true

    fun loadMore() {
        if (loading || !hasMore) return
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.viewTimeline(cursor, 100)
                months = mergeTimelineMonths(months, page.months)
                cursor = page.nextCursor
                hasMore = page.hasMore && page.nextCursor != null
                loaded = true
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                error = userMessage(failure)
            } finally {
                loading = false
            }
        }
    }

    fun retry() {
        error = null
        loadMore()
    }
}

@Composable
private fun TimelineGrid(client: TuvimaClient, openViewer: (List<ViewItem>, Int) -> Unit) {
    val scope = rememberCoroutineScope()
    val controller = remember(client) { TimelineController(client, scope) }
    LaunchedEffect(controller) { controller.loadMore() }
    val columns = photoColumns()
    val months = controller.months
    val rows = remember(months, columns) { buildTimelineRows(months, columns) }
    val flat = remember(months) { months.flatMap { it.items } }
    val failure = controller.error
    // A photo-access refusal cannot be fixed by trying again; the person has to re-pair.
    val retry: () -> Unit = { controller.retry() }
    val retryAction: (() -> Unit)? = if (failure == PHOTO_ACCESS_DENIED_MESSAGE) null else retry
    // Photos kept on this phone are read from it first, and still browsable when the server cannot be reached.
    val kept by offlineDownloads().store().records.collectAsState()
    val keptPaths = remember(kept) { keptPhotoPaths(kept) }
    val keptItems = remember(kept) { keptPhotoItems(kept) }

    when {
        failure != null && flat.isEmpty() && keptItems.isNotEmpty() && failure != PHOTO_ACCESS_DENIED_MESSAGE ->
            KeptOnThisPhone(client, keptItems, keptPaths, columns, failure, retry, openViewer)
        failure != null && flat.isEmpty() -> ErrorBlock(failure, retryAction)
        flat.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        flat.isEmpty() -> ErrorBlock("No photos or videos yet.", null)
        else -> PhotoRows(
            client = client,
            rows = rows,
            columns = columns,
            keptPaths = keptPaths,
            onNearEnd = { if (controller.error == null) controller.loadMore() },
            onOpen = { item -> openViewer(flat, flat.indexOfFirst { it.id == item.id }.coerceAtLeast(0)) },
            footer = {
                if (controller.loading) CircularProgressIndicator()
                if (failure != null) {
                    Text(failure)
                    TextButton(onClick = { controller.retry() }) { Text("Try again") }
                }
            },
        )
    }
}

@Composable
private fun photoColumns(): Int = (LocalConfiguration.current.screenWidthDp / 110).coerceIn(3, 8)

/** What View shows when the server cannot be reached but photos were kept on this phone: those photos, from the phone. */
@Composable
private fun KeptOnThisPhone(
    client: TuvimaClient,
    items: List<ViewItem>,
    keptPaths: Map<String, String>,
    columns: Int,
    failure: String,
    retry: () -> Unit,
    openViewer: (List<ViewItem>, Int) -> Unit,
) {
    val rows = remember(items, columns) { buildFlatRows(items, columns) }
    Column(Modifier.fillMaxSize()) {
        Text(
            "$failure Showing the photos kept on this phone.",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(horizontal = 12.dp, vertical = 4.dp),
        )
        TextButton(onClick = retry) { Text("Try again") }
        PhotoRows(
            client = client,
            rows = rows,
            columns = columns,
            keptPaths = keptPaths,
            onNearEnd = {},
            onOpen = { item -> openViewer(items, items.indexOfFirst { it.id == item.id }.coerceAtLeast(0)) },
            footer = {},
        )
    }
}

/** Month headings and rows of square thumbnails, as one scrolling list. */
@Composable
private fun PhotoRows(
    client: TuvimaClient,
    rows: List<TimelineRow>,
    columns: Int,
    keptPaths: Map<String, String>,
    onNearEnd: () -> Unit,
    onOpen: (ViewItem) -> Unit,
    footer: @Composable () -> Unit,
) {
    LazyColumn(Modifier.fillMaxSize()) {
        itemsIndexed(rows) { index, row ->
            if (index >= rows.size - 3) {
                LaunchedEffect(rows.size) { onNearEnd() }
            }
            when (row) {
                is TimelineRow.Header -> Text(
                    row.label,
                    style = MaterialTheme.typography.titleMedium,
                    modifier = Modifier.padding(start = 12.dp, top = 16.dp, bottom = 6.dp),
                )
                is TimelineRow.Cells -> Row(Modifier.fillMaxWidth().padding(horizontal = 1.dp)) {
                    row.items.forEach { item ->
                        PhotoThumb(client, item, keptPaths[item.id], Modifier.weight(1f).aspectRatio(1f).padding(1.dp)) {
                            onOpen(item)
                        }
                    }
                    repeat(columns - row.items.size) { Spacer(Modifier.weight(1f)) }
                }
            }
        }
        item {
            Column(Modifier.fillMaxWidth().padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                footer()
            }
        }
    }
}

@Composable
private fun PhotoThumb(client: TuvimaClient, item: ViewItem, localPath: String?, modifier: Modifier, onClick: () -> Unit) {
    Box(modifier.clickable(onClick = onClick)) {
        // A photo kept on this phone is read from the phone; only the others ask the server.
        if (localPath != null) {
            LocalImage(localPath, 320, Modifier.fillMaxSize())
        } else {
            RemoteImage(client, ClientRoutes.viewThumbnail(item.id, 320), Modifier.fillMaxSize())
        }
        if (item.isVideo) {
            Text(
                "▶ " + formatDuration(item.durationSeconds),
                color = Color.White,
                style = MaterialTheme.typography.labelSmall,
                modifier = Modifier.align(Alignment.BottomStart).background(Color(0x99000000)).padding(horizontal = 4.dp),
            )
        }
    }
}

@Composable
private fun GalleriesList(client: TuvimaClient, openGallery: (ViewGallery) -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<ViewGalleries>>(Load.Loading) }
    LaunchedEffect(client, attempt) {
        state = Load.Loading
        state = try {
            Load.Ready(client.viewGalleries())
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            Load.Failed(userMessage(failure))
        }
    }
    val denied = (state as? Load.Failed)?.message == PHOTO_ACCESS_DENIED_MESSAGE
    LoadView(state, retry = { if (!denied) attempt += 1 }) { galleries ->
        if (galleries.owned.isEmpty() && galleries.sharedWithYou.isEmpty()) {
            ErrorBlock("No galleries yet.", null)
        } else {
            LazyColumn(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                if (galleries.owned.isNotEmpty()) {
                    item { Text("My galleries", style = MaterialTheme.typography.titleMedium) }
                    itemsIndexed(galleries.owned) { _, gallery -> GalleryRow(client, gallery, openGallery) }
                }
                if (galleries.sharedWithYou.isNotEmpty()) {
                    item { Text("Shared with you", style = MaterialTheme.typography.titleMedium) }
                    itemsIndexed(galleries.sharedWithYou) { _, gallery -> GalleryRow(client, gallery, openGallery) }
                }
            }
        }
    }
}

@Composable
private fun GalleryRow(client: TuvimaClient, gallery: ViewGallery, open: (ViewGallery) -> Unit) {
    Row(
        Modifier.fillMaxWidth().clickable { open(gallery) },
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RemoteImage(
            client,
            gallery.coverItemId?.let { ClientRoutes.viewThumbnail(it, 160) },
            Modifier.padding(vertical = 4.dp).aspectRatio(1f).weight(0.25f),
        )
        Column(Modifier.weight(0.75f)) {
            Text(gallery.name, style = MaterialTheme.typography.titleSmall)
            Text(
                if (gallery.itemCount == 1) "1 item" else "${gallery.itemCount} items",
                style = MaterialTheme.typography.bodySmall,
            )
        }
    }
}

/** The items of one gallery, newest first, as a plain grid with a way back. */
@Composable
internal fun GalleryScreen(
    client: TuvimaClient,
    gallery: ViewGallery,
    openViewer: (List<ViewItem>, Int) -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val controller = remember(gallery.id) { GalleryController(client, scope, gallery.id) }
    LaunchedEffect(controller) { controller.loadMore() }
    val columns = photoColumns()
    val items = controller.items
    val rows = remember(items, columns) { buildFlatRows(items, columns) }
    val failure = controller.error
    val context = LocalContext.current
    val offline = offlineDownloads()
    val kept by offline.store().records.collectAsState()
    val keptPaths = remember(kept) { keptPhotoPaths(kept) }
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("Back") }
            Text(gallery.name, style = MaterialTheme.typography.titleLarge, modifier = Modifier.weight(1f))
            TextButton(onClick = {
                scope.launch {
                    val message = try {
                        keepGalleryMessage(offline.keepGallery(client, gallery.id))
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (problem: Exception) {
                        userMessage(problem)
                    }
                    Toast.makeText(context, message, Toast.LENGTH_LONG).show()
                }
            }) { Text("Keep on this device") }
        }
        Text(
            "Keeping a gallery saves 2048 px previews, not the originals, to save space.",
            style = MaterialTheme.typography.bodySmall,
            modifier = Modifier.padding(horizontal = 12.dp),
        )
        when {
            failure != null && items.isEmpty() -> ErrorBlock(failure, null)
            items.isEmpty() && controller.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            items.isEmpty() -> ErrorBlock("This gallery has nothing you can see yet.", null)
            else -> PhotoRows(
                client = client,
                rows = rows,
                columns = columns,
                keptPaths = keptPaths,
                onNearEnd = { if (controller.error == null) controller.loadMore() },
                onOpen = { item -> openViewer(items, items.indexOfFirst { it.id == item.id }.coerceAtLeast(0)) },
                footer = { if (controller.loading) CircularProgressIndicator() },
            )
        }
    }
}

@Stable
private class GalleryController(
    private val client: TuvimaClient,
    private val scope: CoroutineScope,
    private val galleryId: String,
) {
    var items by mutableStateOf<List<ViewItem>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    private var cursor: String? = null
    private var hasMore = true

    fun loadMore() {
        if (loading || !hasMore) return
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.viewGalleryItems(galleryId, cursor, 100)
                val known = items.map { it.id }.toSet()
                items = items + page.items.filter { it.id !in known }
                cursor = page.nextCursor
                hasMore = page.hasMore && page.nextCursor != null
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                error = userMessage(failure)
            } finally {
                loading = false
            }
        }
    }
}

/**
 * Full-screen photo viewer: pinch to zoom, drag to move a zoomed photo, double-tap to reset, and previous and
 * next buttons. A video shows its still frame with its length; playing personal video is not part of this screen.
 * A photo kept on this phone is read from the phone first (no network); Keep on device saves the 2048 px preview of
 * the photo being viewed, and Remove deletes that copy.
 */
@Composable
internal fun PhotoViewerScreen(client: TuvimaClient, items: List<ViewItem>, startIndex: Int, onClose: () -> Unit) {
    if (items.isEmpty()) {
        ErrorBlock("Nothing to show.", onClose)
        return
    }
    var index by remember { mutableIntStateOf(startIndex.coerceIn(0, items.lastIndex)) }
    val item = items[index]
    var scale by remember(index) { mutableFloatStateOf(1f) }
    var offset by remember(index) { mutableStateOf(Offset.Zero) }
    val context = LocalContext.current
    val offline = offlineDownloads()
    val keptRecords by offline.store().records.collectAsState()
    val keptRecord = keptRecords.firstOrNull { it.isPhoto && it.assetId == item.id }
    // The local copy is used first when it is finished and its file is there; otherwise the server is asked.
    val localPath = OfflineLogic.localPhotoPath(keptRecord) { File(it).isFile }
    val bitmap by produceState<Bitmap?>(initialValue = null, key1 = item.id, key2 = localPath) {
        val local = localPath?.let { path -> withContext(Dispatchers.IO) { decodeLocalImage(path, 2048) } }
        value = local ?: runCatching {
            val bytes = client.viewPreview(item.id, 2048)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        }.getOrNull()
    }
    val image = bitmap

    Box(Modifier.fillMaxSize().background(Color.Black)) {
        if (image != null) {
            Image(
                bitmap = image.asImageBitmap(),
                contentDescription = item.displayTitle(),
                contentScale = ContentScale.Fit,
                modifier = Modifier
                    .fillMaxSize()
                    .pointerInput(index) {
                        detectTapGestures(onDoubleTap = {
                            scale = 1f
                            offset = Offset.Zero
                        })
                    }
                    .pointerInput(index) {
                        detectTransformGestures { _, pan, zoom, _ ->
                            scale = (scale * zoom).coerceIn(1f, 6f)
                            offset = if (scale <= 1.01f) Offset.Zero else offset + pan
                        }
                    }
                    .graphicsLayer(
                        scaleX = scale,
                        scaleY = scale,
                        translationX = offset.x,
                        translationY = offset.y,
                    ),
            )
        } else {
            CircularProgressIndicator(Modifier.align(Alignment.Center))
        }
        Row(
            Modifier.fillMaxWidth().align(Alignment.TopStart).background(Color(0x66000000)).padding(8.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            TextButton(onClick = onClose) { Text("Close", color = Color.White) }
            Column(Modifier.weight(1f)) {
                Text(item.displayTitle(), color = Color.White, maxLines = 1)
                val caption = listOfNotNull(
                    item.capturedAt?.take(10),
                    item.locationName,
                    if (item.isVideo) "Video " + formatDuration(item.durationSeconds) else null,
                ).joinToString(" · ")
                if (caption.isNotBlank()) {
                    Text(caption, color = Color.White, style = MaterialTheme.typography.bodySmall)
                }
            }
            if (!item.isVideo) {
                when {
                    keptRecord == null -> TextButton(onClick = {
                        Toast.makeText(context, photoKeepMessage(offline.keepPhoto(item)), Toast.LENGTH_LONG).show()
                    }) { Text("Keep on device", color = Color.White) }
                    keptRecord.state == DownloadState.Complete -> TextButton(onClick = {
                        offline.delete(item.id)
                        Toast.makeText(context, "Removed from this phone.", Toast.LENGTH_SHORT).show()
                    }) { Text("Remove from device", color = Color.White) }
                    keptRecord.state == DownloadState.Failed -> TextButton(onClick = {
                        Toast.makeText(context, photoKeepMessage(offline.keepPhoto(item)), Toast.LENGTH_LONG).show()
                    }) { Text("Try keeping again", color = Color.White) }
                    else -> Text("Keeping...", color = Color.White, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
        Row(
            Modifier.fillMaxWidth().align(Alignment.BottomCenter).background(Color(0x66000000)).padding(8.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            OutlinedButton(onClick = { if (index > 0) index -= 1 }, enabled = index > 0) { Text("Previous") }
            Text("${index + 1} of ${items.size}", color = Color.White)
            Button(onClick = { if (index < items.lastIndex) index += 1 }, enabled = index < items.lastIndex) { Text("Next") }
        }
    }
}
