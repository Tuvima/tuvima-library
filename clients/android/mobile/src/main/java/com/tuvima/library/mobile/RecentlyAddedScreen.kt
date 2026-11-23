package com.tuvima.library.mobile

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.RecentlyAddedLogic
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/** The newest titles the profile may see, a page at a time by the Engine's cursor, optionally narrowed to Watch, Read or Listen. */
@Stable
internal class RecentController(private val client: TuvimaClient, private val scope: CoroutineScope) {
    val cards = mutableStateListOf<DisplayCard>()
    var type by mutableStateOf<String?>(null)
        private set
    var availableTypes by mutableStateOf<List<String>?>(null)
        private set
    var hasMore by mutableStateOf(false)
        private set
    var loading by mutableStateOf(true)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    private var cursor: String? = null
    private var generation = 0

    fun pickType(value: String?) {
        if (value == type) return
        type = value
        reload()
    }

    fun reload() {
        generation += 1
        val mine = generation
        cards.clear()
        cursor = null
        hasMore = false
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.recentPage(type = type)
                if (mine != generation) return@launch
                cards.addAll(RecentlyAddedLogic.merge(emptyList(), page.cards))
                cursor = page.nextCursor
                hasMore = page.hasMore && page.nextCursor != null
                // The filter list comes with the first page of the unfiltered view and stays while a filter is on.
                if (type == null) availableTypes = page.availableTypes
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                if (mine == generation) error = userMessage(failure)
            } finally {
                if (mine == generation) loading = false
            }
        }
    }

    /** Asks again for the page that failed. */
    fun retryMore() {
        error = null
        loadMore()
    }

    fun loadMore() {
        val next = cursor
        if (loading || !hasMore || next == null || error != null) return
        val mine = generation
        loading = true
        scope.launch {
            try {
                val page = client.recentPage(type = type, cursor = next)
                if (mine != generation) return@launch
                val merged = RecentlyAddedLogic.merge(cards.toList(), page.cards)
                cards.clear()
                cards.addAll(merged)
                cursor = page.nextCursor
                hasMore = page.hasMore && page.nextCursor != null
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                if (mine == generation) error = userMessage(failure)
            } finally {
                if (mine == generation) loading = false
            }
        }
    }
}

/** Recently added: a poster grid of the newest titles with Watch, Read and Listen filters when more than one has something. */
@Composable
internal fun RecentlyAddedScreen(client: TuvimaClient, saved: SavedItems, openCard: (DisplayCard) -> Unit) {
    val scope = rememberCoroutineScope()
    val recent = remember(client) { RecentController(client, scope) }
    LaunchedEffect(recent) {
        saved.ensureLoaded()
        recent.reload()
    }
    val full: LazyGridItemSpanScope.() -> GridItemSpan = { GridItemSpan(maxLineSpan) }

    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 110.dp),
        contentPadding = PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        item(span = full) {
            Text(RecentlyAddedLogic.TITLE, style = MaterialTheme.typography.headlineMedium)
        }
        val chips = RecentlyAddedLogic.typeChips(recent.availableTypes)
        if (chips.isNotEmpty()) {
            item(span = full) {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    FilterChip(selected = recent.type == null, onClick = { recent.pickType(null) }, label = { Text(AllLabel) })
                    chips.forEach { (value, label) ->
                        FilterChip(selected = recent.type == value, onClick = { recent.pickType(value) }, label = { Text(label) })
                    }
                }
            }
        }
        val failure = recent.error
        if (failure != null && recent.cards.isEmpty()) {
            item(span = full) {
                Column(Modifier.fillMaxWidth().padding(vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(failure)
                    Button(onClick = { recent.reload() }) { Text("Try again") }
                }
            }
        } else if (recent.loading && recent.cards.isEmpty()) {
            item(span = full) {
                Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            }
        } else if (recent.cards.isEmpty()) {
            item(span = full) { Text(RecentlyAddedLogic.EMPTY) }
        }
        itemsIndexed(recent.cards) { index, card ->
            if (index >= recent.cards.size - 6) {
                LaunchedEffect(recent.cards.size) { recent.loadMore() }
            }
            PosterTile(client, card, Modifier.fillMaxWidth()) { openCard(card) }
        }
        if (recent.cards.isNotEmpty() && failure != null) {
            item(span = full) {
                TextButton(onClick = { recent.retryMore() }) { Text("Try again") }
            }
        }
    }
}

private const val AllLabel = "All"
