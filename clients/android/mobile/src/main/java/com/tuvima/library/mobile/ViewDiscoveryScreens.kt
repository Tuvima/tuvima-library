package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.ClientRoutes
import com.tuvima.library.core.PHOTO_ACCESS_DENIED_MESSAGE
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewDiscoveryCapability
import com.tuvima.library.core.ViewDiscoveryLogic
import com.tuvima.library.core.ViewItem
import com.tuvima.library.core.ViewPerson
import com.tuvima.library.core.ViewPlace
import com.tuvima.library.core.buildFlatRows
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

// View's People and Places on the phone, and one person's photos. All of it is the signed-in person's own photos and videos; a Kids
// profile has no View section, and the Engine refuses these routes to a profile without View access.

/** One page of a cursor-paged list: the rows, the cursor for the next page (null at the end) and the Engine's own explanation. */
internal data class CursorPage<T>(val items: List<T>, val nextCursor: String?, val capability: ViewDiscoveryCapability?)

/** A list read a page at a time by the cursor the Engine gives. [merge] drops a row that arrives twice. */
@Stable
internal class CursorController<T>(
    private val scope: CoroutineScope,
    private val fetch: suspend (cursor: String?) -> CursorPage<T>,
    private val merge: (List<T>, List<T>) -> List<T>,
) {
    var items by mutableStateOf<List<T>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var loaded by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set
    var capability by mutableStateOf<ViewDiscoveryCapability?>(null)
        private set

    private var cursor: String? = null
    private var hasMore = true

    fun loadMore() {
        if (loading || !hasMore) return
        loading = true
        error = null
        scope.launch {
            try {
                val page = fetch(cursor)
                val before = items.size
                items = merge(items, page.items)
                capability = page.capability ?: capability
                cursor = page.nextCursor
                // A page that added nothing, or gave no cursor, is the end, so scrolling can never loop.
                hasMore = page.nextCursor != null && items.size > before
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

/** The people in the photos, as round pictures with a name and a count; a press opens that person's photos. */
@Composable
internal fun PeopleList(client: TuvimaClient, openPerson: (ViewPerson) -> Unit) {
    val scope = rememberCoroutineScope()
    val controller = remember(client) {
        CursorController<ViewPerson>(
            scope,
            fetch = { cursor ->
                val page = client.viewPeoplePage(cursor)
                CursorPage(page.items, ViewDiscoveryLogic.cursorFor(page), page.capability)
            },
            merge = { shown, incoming -> ViewDiscoveryLogic.mergePeople(shown, incoming) },
        )
    }
    LaunchedEffect(controller) { controller.loadMore() }
    val failure = controller.error
    val denied = failure == PHOTO_ACCESS_DENIED_MESSAGE
    when {
        failure != null && controller.items.isEmpty() -> ErrorBlock(failure, if (denied) null else ({ controller.retry() }))
        controller.items.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        controller.items.isEmpty() ->
            ErrorBlock(ViewDiscoveryLogic.emptyText(controller.capability, ViewDiscoveryLogic.EMPTY_PEOPLE), null)
        else -> LazyVerticalGrid(
            columns = GridCells.Adaptive(minSize = 100.dp),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            itemsIndexed(controller.items) { index, person ->
                if (index >= controller.items.size - 6) {
                    LaunchedEffect(controller.items.size) { controller.loadMore() }
                }
                val name = ViewDiscoveryLogic.personName(person)
                Column(
                    Modifier.fillMaxWidth().clickable(onClickLabel = "Open $name") { openPerson(person) },
                    horizontalAlignment = Alignment.CenterHorizontally,
                ) {
                    RemoteImage(
                        client,
                        person.representativeItemId?.let { ClientRoutes.viewThumbnail(it, 320) },
                        Modifier.fillMaxWidth().aspectRatio(1f).clip(CircleShape),
                    )
                    Text(
                        name,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodyMedium,
                        modifier = Modifier.padding(top = 4.dp),
                    )
                    Text(
                        ViewDiscoveryLogic.countText(person.assetCount),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                        style = MaterialTheme.typography.bodySmall,
                        color = TuvimaTokens.textMuted,
                    )
                }
            }
            if (controller.loading || failure != null) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        if (failure != null && !denied) TextButton(onClick = { controller.retry() }) { Text("Try again") } else CircularProgressIndicator()
                    }
                }
            }
        }
    }
}

/**
 * The places the photos cluster around: a picture, the place name and how many photos and videos. A place is shown, not opened: the
 * Engine has no read of one place's photos for the phone yet.
 */
@Composable
internal fun PlacesList(client: TuvimaClient) {
    val scope = rememberCoroutineScope()
    val controller = remember(client) {
        CursorController<ViewPlace>(
            scope,
            fetch = { cursor ->
                val page = client.viewPlacesPage(cursor)
                CursorPage(page.items, ViewDiscoveryLogic.cursorFor(page), page.capability)
            },
            merge = { shown, incoming -> ViewDiscoveryLogic.mergePlaces(shown, incoming) },
        )
    }
    LaunchedEffect(controller) { controller.loadMore() }
    val failure = controller.error
    val denied = failure == PHOTO_ACCESS_DENIED_MESSAGE
    when {
        failure != null && controller.items.isEmpty() -> ErrorBlock(failure, if (denied) null else ({ controller.retry() }))
        controller.items.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        controller.items.isEmpty() ->
            ErrorBlock(ViewDiscoveryLogic.emptyText(controller.capability, ViewDiscoveryLogic.EMPTY_PLACES), null)
        else -> LazyColumn(Modifier.fillMaxSize()) {
            itemsIndexed(controller.items) { index, place ->
                if (index >= controller.items.size - 8) {
                    LaunchedEffect(controller.items.size) { controller.loadMore() }
                }
                Row(
                    Modifier.fillMaxWidth().heightIn(min = 64.dp).padding(horizontal = 12.dp, vertical = 4.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RemoteImage(
                        client,
                        place.representativeItemId?.let { ClientRoutes.viewThumbnail(it, 160) },
                        Modifier.size(56.dp).clip(RoundedCornerShape(8.dp)),
                    )
                    Column(Modifier.weight(1f)) {
                        Text(
                            ViewDiscoveryLogic.placeName(place),
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.bodyLarge,
                        )
                        Text(
                            ViewDiscoveryLogic.countText(place.assetCount),
                            style = MaterialTheme.typography.bodySmall,
                            color = TuvimaTokens.textMuted,
                        )
                    }
                }
            }
            if (controller.loading || failure != null) {
                item {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        if (failure != null && !denied) TextButton(onClick = { controller.retry() }) { Text("Try again") } else CircularProgressIndicator()
                    }
                }
            }
        }
    }
}

/** The photos and videos that carry one person's name, newest first, as a plain grid with a way back. */
@Composable
internal fun PersonPhotosScreen(
    client: TuvimaClient,
    person: ViewPerson,
    openViewer: (List<ViewItem>, Int) -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val controller = remember(client, person.key) {
        CursorController<ViewItem>(
            scope,
            fetch = { cursor ->
                val page = client.viewPersonItems(person.key, cursor)
                CursorPage(page.items, if (page.hasMore) page.nextCursor else null, null)
            },
            merge = { shown, incoming ->
                val known = shown.mapTo(HashSet()) { it.id }
                shown + incoming.filter { known.add(it.id) }
            },
        )
    }
    LaunchedEffect(controller) { controller.loadMore() }
    val columns = photoColumns()
    val items = controller.items
    val rows = remember(items, columns) { buildFlatRows(items, columns) }
    val kept by offlineDownloads().store().records.collectAsState()
    val keptPaths = remember(kept) { keptPhotoPaths(kept) }
    val failure = controller.error
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("Back") }
            Text(
                ViewDiscoveryLogic.personName(person),
                style = MaterialTheme.typography.titleLarge,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
        }
        when {
            failure != null && items.isEmpty() ->
                ErrorBlock(failure, if (failure == PHOTO_ACCESS_DENIED_MESSAGE) null else ({ controller.retry() }))
            items.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            items.isEmpty() -> ErrorBlock(ViewDiscoveryLogic.NO_ITEMS, null)
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
