package com.tuvima.library.tv

import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.ClientRoutes
import com.tuvima.library.core.CollectionEntry
import com.tuvima.library.core.CollectionItem
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.DisplayPage
import com.tuvima.library.core.DisplayShelf
import com.tuvima.library.core.PHOTO_ACCESS_DENIED_MESSAGE
import com.tuvima.library.core.PairingPollResult
import com.tuvima.library.core.PairingSession
import com.tuvima.library.core.PhotoAccessDeniedException
import com.tuvima.library.core.PhotoScopes
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.ViewItem
import com.tuvima.library.core.playableAssetId
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val application = application as TuvimaTvApplication
        setContent {
            MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF9B7BFF))) {
                TuvimaTvRoot(application) { assetId ->
                    startActivity(Intent(this, PlaybackActivity::class.java).putExtra(PlaybackActivity.ASSET_ID, assetId))
                }
            }
        }
    }
}

/** The rows of the home page that come from calls other than /display/home. */
private data class TvExtras(
    val continueShelf: DisplayShelf? = null,
    val collections: List<CollectionEntry> = emptyList(),
    val photos: List<ViewItem> = emptyList(),
    val photoNote: String? = null,
)

private sealed interface TvState {
    data object Server : TvState
    data class Loading(val message: String) : TvState
    data class Pairing(val session: PairingSession) : TvState
    data class Library(val page: DisplayPage, val extras: TvExtras = TvExtras()) : TvState
    data class Search(val query: String, val results: List<TvSearchResult>) : TvState
    data class Detail(val detail: TvDetail, val playableAssetId: String?) : TvState
    data class CollectionItems(
        val entry: CollectionEntry,
        val items: List<CollectionItem>,
        val nextCursor: String?,
        val playable: Map<String, String>,
    ) : TvState
    data class Photo(val items: List<ViewItem>, val index: Int) : TvState
    data class Error(val message: String) : TvState
}

private data class TvSearchResult(
    val id: String,
    val entityType: String,
    val title: String,
    val subtitle: String?,
)

private data class TvDetail(
    val title: String,
    val subtitle: String?,
    val description: String?,
    val facts: String,
)

private val FocusBorderColor = Color(0xFFFFFFFF)

@Composable
private fun TuvimaTvRoot(application: TuvimaTvApplication, play: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var state: TvState by remember { mutableStateOf(TvState.Server) }
    val history = remember { mutableListOf<TvState>() }
    var activeClient: TuvimaClient? by remember { mutableStateOf(null) }

    // Shows a loading page, then the result; the page the person came from is kept so Back returns to it.
    fun navigate(message: String, replaceCurrent: Boolean = false, block: suspend () -> TvState) {
        val previous = state
        state = TvState.Loading(message)
        scope.launch {
            val next = try {
                block()
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                TvState.Error(userMessage(error))
            }
            if (!replaceCurrent) history.add(previous)
            state = next
        }
    }

    fun back() {
        if (history.isNotEmpty()) state = history.removeAt(history.lastIndex)
    }

    fun openDetail(entityType: String, entityId: String, playableAssetId: String?) {
        navigate("Loading details…") {
            TvState.Detail(parseDetail(activeClient!!.details(entityType, entityId)), playableAssetId)
        }
    }

    fun search(query: String) {
        if (query.isBlank()) return
        navigate("Searching…", replaceCurrent = state is TvState.Search) {
            TvState.Search(query, parseSearch(activeClient!!.search(query)))
        }
    }

    fun openCollection(entry: CollectionEntry) {
        navigate("Loading ${entry.name}…") {
            val api = activeClient!!
            val page = api.collectionItems(entry.id, null, 48)
            TvState.CollectionItems(
                entry,
                page.items,
                if (page.hasMore) page.nextCursor else null,
                api.playableAssetsByWork(entry.id),
            )
        }
    }

    fun moreCollectionItems(current: TvState.CollectionItems) {
        val cursor = current.nextCursor ?: return
        navigate("Loading more…", replaceCurrent = true) {
            val page = activeClient!!.collectionItems(current.entry.id, cursor, 48)
            current.copy(
                items = current.items + page.items,
                nextCursor = if (page.hasMore) page.nextCursor else null,
            )
        }
    }

    fun showHome(client: TuvimaClient) {
        history.clear()
        state = TvState.Loading("Loading your library…")
        scope.launch {
            state = try {
                loadHome(client)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                TvState.Error(userMessage(error))
            }
        }
    }

    fun connect(address: String) {
        history.clear()
        state = TvState.Loading("Connecting to Tuvima Library…")
        scope.launch {
            runCatching {
                application.connect(address).also { client ->
                    activeClient = client
                    client.discover()
                    if (client.isPaired()) {
                        state = loadHome(client)
                    } else {
                        state = TvState.Pairing(
                            client.beginPairing(PhotoScopes.requested(DEFAULT_SCOPES, application.photosOptIn())),
                        )
                    }
                }
            }.onFailure { state = TvState.Error(it.message ?: "The server could not be reached.") }
        }
    }

    LaunchedEffect(Unit) { application.savedServer()?.let(::connect) }
    BackHandler(enabled = history.isNotEmpty() && state !is TvState.Loading) { back() }

    Box(Modifier.fillMaxSize().background(Color(0xFF09080D))) {
        when (val current = state) {
            TvState.Server -> ServerScreen(application.photosOptIn()) { address, photos ->
                application.setPhotosOptIn(photos)
                connect(address)
            }
            is TvState.Loading -> LoadingScreen(current.message)
            is TvState.Pairing -> PairingScreen(activeClient, current.session) { result ->
                when (result) {
                    is PairingPollResult.Authorized -> showHome(activeClient!!)
                    is PairingPollResult.Failed -> state = TvState.Error(result.description ?: result.code)
                    is PairingPollResult.Pending -> Unit
                }
            }
            is TvState.Library -> LibraryScreen(
                current.page,
                current.extras,
                activeClient!!,
                openLane = { lane ->
                    navigate("Loading $lane…") { TvState.Library(activeClient!!.browse(lane)) }
                },
                onSearch = ::search,
                openDetail = ::openDetail,
                openCollection = ::openCollection,
                openPhoto = { items, index -> history.add(current); state = TvState.Photo(items, index) },
            )
            is TvState.Search -> SearchScreen(current, ::search) { result ->
                openDetail(result.entityType, result.id, null)
            }
            is TvState.Detail -> DetailScreen(current, play) { back() }
            is TvState.CollectionItems -> CollectionItemsScreen(
                activeClient!!,
                current,
                onBack = { back() },
                onOpen = { item ->
                    val target = item.detailTarget()
                    openDetail(target.first, target.second, item.workId?.let { current.playable[it] })
                },
                onMore = { moreCollectionItems(current) },
            )
            is TvState.Photo -> PhotoScreen(activeClient!!, current.items, current.index) { back() }
            is TvState.Error -> ErrorScreen(
                current.message,
                onBack = if (history.isNotEmpty()) ({ back() }) else null,
            ) {
                activeClient?.signOut()
                history.clear()
                state = TvState.Server
            }
        }
    }
}

/** Home: Continue first (from /display/continue), the Home shelves, then Collections and the photo timeline. */
private suspend fun loadHome(client: TuvimaClient): TvState.Library {
    val home = client.homeShelves()
    val continueShelf = runCatching { client.continueAll() }.getOrNull()
        ?.shelves?.firstOrNull { it.items.isNotEmpty() }
        ?.copy(key = "continue", title = "Continue")
    val collections = runCatching { client.collectionsPage(null, 20).items }.getOrDefault(emptyList())
    var photoNote: String? = null
    val photos: List<ViewItem> = if (!client.hasPhotoAccess()) {
        photoNote = PHOTO_ACCESS_DENIED_MESSAGE
        emptyList()
    } else {
        try {
            client.viewTimeline(null, 30).months.flatMap { it.items }.take(30)
        } catch (denied: PhotoAccessDeniedException) {
            photoNote = PHOTO_ACCESS_DENIED_MESSAGE
            emptyList()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            emptyList()
        }
    }
    val page = home.copy(shelves = home.shelves.filter { it.key != "continue" && it.items.isNotEmpty() })
    return TvState.Library(page, TvExtras(continueShelf, collections, photos, photoNote))
}

private fun userMessage(error: Throwable): String =
    if (error is PhotoAccessDeniedException) PHOTO_ACCESS_DENIED_MESSAGE else error.message ?: "Something went wrong."

@Composable
private fun ServerScreen(savedPhotos: Boolean, onConnect: (String, Boolean) -> Unit) {
    var address by remember { mutableStateOf("") }
    var photos by remember { mutableStateOf(savedPhotos) }
    Column(
        Modifier.fillMaxSize().padding(horizontal = 96.dp, vertical = 72.dp),
        verticalArrangement = Arrangement.Center,
    ) {
        Text("TUVIMA LIBRARY", style = MaterialTheme.typography.displayMedium)
        Spacer(Modifier.height(16.dp))
        Text("Enter the HTTPS address shown in Settings → Network & Remote Access.")
        Spacer(Modifier.height(28.dp))
        OutlinedTextField(
            value = address,
            onValueChange = { address = it },
            label = { Text("Dashboard address") },
            placeholder = { Text("https://library.example") },
            singleLine = true,
            keyboardOptions = KeyboardOptions(imeAction = ImeAction.Done),
            modifier = Modifier.fillMaxWidth(0.65f),
        )
        Spacer(Modifier.height(16.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = photos, onCheckedChange = { photos = it })
            Text("Allow access to my photos and videos (optional)")
        }
        Spacer(Modifier.height(20.dp))
        Button(onClick = { if (address.isNotBlank()) onConnect(address, photos) }) { Text("Connect") }
    }
}

@Composable
private fun PairingScreen(
    client: TuvimaClient?,
    session: PairingSession,
    onResult: (PairingPollResult) -> Unit,
) {
    LaunchedEffect(client, session.deviceCode) {
        if (client == null) return@LaunchedEffect
        var waitSeconds = session.pollingIntervalSeconds
        val deadline = System.currentTimeMillis() + session.expiresInSeconds * 1000L
        while (System.currentTimeMillis() < deadline) {
            delay(waitSeconds * 1000L)
            when (val result = client.pollPairing(session)) {
                is PairingPollResult.Pending -> waitSeconds = result.retryAfterSeconds
                else -> { onResult(result); return@LaunchedEffect }
            }
        }
        onResult(PairingPollResult.Failed("expired_token", "The pairing code expired."))
    }
    Column(Modifier.fillMaxSize().padding(96.dp), verticalArrangement = Arrangement.Center) {
        Text("PAIR THIS TELEVISION", style = MaterialTheme.typography.displaySmall)
        Spacer(Modifier.height(20.dp))
        Text("On a signed-in browser, open:")
        Text(session.verificationUri, style = MaterialTheme.typography.headlineSmall, color = MaterialTheme.colorScheme.primary)
        Spacer(Modifier.height(28.dp))
        Text(session.userCode, style = MaterialTheme.typography.displayLarge)
        Spacer(Modifier.height(20.dp))
        Text("Waiting for approval…")
    }
}

/**
 * A tile the remote can land on: it takes focus, shows a white border while focused, and opens on select.
 */
@Composable
private fun FocusCard(modifier: Modifier, onClick: () -> Unit, content: @Composable () -> Unit) {
    var focused by remember { mutableStateOf(false) }
    val shape = RoundedCornerShape(12.dp)
    Card(
        modifier = modifier
            .onFocusChanged { focused = it.isFocused }
            .border(
                width = if (focused) 4.dp else 0.dp,
                color = if (focused) FocusBorderColor else Color.Transparent,
                shape = shape,
            )
            .clickable(onClick = onClick),
        colors = CardDefaults.cardColors(containerColor = Color(0xFF241D32)),
        shape = shape,
    ) {
        content()
    }
}

/** A titled row of focusable tiles. */
@Composable
private fun TvRow(title: String, subtitle: String? = null, content: LazyListScope.() -> Unit) {
    Column(Modifier.padding(bottom = 28.dp)) {
        Text(title, style = MaterialTheme.typography.headlineMedium, modifier = Modifier.padding(horizontal = 64.dp))
        subtitle?.let { Text(it, modifier = Modifier.padding(horizontal = 64.dp)) }
        Spacer(Modifier.height(12.dp))
        LazyRow(
            contentPadding = PaddingValues(horizontal = 64.dp),
            horizontalArrangement = Arrangement.spacedBy(18.dp),
            content = content,
        )
    }
}

@Composable
private fun LibraryScreen(
    page: DisplayPage,
    extras: TvExtras,
    client: TuvimaClient,
    openLane: (String) -> Unit,
    onSearch: (String) -> Unit,
    openDetail: (String, String, String?) -> Unit,
    openCollection: (CollectionEntry) -> Unit,
    openPhoto: (List<ViewItem>, Int) -> Unit,
) {
    var query by remember { mutableStateOf("") }
    val openCard: (DisplayCard) -> Unit = { card ->
        val target = cardDetailTarget(card)
        openDetail(target.first, target.second, card.playableAssetId())
    }
    LazyColumn(contentPadding = PaddingValues(bottom = 64.dp)) {
        item {
            Column(Modifier.padding(horizontal = 64.dp, vertical = 32.dp)) {
                Text(page.title.uppercase(), style = MaterialTheme.typography.displaySmall)
                page.subtitle?.let { Text(it) }
                Spacer(Modifier.height(20.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    listOf("watch", "read", "listen").forEach { lane ->
                        Button(onClick = { openLane(lane) }) { Text(lane.uppercase()) }
                    }
                }
                Spacer(Modifier.height(16.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = query,
                        onValueChange = { query = it },
                        label = { Text("Search") },
                        singleLine = true,
                        modifier = Modifier.fillMaxWidth(0.55f),
                    )
                    Button(onClick = { onSearch(query) }) { Text("SEARCH") }
                }
            }
        }
        extras.continueShelf?.let { shelf ->
            item {
                TvRow(shelf.title, shelf.subtitle) {
                    items(shelf.items) { card -> MediaCard(card, client) { openCard(card) } }
                }
            }
        }
        page.shelves.forEach { shelf ->
            item {
                TvRow(shelf.title, shelf.subtitle) {
                    items(shelf.items) { card -> MediaCard(card, client) { openCard(card) } }
                }
            }
        }
        if (extras.collections.isNotEmpty()) {
            item {
                TvRow("Collections", "Shows, series, collections and lists from your library") {
                    items(extras.collections) { entry -> CollectionCard(entry, client) { openCollection(entry) } }
                }
            }
        }
        if (extras.photos.isNotEmpty()) {
            item {
                TvRow("View", "Your photos and videos, newest first") {
                    itemsIndexed(extras.photos) { index, photo ->
                        PhotoCard(photo, client) { openPhoto(extras.photos, index) }
                    }
                }
            }
        } else {
            extras.photoNote?.let { note ->
                item {
                    Column(Modifier.padding(horizontal = 64.dp, vertical = 8.dp)) {
                        Text("View", style = MaterialTheme.typography.headlineMedium)
                        Text(note, style = MaterialTheme.typography.bodyMedium)
                    }
                }
            }
        }
    }
}

@Composable
private fun MediaCard(card: DisplayCard, client: TuvimaClient, open: () -> Unit) {
    FocusCard(Modifier.size(width = 260.dp, height = 178.dp), open) {
        Box(Modifier.fillMaxSize()) {
            card.artworkUrl?.let { AuthenticatedArtwork(client, it) }
            Column(
                Modifier.fillMaxWidth().align(Alignment.BottomStart)
                    .background(Color(0xCC09080D)).padding(12.dp),
            ) {
                Text(card.title, maxLines = 1)
                card.facts.take(2).joinToString(" · ").takeIf(String::isNotBlank)?.let {
                    Text(it, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}

@Composable
private fun CollectionCard(entry: CollectionEntry, client: TuvimaClient, open: () -> Unit) {
    FocusCard(Modifier.size(width = 260.dp, height = 178.dp), open) {
        Box(Modifier.fillMaxSize()) {
            entry.coverUrls.firstOrNull()?.let { AuthenticatedArtwork(client, it) }
            Column(
                Modifier.fillMaxWidth().align(Alignment.BottomStart)
                    .background(Color(0xCC09080D)).padding(12.dp),
            ) {
                Text(entry.name, maxLines = 1)
                Text(
                    "${entry.kindLabel()} · ${entry.itemCount} items",
                    style = MaterialTheme.typography.bodySmall,
                )
            }
        }
    }
}

@Composable
private fun PhotoCard(photo: ViewItem, client: TuvimaClient, open: () -> Unit) {
    FocusCard(Modifier.size(178.dp), open) {
        Box(Modifier.fillMaxSize()) {
            AuthenticatedArtwork(client, ClientRoutes.viewThumbnail(photo.id, 320))
            if (photo.isVideo) {
                Text(
                    "VIDEO",
                    style = MaterialTheme.typography.labelSmall,
                    modifier = Modifier.align(Alignment.BottomStart).background(Color(0xCC09080D)).padding(6.dp),
                )
            }
        }
    }
}

@Composable
private fun CollectionItemsScreen(
    client: TuvimaClient,
    state: TvState.CollectionItems,
    onBack: () -> Unit,
    onOpen: (CollectionItem) -> Unit,
    onMore: () -> Unit,
) {
    Column(Modifier.fillMaxSize().padding(horizontal = 64.dp, vertical = 32.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(24.dp), verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = onBack) { Text("BACK") }
            Column {
                Text(state.entry.name.uppercase(), style = MaterialTheme.typography.displaySmall)
                Text(state.entry.kindLabel())
            }
        }
        Spacer(Modifier.height(20.dp))
        LazyVerticalGrid(
            columns = GridCells.Adaptive(minSize = 200.dp),
            horizontalArrangement = Arrangement.spacedBy(18.dp),
            verticalArrangement = Arrangement.spacedBy(18.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            items(state.items) { item ->
                FocusCard(Modifier.size(width = 200.dp, height = 300.dp), { onOpen(item) }) {
                    Box(Modifier.fillMaxSize()) {
                        item.coverUrl?.let { AuthenticatedArtwork(client, it) }
                        Column(
                            Modifier.fillMaxWidth().align(Alignment.BottomStart)
                                .background(Color(0xCC09080D)).padding(10.dp),
                        ) {
                            Text(item.title, maxLines = 1)
                            item.creator?.let { Text(it, maxLines = 1, style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                }
            }
            if (state.nextCursor != null) {
                item { Button(onClick = onMore) { Text("MORE") } }
            }
        }
    }
}

@Composable
private fun PhotoScreen(client: TuvimaClient, items: List<ViewItem>, startIndex: Int, back: () -> Unit) {
    var index by remember { mutableIntStateOf(startIndex.coerceIn(0, items.lastIndex)) }
    val item = items[index]
    val bitmap by produceState<Bitmap?>(initialValue = null, key1 = item.id) {
        value = runCatching {
            val bytes = client.viewPreview(item.id, 1280)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        }.getOrNull()
    }
    val image = bitmap
    Column(Modifier.fillMaxSize().padding(horizontal = 64.dp, vertical = 32.dp)) {
        Box(Modifier.weight(1f).fillMaxWidth(), contentAlignment = Alignment.Center) {
            if (image != null) {
                Image(image.asImageBitmap(), item.displayTitle(), Modifier.fillMaxSize(), contentScale = ContentScale.Fit)
            } else {
                CircularProgressIndicator()
            }
        }
        Spacer(Modifier.height(16.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
            Button(onClick = { if (index > 0) index -= 1 }, enabled = index > 0) { Text("PREVIOUS") }
            Button(onClick = { if (index < items.lastIndex) index += 1 }, enabled = index < items.lastIndex) { Text("NEXT") }
            Button(onClick = back) { Text("BACK") }
            Text(
                listOfNotNull(item.displayTitle(), item.capturedAt?.take(10), item.locationName).joinToString(" · "),
                maxLines = 1,
            )
        }
    }
}

@Composable
private fun SearchScreen(state: TvState.Search, search: (String) -> Unit, open: (TvSearchResult) -> Unit) {
    var query by remember(state.query) { mutableStateOf(state.query) }
    LazyColumn(Modifier.fillMaxSize().padding(64.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item {
            Text("SEARCH", style = MaterialTheme.typography.displaySmall)
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(query, { query = it }, singleLine = true, modifier = Modifier.fillMaxWidth(0.6f))
                Button(onClick = { search(query) }) { Text("SEARCH") }
            }
        }
        if (state.results.isEmpty()) item { Text("No results for “${state.query}”.") }
        items(state.results) { result ->
            FocusCard(Modifier.fillMaxWidth(), { open(result) }) {
                Column(Modifier.padding(18.dp)) {
                    Text(result.title, style = MaterialTheme.typography.titleLarge)
                    result.subtitle?.let { Text(it) }
                }
            }
        }
    }
}

@Composable
private fun DetailScreen(state: TvState.Detail, play: (String) -> Unit, back: () -> Unit) {
    Column(Modifier.fillMaxSize().padding(72.dp), verticalArrangement = Arrangement.Center) {
        Text(state.detail.title, style = MaterialTheme.typography.displayMedium)
        state.detail.subtitle?.let { Text(it, style = MaterialTheme.typography.headlineSmall) }
        if (state.detail.facts.isNotBlank()) Text(state.detail.facts, modifier = Modifier.padding(top = 12.dp))
        state.detail.description?.let { Text(it, modifier = Modifier.fillMaxWidth(0.7f).padding(top = 20.dp)) }
        Row(Modifier.padding(top = 28.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            state.playableAssetId?.let { asset -> Button(onClick = { play(asset) }) { Text("PLAY") } }
            Button(onClick = back) { Text("BACK") }
        }
    }
}

private fun parseSearch(json: JSONObject): List<TvSearchResult> = buildList {
    val seen = mutableSetOf<String>()
    val sections = json.optJSONArray("sections") ?: return@buildList
    for (sectionIndex in 0 until sections.length()) {
        val results = sections.optJSONObject(sectionIndex)?.optJSONArray("results") ?: continue
        for (resultIndex in 0 until results.length()) {
            val value = results.optJSONObject(resultIndex) ?: continue
            val route = value.optString("detailRoute").split('/').filter(String::isNotBlank)
            val id = route.lastOrNull() ?: value.optString("id")
            val entityType = route.dropLast(1).lastOrNull() ?: value.optString("entityType", "work")
            if (id.isBlank() || !seen.add("$entityType:$id")) continue
            add(TvSearchResult(id, entityType, value.optString("title"), value.optString("subtitle").takeIf(String::isNotBlank)))
        }
    }
}

private fun parseDetail(json: JSONObject): TvDetail {
    val facts = json.optJSONObject("facts")
    val factText = listOf("year", "rating", "contentRating", "runtime", "duration")
        .mapNotNull { key -> facts?.optString(key)?.takeIf(String::isNotBlank) }
        .distinct()
        .joinToString(" · ")
    return TvDetail(
        title = json.optString("title", "Tuvima Library"),
        subtitle = json.optString("subtitle").takeIf(String::isNotBlank),
        description = json.optString("description").takeIf(String::isNotBlank),
        facts = factText,
    )
}

private fun detailEntityType(mediaType: String): String {
    val value = mediaType.lowercase()
    return when {
        "movie" in value -> "movie"
        "episode" in value -> "tvEpisode"
        "tv" in value -> "tvShow"
        "audiobook" in value -> "audiobook"
        "book" in value -> "book"
        "comic" in value -> "comicIssue"
        "music" in value -> "musicAlbum"
        else -> "work"
    }
}

private fun cardDetailTarget(card: DisplayCard): Pair<String, String> {
    card.actions.firstNotNullOfOrNull { action ->
        action.webUrl?.takeIf { "/details/" in it }?.split('/')?.filter(String::isNotBlank)?.takeIf { it.size >= 2 }
            ?.let { it[it.size - 2] to it.last() }
    }?.let { return it }
    card.collectionId?.let { return "collection" to it }
    return detailEntityType(card.mediaType) to (card.workId ?: card.id)
}

@Composable
private fun AuthenticatedArtwork(client: TuvimaClient, url: String) {
    val bitmap by produceState<android.graphics.Bitmap?>(null, url) {
        value = runCatching {
            val bytes = client.authenticatedBytes(url)
            BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
        }.getOrNull()
    }
    bitmap?.let { Image(it.asImageBitmap(), null, Modifier.fillMaxSize(), contentScale = ContentScale.Crop) }
}

@Composable
private fun LoadingScreen(message: String) = Column(
    Modifier.fillMaxSize(),
    horizontalAlignment = Alignment.CenterHorizontally,
    verticalArrangement = Arrangement.Center,
) {
    CircularProgressIndicator()
    Spacer(Modifier.height(20.dp))
    Text(message)
}

@Composable
private fun ErrorScreen(message: String, onBack: (() -> Unit)?, reset: () -> Unit) = Column(
    Modifier.fillMaxSize().padding(96.dp),
    verticalArrangement = Arrangement.Center,
) {
    Text("Tuvima needs attention", style = MaterialTheme.typography.headlineLarge)
    Spacer(Modifier.height(12.dp))
    Text(message)
    Spacer(Modifier.height(24.dp))
    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        if (onBack != null) Button(onClick = onBack) { Text("BACK") }
        Button(onClick = reset) { Text("Connect again") }
    }
}

private const val DEFAULT_SCOPES =
    "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write"
