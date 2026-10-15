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
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.itemsIndexed
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.BrowseQuery
import com.tuvima.library.core.CollectionEntry
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewGallery
import com.tuvima.library.core.ViewItem
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

private enum class LibrarySection(val label: String) {
    Watch("Watch"),
    Read("Read"),
    Listen("Listen"),
    View("View"),
    Collections("Collections"),
}

/** Libraries: Watch, Read, Listen, View (photos) and Collections, switched with chips at the top. */
@Composable
internal fun LibrariesScreen(
    client: TuvimaClient,
    openCard: (DisplayCard) -> Unit,
    openCollection: (CollectionEntry) -> Unit,
    openViewer: (List<ViewItem>, Int) -> Unit,
    openGallery: (ViewGallery) -> Unit,
) {
    var section by remember { mutableStateOf(LibrarySection.Watch) }
    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp, vertical = 8.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            LibrarySection.entries.forEach { item ->
                FilterChip(
                    selected = section == item,
                    onClick = { section = item },
                    label = { Text(item.label) },
                )
            }
        }
        key(section) {
            when (section) {
                LibrarySection.Watch -> LaneBrowse(client, "watch", openCard)
                LibrarySection.Read -> LaneBrowse(client, "read", openCard)
                LibrarySection.Listen -> LaneBrowse(client, "listen", openCard)
                LibrarySection.View -> PhotosScreen(client, openViewer, openGallery)
                LibrarySection.Collections -> CollectionsScreen(client, openCollection)
            }
        }
    }
}

private data class LaneTab(val label: String, val mediaType: String, val grouping: String? = null)

// Each lane's tabs are the Dashboard's: Movies and TV, Books and Comics, Music (albums) and Audiobooks.
private fun laneTabs(lane: String): List<LaneTab> = when (lane) {
    "watch" -> listOf(LaneTab("Movies", "Movies"), LaneTab("TV", "TV", "shows"))
    "read" -> listOf(LaneTab("Books", "Books"), LaneTab("Comics", "Comics"))
    else -> listOf(LaneTab("Music", "Music", "timeline"), LaneTab("Audiobooks", "Audiobooks"))
}

// The Dashboard's sort choices for a card grid, and its narrower pair for the dated album timeline.
private val gridSortOptions = listOf(
    "newest" to "Newest",
    "title" to "A-Z",
    "oldest" to "Oldest",
    "creator" to "Creator",
    "year" to "Year",
)
private val timelineSortOptions = listOf("newest" to "Newest first", "oldest" to "Oldest first")

/** The cards of one lane tab, loaded a page at a time, with the sort and filter options applied by the Engine. */
@Stable
internal class BrowseController(
    private val client: TuvimaClient,
    private val scope: CoroutineScope,
    base: BrowseQuery,
) {
    var query by mutableStateOf(base)
        private set
    val cards = mutableStateListOf<DisplayCard>()
    var totalCount by mutableIntStateOf(0)
        private set
    var genres by mutableStateOf<List<String>>(emptyList())
        private set
    var creators by mutableStateOf<List<String>>(emptyList())
        private set
    var years by mutableStateOf<List<String>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    private var generation = 0

    fun reload(next: BrowseQuery = query) {
        query = next.copy(offset = 0)
        cards.clear()
        totalCount = 0
        fetch()
    }

    fun loadMore() {
        if (loading || error != null || cards.isEmpty() || cards.size >= totalCount) return
        query = query.copy(offset = cards.size)
        fetch()
    }

    private fun fetch() {
        generation += 1
        val mine = generation
        val requested = query
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.browsePage(requested)
                if (mine != generation) return@launch
                if (requested.offset == 0) {
                    cards.clear()
                    genres = page.genres
                    creators = page.creators
                    years = page.years
                }
                cards.addAll(page.cards)
                totalCount = page.totalCount
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

@Composable
private fun LaneBrowse(client: TuvimaClient, lane: String, openCard: (DisplayCard) -> Unit) {
    val tabs = remember(lane) { laneTabs(lane) }
    var tabIndex by remember(lane) { mutableIntStateOf(0) }
    val tab = tabs[tabIndex]
    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.horizontalScroll(rememberScrollState()).padding(horizontal = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            tabs.forEachIndexed { index, item ->
                FilterChip(selected = tabIndex == index, onClick = { tabIndex = index }, label = { Text(item.label) })
            }
        }
        key(lane, tabIndex) { BrowseGrid(client, lane, tab, openCard) }
    }
}

@Composable
private fun BrowseGrid(client: TuvimaClient, lane: String, tab: LaneTab, openCard: (DisplayCard) -> Unit) {
    val scope = rememberCoroutineScope()
    val browser = remember(lane, tab.mediaType) {
        BrowseController(
            client,
            scope,
            BrowseQuery(lane = lane, mediaType = tab.mediaType, grouping = tab.grouping),
        )
    }
    var showOptions by remember { mutableStateOf(false) }
    LaunchedEffect(browser) { browser.reload() }

    Column(Modifier.fillMaxSize()) {
        Row(
            Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            val count = browser.totalCount
            Text(
                if (count > 0) "$count titles" else "",
                style = MaterialTheme.typography.bodyMedium,
            )
            OutlinedButton(onClick = { showOptions = true }) {
                Text(if (browser.query.hasFilters) "Sort and filter (on)" else "Sort and filter")
            }
        }
        val failure = browser.error
        when {
            failure != null && browser.cards.isEmpty() -> ErrorBlock(failure) { browser.reload() }
            browser.cards.isEmpty() && browser.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            browser.cards.isEmpty() -> ErrorBlock("Nothing here yet.", null)
            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(minSize = 110.dp),
                contentPadding = PaddingValues(12.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(14.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                itemsIndexed(browser.cards) { index, card ->
                    if (index >= browser.cards.size - 6) {
                        LaunchedEffect(browser.cards.size) { browser.loadMore() }
                    }
                    PosterTile(client, card, Modifier.fillMaxWidth()) { openCard(card) }
                }
                if (browser.loading) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                            CircularProgressIndicator()
                        }
                    }
                }
            }
        }
    }

    if (showOptions) {
        BrowseOptionsSheet(
            current = browser.query,
            sortOptions = if (tab.grouping == "timeline") timelineSortOptions else gridSortOptions,
            genres = browser.genres,
            creators = browser.creators,
            years = browser.years,
            onApply = { next ->
                showOptions = false
                browser.reload(next)
            },
            onDismiss = { showOptions = false },
        )
    }
}
