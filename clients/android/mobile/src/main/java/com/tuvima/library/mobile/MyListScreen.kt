package com.tuvima.library.mobile

import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.DisplayShelf
import com.tuvima.library.core.MyListKind
import com.tuvima.library.core.MyListLogic
import com.tuvima.library.core.MyListSort
import com.tuvima.library.core.SavedKey
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.UndoOffer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

/** The saved titles of My List's grid, a page at a time, with the sort and kind the person picked. */
@Stable
internal class MyListController(private val client: TuvimaClient, private val scope: CoroutineScope) {
    /** Null until the person picks one: the Engine then answers with the sort it remembers for this profile. */
    var sort by mutableStateOf<MyListSort?>(null)
        private set
    var kind by mutableStateOf<String?>(null)
        private set
    val cards = mutableStateListOf<DisplayCard>()
    var kinds by mutableStateOf<List<MyListKind>>(emptyList())
        private set
    var total by mutableIntStateOf(0)
        private set
    var shownSort by mutableStateOf(MyListSort.Added)
        private set
    var hasMore by mutableStateOf(false)
        private set
    var loading by mutableStateOf(true)
        private set
    var loadingMore by mutableStateOf(false)
        private set
    var moreFailed by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    /** How many saved titles are finished (what Remove finished would take); -1 until known. */
    var finished by mutableIntStateOf(-1)
        private set

    private var generation = 0

    fun pickKind(value: String?) {
        if (value == kind) return
        kind = value
        reload()
    }

    fun pickSort(value: MyListSort) {
        if (value == sort) return
        sort = value
        reload()
    }

    /** Reads the first page. A silent read keeps the covers on screen until the new ones arrive (after a removal or Undo). */
    fun reload(silent: Boolean = false) {
        generation += 1
        val mine = generation
        val limit = if (silent) cards.size.coerceIn(MyListLogic.PAGE_SIZE, 100) else MyListLogic.PAGE_SIZE
        if (!silent) {
            cards.clear()
            hasMore = false
            loading = true
        }
        error = null
        moreFailed = false
        loadingMore = false
        scope.launch {
            try {
                val page = client.myListPage(sort, kind, 0, limit)
                if (mine != generation) return@launch
                cards.clear()
                cards.addAll(page.cards)
                kinds = page.kinds
                total = page.totalCount
                hasMore = page.hasMore
                shownSort = page.sort
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                if (mine == generation) error = userMessage(failure)
            } finally {
                if (mine == generation) loading = false
            }
            refreshFinished(mine)
        }
    }

    fun loadMore() {
        if (loading || loadingMore || !hasMore || error != null || cards.isEmpty()) return
        val mine = generation
        val offset = cards.size
        loadingMore = true
        moreFailed = false
        scope.launch {
            try {
                val page = client.myListPage(sort, kind, offset, MyListLogic.PAGE_SIZE)
                if (mine != generation) return@launch
                val known = cards.mapTo(HashSet()) { it.id }
                cards.addAll(page.cards.filter { known.add(it.id) })
                hasMore = page.hasMore
                total = page.totalCount
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                if (mine == generation) moreFailed = true
            } finally {
                if (mine == generation) loadingMore = false
            }
        }
    }

    private suspend fun refreshFinished(mine: Int) {
        val count = try {
            client.finishedSavedItems().size
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            -1
        }
        if (mine == generation) finished = count
    }
}

/**
 * My List: the person's saved titles as a poster grid, with Continue above, kind chips, a sort, Remove finished in the "..." menu and an
 * Undo line after any removal. A tick on a poster takes the title off the list. Left out of this version: Select (the tick covers one
 * title at a time), the profile links and the Downloads card (Downloads is in the top bar).
 */
@Composable
internal fun MyListScreen(
    client: TuvimaClient,
    saved: SavedItems,
    openCard: (DisplayCard) -> Unit,
    goHome: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val list = remember(client) { MyListController(client, scope) }
    var undo by remember { mutableStateOf<UndoOffer?>(null) }
    var continueShelf by remember { mutableStateOf<DisplayShelf?>(null) }
    var sortMenu by remember { mutableStateOf(false) }
    var pageMenu by remember { mutableStateOf(false) }
    var changeFailed by remember { mutableStateOf(false) }

    LaunchedEffect(list) {
        saved.load()
        list.reload()
    }
    // Any plus, tick, removal or Undo (here or elsewhere) changes what this list should show, so it is read again in place.
    var seenVersion by remember { mutableIntStateOf(saved.version) }
    LaunchedEffect(saved.version) {
        if (saved.version != seenVersion) {
            seenVersion = saved.version
            list.reload(silent = true)
        }
    }
    LaunchedEffect(client) {
        // Best effort: without Continue the list is still complete.
        continueShelf = runCatching { client.continueAll() }.getOrNull()
            ?.shelves?.firstOrNull { it.items.isNotEmpty() }
            ?.copy(key = "continue", title = MyListLogic.CONTINUE)
    }
    LaunchedEffect(undo) {
        if (undo != null) {
            delay(MyListLogic.UNDO_SECONDS * 1000L)
            undo = null
        }
    }

    // Takes titles off My List and offers Undo for exactly what the Engine took.
    fun remove(keys: List<SavedKey>) {
        if (keys.isEmpty()) return
        changeFailed = false
        scope.launch {
            try {
                val removed = client.removeSavedItems(keys)
                // The change bumps the saved version, which reads the grid again.
                saved.applied(removed, emptyList())
                undo = UndoOffer.forRemoved(removed)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                changeFailed = true
            }
        }
    }

    fun removeFinished() {
        pageMenu = false
        changeFailed = false
        scope.launch {
            try {
                val finished = client.finishedSavedItems().take(500)
                if (finished.isEmpty()) {
                    list.reload(silent = true)
                    return@launch
                }
                val removed = client.removeSavedItems(finished.map { it.key })
                saved.applied(removed, emptyList())
                undo = UndoOffer.forRemoved(removed)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                changeFailed = true
            }
        }
    }

    fun undoNow(offer: UndoOffer) {
        undo = null
        changeFailed = false
        scope.launch {
            try {
                val restored = client.restoreSavedItems(offer.removed)
                saved.applied(emptyList(), restored)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                changeFailed = true
            }
        }
    }

    val full: androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> GridItemSpan = { GridItemSpan(maxLineSpan) }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(minSize = 110.dp),
        contentPadding = PaddingValues(12.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
        modifier = Modifier.fillMaxSize(),
    ) {
        item(span = full) {
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(MyListLogic.TITLE, style = MaterialTheme.typography.headlineMedium, modifier = Modifier.weight(1f))
                Box {
                    TextButton(onClick = { sortMenu = true }) { Text("${MyListLogic.SORT_BY}: ${list.shownSort.label}") }
                    DropdownMenu(expanded = sortMenu, onDismissRequest = { sortMenu = false }) {
                        MyListSort.entries.forEach { choice ->
                            DropdownMenuItem(
                                text = { Text(if (choice == list.shownSort) "✓ ${choice.label}" else choice.label) },
                                onClick = {
                                    sortMenu = false
                                    list.pickSort(choice)
                                },
                            )
                        }
                    }
                }
                Box {
                    BareIconButton("More actions for My List", { pageMenu = true }) {
                        Text("⋯", color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleLarge)
                    }
                    DropdownMenu(expanded = pageMenu, onDismissRequest = { pageMenu = false }) {
                        DropdownMenuItem(
                            text = { Text(if (list.finished == 0) MyListLogic.NOTHING_FINISHED else MyListLogic.REMOVE_FINISHED) },
                            enabled = list.finished != 0,
                            onClick = { removeFinished() },
                        )
                    }
                }
            }
        }

        continueShelf?.let { shelf ->
            item(span = full) { ShelfRow(client, shelf, openCard) }
        }

        if (MyListLogic.showsChips(list.kinds)) {
            item(span = full) {
                Row(
                    Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    FilterChip(
                        selected = list.kind == null,
                        onClick = { list.pickKind(null) },
                        label = { Text("${MyListLogic.ALL} ${MyListLogic.allCount(list.kinds, list.total)}") },
                    )
                    MyListLogic.visibleKinds(list.kinds).forEach { kind ->
                        FilterChip(
                            selected = list.kind.equals(kind.value, ignoreCase = true),
                            onClick = { list.pickKind(kind.value) },
                            label = { Text("${kind.label} ${kind.count}") },
                        )
                    }
                }
            }
        }

        if (!list.loading && list.error == null && list.total > 0) {
            item(span = full) {
                Text(MyListLogic.countText(list.total, list.kind), style = MaterialTheme.typography.bodyMedium)
            }
        }

        undo?.let { offer ->
            item(span = full) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(offer.message, modifier = Modifier.weight(1f))
                    TextButton(onClick = { undoNow(offer) }) { Text(MyListLogic.UNDO) }
                }
            }
        }

        if (changeFailed || saved.changeFailed) {
            item(span = full) { Text(MyListLogic.COULD_NOT_CHANGE, color = MaterialTheme.colorScheme.error) }
        }

        val failure = list.error
        when {
            failure != null -> item(span = full) {
                Row(
                    Modifier.fillMaxWidth().padding(vertical = 16.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text("${MyListLogic.COULD_NOT_LOAD}. $failure", modifier = Modifier.weight(1f))
                    Button(onClick = { list.reload() }) { Text("Try again") }
                }
            }
            list.loading && list.cards.isEmpty() -> item(span = full) {
                Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            }
            list.cards.isEmpty() -> item(span = full) {
                androidx.compose.foundation.layout.Column(
                    Modifier.fillMaxWidth().padding(vertical = 24.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    Text(MyListLogic.EMPTY_TITLE, style = MaterialTheme.typography.titleMedium)
                    Text(MyListLogic.EMPTY_MESSAGE, style = MaterialTheme.typography.bodyMedium)
                    Button(onClick = goHome) { Text(MyListLogic.BROWSE_HOME) }
                }
            }
        }

        itemsIndexed(list.cards) { index, card ->
            if (index >= list.cards.size - 6) {
                LaunchedEffect(list.cards.size) { list.loadMore() }
            }
            PosterTile(client, card, Modifier.fillMaxWidth(), onSavedPress = { key ->
                // A tick removes (with Undo); a plus, on a card that is no longer saved, adds it back.
                if (saved.titles?.contains(key) == true) remove(listOf(key)) else saved.toggle(key)
            }) { openCard(card) }
        }

        if (list.loadingMore || list.moreFailed) {
            item(span = full) {
                Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                    if (list.moreFailed) {
                        TextButton(onClick = { list.loadMore() }) { Text("Try again") }
                    } else {
                        CircularProgressIndicator()
                    }
                }
            }
        }
    }
}
