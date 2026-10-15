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
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.CollectionEntry
import com.tuvima.library.core.CollectionItem
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.UniverseDetail
import com.tuvima.library.core.groupTypeLabel
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** Reads a paged list from the Engine, one cursor at a time. */
@Stable
private class PagedList<T>(
    private val scope: CoroutineScope,
    private val fetch: suspend (cursor: String?) -> PageResult<T>,
) {
    var items by mutableStateOf<List<T>>(emptyList())
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
        if (loading || !hasMore || error != null) return
        loading = true
        scope.launch {
            try {
                val page = fetch(cursor)
                items = items + page.items
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

private class PageResult<T>(val items: List<T>, val nextCursor: String?, val hasMore: Boolean)

/** The Collections catalogue: one tile per show, series, collection or list the profile may see. */
@Composable
internal fun CollectionsScreen(client: TuvimaClient, openCollection: (CollectionEntry) -> Unit) {
    val scope = rememberCoroutineScope()
    val list = remember(client) {
        PagedList<CollectionEntry>(scope) { cursor ->
            val page = client.collectionsPage(cursor, 24)
            PageResult(page.items, page.nextCursor, page.hasMore)
        }
    }
    LaunchedEffect(list) { list.loadMore() }
    val failure = list.error
    when {
        failure != null && list.items.isEmpty() -> ErrorBlock(failure) { list.retry() }
        list.items.isEmpty() && !list.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        list.items.isEmpty() -> ErrorBlock("No collections yet.", null)
        else -> LazyVerticalGrid(
            columns = GridCells.Adaptive(minSize = 150.dp),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            itemsIndexed(list.items) { index, entry ->
                if (index >= list.items.size - 4) {
                    LaunchedEffect(list.items.size) { list.loadMore() }
                }
                CollectionTile(client, entry) { openCollection(entry) }
            }
            if (list.loading) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        CircularProgressIndicator()
                    }
                }
            }
        }
    }
}

@Composable
private fun CollectionTile(client: TuvimaClient, entry: CollectionEntry, onClick: () -> Unit) {
    Column(Modifier.clickable(onClick = onClick)) {
        RemoteImage(
            client,
            entry.coverUrls.firstOrNull(),
            Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(10.dp)),
        )
        Text(
            entry.name,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.titleSmall,
            modifier = Modifier.padding(top = 6.dp),
        )
        val count = if (entry.itemCount == 1) "1 item" else "${entry.itemCount} items"
        Text("${entry.kindLabel()} · $count", style = MaterialTheme.typography.bodySmall)
    }
}

/**
 * One collection's items in the collection's own order. [openDetail] gets the item's details route and the file
 * to play when the group's cards know one.
 */
@Composable
internal fun CollectionDetailScreen(
    client: TuvimaClient,
    entry: CollectionEntry,
    openDetail: (entityType: String, id: String, assetId: String?) -> Unit,
    openUniverse: (String) -> Unit,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val list = remember(entry.id) {
        PagedList<CollectionItem>(scope) { cursor ->
            val page = client.collectionItems(entry.id, cursor, 24)
            PageResult(page.items, page.nextCursor, page.hasMore)
        }
    }
    // A collection's item list names works but not files; the group's cards carry the file to play for each work.
    var playable by remember(entry.id) { mutableStateOf<Map<String, String>>(emptyMap()) }
    LaunchedEffect(list) { list.loadMore() }
    LaunchedEffect(entry.id) {
        playable = client.playableAssetsByWork(entry.id)
    }
    val failure = list.error
    Column(Modifier.fillMaxSize()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("Back") }
            Column(Modifier.weight(1f)) {
                Text(entry.name, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(entry.kindLabel(), style = MaterialTheme.typography.bodySmall)
            }
        }
        entry.universeQid()?.let { qid ->
            TextButton(onClick = { openUniverse(qid) }, modifier = Modifier.padding(start = 8.dp)) {
                Text("See the Universe shelves")
            }
        }
        entry.description?.let {
            Text(it, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        }
        when {
            failure != null && list.items.isEmpty() -> ErrorBlock(failure) { list.retry() }
            list.items.isEmpty() && !list.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            list.items.isEmpty() -> ErrorBlock("Nothing in this collection that you can see.", null)
            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(minSize = 110.dp),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                itemsIndexed(list.items) { index, item ->
                    if (index >= list.items.size - 6) {
                        LaunchedEffect(list.items.size) { list.loadMore() }
                    }
                    Column(
                        Modifier.clickable {
                            val target = item.detailTarget()
                            openDetail(target.first, target.second, item.workId?.let { playable[it] })
                        },
                    ) {
                        RemoteImage(
                            client,
                            item.coverUrl,
                            Modifier.fillMaxWidth().aspectRatio(2f / 3f).clip(RoundedCornerShape(8.dp)),
                        )
                        Text(
                            item.title,
                            maxLines = 1,
                            overflow = TextOverflow.Ellipsis,
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(top = 4.dp),
                        )
                        item.creator?.let { Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall) }
                    }
                }
                if (list.loading) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator()
                        }
                    }
                }
            }
        }
    }
}

/** A Universe: the series and shows that belong to one franchise, each as a shelf. */
@Composable
internal fun UniverseScreen(
    client: TuvimaClient,
    qid: String,
    openCard: (DisplayCard) -> Unit,
    onBack: () -> Unit,
) {
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<UniverseDetail>>(Load.Loading) }
    LaunchedEffect(client, qid, attempt) {
        state = Load.Loading
        state = try {
            Load.Ready(client.universe(qid))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            Load.Failed(userMessage(failure))
        }
    }
    Column(Modifier.fillMaxSize()) {
        TextButton(onClick = onBack) { Text("Back") }
        LoadView(state, retry = { attempt += 1 }) { universe ->
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(bottom = 24.dp)) {
                item {
                    Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                        Text(universe.name, style = MaterialTheme.typography.headlineMedium)
                        Text(
                            "Universe · ${universe.itemCount} items",
                            style = MaterialTheme.typography.bodyMedium,
                        )
                        universe.description?.let { Text(it, modifier = Modifier.padding(top = 8.dp)) }
                    }
                }
                items(universe.shelves) { member ->
                    Column(Modifier.padding(top = 16.dp)) {
                        Text(
                            "${groupTypeLabel(member.groupType)} · ${member.shelf.title} (${member.itemCount})",
                            style = MaterialTheme.typography.titleMedium,
                            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                        )
                        LazyRow(
                            contentPadding = PaddingValues(horizontal = 16.dp),
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                        ) {
                            items(member.shelf.items) { card ->
                                PosterTile(client, card, Modifier.width(120.dp)) { openCard(card) }
                            }
                        }
                    }
                }
            }
        }
    }
}
