package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
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
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.ListenLogic
import com.tuvima.library.core.ListenTab
import com.tuvima.library.core.SearchLogic
import com.tuvima.library.core.SongSort
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.playableAssetId
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch

// The Listen section on the phone: Songs, Albums, Artists, Audiobooks and Favorites, built from the lanes the Engine already returns
// (engineering/validation/native-listen-lanes-2026-11.md). There is no Playlists tab: the phone has no route for them yet.

/** Listen: the five tabs as chips that wrap rather than scroll sideways, and the pane under the picked one. */
@Composable
internal fun ListenScreen(
    client: TuvimaClient,
    play: (PlayRequest) -> Unit,
    openCard: (DisplayCard) -> Unit,
    openArtist: (String) -> Unit,
) {
    var tab by remember { mutableStateOf(ListenTab.Songs) }
    Column(Modifier.fillMaxSize()) {
        TabChipRows(ListenLogic.tabs(), tab, { it.label }, { tab = it }, perRow = 3)
        when (tab) {
            ListenTab.Songs -> SongsPane(client, play, artist = null)
            ListenTab.Albums -> AlbumsPane(client, openCard)
            ListenTab.Artists -> ArtistsPane(client, openArtist)
            ListenTab.Audiobooks -> BrowseGrid(client, "listen", LaneTab("Audiobooks", "Audiobooks"), openCard)
            ListenTab.Favorites -> FavoritesPane(client, play)
        }
    }
}

/** An artist's songs, opened from the Artists tab, with a way back. */
@Composable
internal fun ArtistSongsScreen(client: TuvimaClient, artist: String, play: (PlayRequest) -> Unit, onBack: () -> Unit) {
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack) { Text("Back") }
            Text(artist, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        }
        SongsPane(client, play, artist = artist)
    }
}

/** Songs a page at a time, with the person's search and sort applied by the Engine, optionally only one artist's. */
@Stable
internal class SongsController(
    private val client: TuvimaClient,
    private val scope: CoroutineScope,
    private val artist: String?,
) {
    val cards = mutableStateListOf<DisplayCard>()
    var total by mutableIntStateOf(0)
        private set
    var loading by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    private var search = ""
    private var sort = SongSort.Title
    private var generation = 0

    fun reload(search: String, sort: SongSort) {
        this.search = search
        this.sort = sort
        cards.clear()
        total = 0
        fetch(0)
    }

    fun loadMore() {
        if (loading || error != null || cards.isEmpty() || cards.size >= total) return
        fetch(cards.size)
    }

    private fun fetch(offset: Int) {
        generation += 1
        val mine = generation
        val query = ListenLogic.songsQuery(search, sort, artist, offset)
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.browsePage(query)
                if (mine != generation) return@launch
                val known = cards.mapTo(HashSet()) { it.id }
                cards.addAll(page.cards.filter { known.add(it.id) })
                total = page.totalCount
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
internal fun SongsPane(client: TuvimaClient, play: (PlayRequest) -> Unit, artist: String?) {
    val scope = rememberCoroutineScope()
    val controller = remember(client, artist) { SongsController(client, scope, artist) }
    val saved = LocalSavedItems.current
    var text by remember { mutableStateOf("") }
    var applied by remember { mutableStateOf("") }
    var sort by remember { mutableStateOf(SongSort.Title) }

    LaunchedEffect(saved) { saved?.ensureLoaded() }
    // Typing is sent once it pauses; the keyboard's search key sends it at once.
    LaunchedEffect(text) {
        if (text != applied) {
            delay(SearchLogic.DEBOUNCE_MS)
            applied = text
        }
    }
    LaunchedEffect(controller, applied, sort) { controller.reload(applied, sort) }

    Column(Modifier.fillMaxSize()) {
        OutlinedTextField(
            value = text,
            onValueChange = { text = it },
            placeholder = { Text(ListenLogic.SEARCH_SONGS) },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
            keyboardActions = KeyboardActions(onSearch = { applied = text }),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
        )
        TabChipRows(SongSort.entries.toList(), sort, { it.label }, { sort = it }, perRow = 3)
        val count = ListenLogic.songCountText(controller.total)
        if (count.isNotEmpty()) {
            Text(count, style = MaterialTheme.typography.bodyMedium, modifier = Modifier.padding(horizontal = 12.dp))
        }
        val failure = controller.error
        when {
            failure != null && controller.cards.isEmpty() -> ErrorBlock(failure) { controller.reload(applied, sort) }
            controller.cards.isEmpty() && controller.loading -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            controller.cards.isEmpty() ->
                ErrorBlock(if (applied.isNotBlank()) ListenLogic.NO_MATCHES else ListenLogic.EMPTY_SONGS, null)
            else -> LazyColumn(Modifier.fillMaxSize()) {
                if (saved?.changeFailed == true) {
                    item { Text(ListenLogic.COULD_NOT_CHANGE, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(12.dp)) }
                }
                itemsIndexed(controller.cards) { index, card ->
                    if (index >= controller.cards.size - 8) {
                        LaunchedEffect(controller.cards.size) { controller.loadMore() }
                    }
                    SongRow(client, card, play)
                }
                if (controller.loading) {
                    item {
                        Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
                    }
                }
            }
        }
    }
}

/** One song: its cover, title, artist and album, length and the Favorite heart. A press on the row plays it. */
@Composable
private fun SongRow(client: TuvimaClient, card: DisplayCard, play: (PlayRequest) -> Unit) {
    val saved = LocalSavedItems.current
    val hearted = ListenLogic.isHearted(card, saved?.titles)
    val line = ListenLogic.songLine(card)
    val length = ListenLogic.songLength(card)
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 60.dp)
            .clickable(onClickLabel = "Play ${card.title}") {
                card.playableAssetId()?.let { assetId -> play(PlayRequest(assetId, "music", card.title, line.ifEmpty { null })) }
            }
            .padding(start = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RemoteImage(client, card.squareArtUrl ?: card.posterUrl, Modifier.size(48.dp).clip(RoundedCornerShape(4.dp)))
        Column(Modifier.weight(1f)) {
            Text(card.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
            if (line.isNotEmpty()) {
                Text(line, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
            }
        }
        if (length.isNotEmpty()) Text(length, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
        val key = ListenLogic.songKey(card)
        if (key != null && saved != null) {
            BareIconButton(ListenLogic.heartLabel(hearted), { saved.toggle(key) }) {
                Text(
                    if (hearted) "♥" else "♡",
                    color = if (hearted) TuvimaTokens.accentPrimaryHover else TuvimaTokens.textPrimary,
                    fontSize = 22.sp,
                )
            }
        }
    }
}

/** A Listen shelf (Albums or Artists) read a page at a time by the cursor the Engine gives. */
@Stable
internal class ShelfController(
    private val client: TuvimaClient,
    private val scope: CoroutineScope,
    private val key: String,
) {
    val cards = mutableStateListOf<DisplayCard>()
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
        val offset = cards.size
        scope.launch {
            try {
                val page = client.listenShelfPage(key, cursor, offset)
                val known = cards.mapTo(HashSet()) { it.id }
                val fresh = page.shelf.items.filter { known.add(it.id) }
                cards.addAll(fresh)
                cursor = page.nextCursor
                // A page that added nothing new is the end, whatever the Engine says, so scrolling can never loop.
                hasMore = ListenLogic.hasNextShelfPage(page) && fresh.isNotEmpty()
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
private fun AlbumsPane(client: TuvimaClient, openCard: (DisplayCard) -> Unit) {
    val scope = rememberCoroutineScope()
    val shelf = remember(client) { ShelfController(client, scope, ListenLogic.SHELF_ALBUMS) }
    LaunchedEffect(shelf) { shelf.loadMore() }
    val failure = shelf.error
    when {
        failure != null && shelf.cards.isEmpty() -> ErrorBlock(failure) { shelf.retry() }
        shelf.cards.isEmpty() && !shelf.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        shelf.cards.isEmpty() -> ErrorBlock(ListenLogic.EMPTY_ALBUMS, null)
        else -> LazyVerticalGrid(
            columns = GridCells.Adaptive(minSize = 110.dp),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            itemsIndexed(shelf.cards) { index, card ->
                if (index >= shelf.cards.size - 6) {
                    LaunchedEffect(shelf.cards.size) { shelf.loadMore() }
                }
                PosterTile(client, card, Modifier.fillMaxWidth(), square = true) { openCard(card) }
            }
            if (shelf.loading || failure != null) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        if (failure != null) TextButton(onClick = { shelf.retry() }) { Text("Try again") } else CircularProgressIndicator()
                    }
                }
            }
        }
    }
}

@Composable
private fun ArtistsPane(client: TuvimaClient, openArtist: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    val shelf = remember(client) { ShelfController(client, scope, ListenLogic.SHELF_ARTISTS) }
    LaunchedEffect(shelf) { shelf.loadMore() }
    val failure = shelf.error
    when {
        failure != null && shelf.cards.isEmpty() -> ErrorBlock(failure) { shelf.retry() }
        shelf.cards.isEmpty() && !shelf.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
        shelf.cards.isEmpty() -> ErrorBlock(ListenLogic.EMPTY_ARTISTS, null)
        else -> LazyColumn(Modifier.fillMaxSize()) {
            itemsIndexed(shelf.cards) { index, card ->
                if (index >= shelf.cards.size - 8) {
                    LaunchedEffect(shelf.cards.size) { shelf.loadMore() }
                }
                Row(
                    Modifier
                        .fillMaxWidth()
                        .heightIn(min = 64.dp)
                        .clickable(onClickLabel = "Open ${card.title}") { openArtist(card.title) }
                        .padding(horizontal = 12.dp, vertical = 4.dp),
                    horizontalArrangement = Arrangement.spacedBy(12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RemoteImage(client, card.squareArtUrl ?: card.posterUrl, Modifier.size(56.dp).clip(CircleShape))
                    Column(Modifier.weight(1f)) {
                        Text(card.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
                        val line = ListenLogic.artistLine(card)
                        if (line.isNotEmpty()) {
                            Text(line, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
                        }
                    }
                }
            }
            if (shelf.loading || failure != null) {
                item {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        if (failure != null) TextButton(onClick = { shelf.retry() }) { Text("Try again") } else CircularProgressIndicator()
                    }
                }
            }
        }
    }
}

@Composable
private fun FavoritesPane(client: TuvimaClient, play: (PlayRequest) -> Unit) {
    val saved = LocalSavedItems.current
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<List<DisplayCard>>>(Load.Loading) }
    LaunchedEffect(saved) { saved?.ensureLoaded() }
    LaunchedEffect(client, attempt) {
        state = Load.Loading
        state = try {
            Load.Ready(client.favoriteSongs())
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            Load.Failed(userMessage(failure))
        }
    }
    LoadView(state, retry = { attempt += 1 }) { songs ->
        if (songs.isEmpty()) {
            ErrorBlock(ListenLogic.EMPTY_FAVORITES, null)
        } else {
            LazyColumn(Modifier.fillMaxSize()) {
                if (saved?.changeFailed == true) {
                    item { Text(ListenLogic.COULD_NOT_CHANGE, color = MaterialTheme.colorScheme.error, modifier = Modifier.padding(12.dp)) }
                }
                itemsIndexed(songs) { _, card -> SongRow(client, card, play) }
            }
        }
    }
}
