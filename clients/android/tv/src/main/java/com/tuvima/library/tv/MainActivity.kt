package com.tuvima.library.tv

import android.content.Intent
import android.graphics.BitmapFactory
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.focusable
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
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.DisplayPage
import com.tuvima.library.core.PairingPollResult
import com.tuvima.library.core.PairingSession
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val application = application as TuvimaTvApplication
        setContent {
            MaterialTheme(colorScheme = darkColorScheme(primary = androidx.compose.ui.graphics.Color(0xFF9B7BFF))) {
                TuvimaTvRoot(application) { assetId ->
                    startActivity(Intent(this, PlaybackActivity::class.java).putExtra(PlaybackActivity.ASSET_ID, assetId))
                }
            }
        }
    }
}

private sealed interface TvState {
    data object Server : TvState
    data class Loading(val message: String) : TvState
    data class Pairing(val session: PairingSession) : TvState
    data class Library(val page: DisplayPage) : TvState
    data class Search(val query: String, val results: List<TvSearchResult>) : TvState
    data class Detail(val detail: TvDetail, val playableAssetId: String?) : TvState
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

@Composable
private fun TuvimaTvRoot(application: TuvimaTvApplication, play: (String) -> Unit) {
    val scope = rememberCoroutineScope()
    var state: TvState by remember { mutableStateOf(TvState.Server) }
    var returnState: TvState by remember { mutableStateOf(TvState.Server) }
    var activeClient: TuvimaClient? by remember { mutableStateOf(null) }

    fun openDetail(entityType: String, entityId: String, playableAssetId: String?) {
        returnState = state
        state = TvState.Loading("Loading details…")
        scope.launch {
            state = runCatching {
                TvState.Detail(parseDetail(activeClient!!.details(entityType, entityId)), playableAssetId)
            }.getOrElse { TvState.Error(it.message ?: "Details could not be loaded.") }
        }
    }

    fun search(query: String) {
        if (query.isBlank()) return
        returnState = state
        state = TvState.Loading("Searching…")
        scope.launch {
            state = runCatching { TvState.Search(query, parseSearch(activeClient!!.search(query))) }
                .getOrElse { TvState.Error(it.message ?: "Search could not be loaded.") }
        }
    }

    fun connect(address: String) {
        state = TvState.Loading("Connecting to Tuvima Library…")
        scope.launch {
            runCatching {
                application.connect(address).also { client ->
                    activeClient = client
                    client.discover()
                    if (client.isPaired()) state = TvState.Library(client.home())
                    else state = TvState.Pairing(client.beginPairing(DEFAULT_SCOPES))
                }
            }.onFailure { state = TvState.Error(it.message ?: "The server could not be reached.") }
        }
    }

    LaunchedEffect(Unit) { application.savedServer()?.let(::connect) }

    Box(Modifier.fillMaxSize().background(androidx.compose.ui.graphics.Color(0xFF09080D))) {
        when (val current = state) {
            TvState.Server -> ServerScreen(onConnect = ::connect)
            is TvState.Loading -> LoadingScreen(current.message)
            is TvState.Pairing -> PairingScreen(activeClient, current.session) { result ->
                when (result) {
                    is PairingPollResult.Authorized -> scope.launch {
                        state = TvState.Loading("Loading your library…")
                        state = runCatching { TvState.Library(activeClient!!.home()) }
                            .getOrElse { TvState.Error(it.message ?: "Your library could not be loaded.") }
                    }
                    is PairingPollResult.Failed -> state = TvState.Error(result.description ?: result.code)
                    is PairingPollResult.Pending -> Unit
                }
            }
            is TvState.Library -> LibraryScreen(
                current.page,
                activeClient!!,
                openLane = { lane ->
                    scope.launch {
                        state = TvState.Loading("Loading $lane…")
                        state = runCatching { TvState.Library(activeClient!!.browse(lane)) }
                            .getOrElse { TvState.Error(it.message ?: "That section could not be loaded.") }
                    }
                },
                onSearch = ::search,
                openDetail = ::openDetail,
            )
            is TvState.Search -> SearchScreen(current, ::search) { result ->
                openDetail(result.entityType, result.id, null)
            }
            is TvState.Detail -> DetailScreen(current, play) { state = returnState }
            is TvState.Error -> ErrorScreen(current.message) {
                activeClient?.signOut()
                state = TvState.Server
            }
        }
    }
}

@Composable
private fun ServerScreen(onConnect: (String) -> Unit) {
    var address by remember { mutableStateOf("") }
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
        Spacer(Modifier.height(20.dp))
        Button(onClick = { if (address.isNotBlank()) onConnect(address) }) { Text("Connect") }
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

@Composable
private fun LibraryScreen(
    page: DisplayPage,
    client: TuvimaClient,
    openLane: (String) -> Unit,
    onSearch: (String) -> Unit,
    openDetail: (String, String, String?) -> Unit,
) {
    var query by remember { mutableStateOf("") }
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
        page.shelves.forEach { shelf ->
            item {
                Column(Modifier.padding(bottom = 28.dp)) {
                    Text(shelf.title, style = MaterialTheme.typography.headlineMedium, modifier = Modifier.padding(horizontal = 64.dp))
                    shelf.subtitle?.let { Text(it, modifier = Modifier.padding(horizontal = 64.dp)) }
                    Spacer(Modifier.height(12.dp))
                    LazyRow(
                        contentPadding = PaddingValues(horizontal = 64.dp),
                        horizontalArrangement = Arrangement.spacedBy(18.dp),
                    ) {
                        items(shelf.items, key = { it.id }) { card ->
                            MediaCard(card, client) {
                                val asset = card.actions.firstNotNullOfOrNull { it.assetId } ?: card.assetId
                                val target = cardDetailTarget(card)
                                openDetail(target.first, target.second, asset)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun MediaCard(card: DisplayCard, client: TuvimaClient, open: () -> Unit) {
    Card(
        modifier = Modifier.size(width = 260.dp, height = 178.dp).focusable().clickable(onClick = open),
        colors = CardDefaults.cardColors(containerColor = androidx.compose.ui.graphics.Color(0xFF241D32)),
        shape = RoundedCornerShape(12.dp),
    ) {
        Box(Modifier.fillMaxSize()) {
            card.artworkUrl?.let { AuthenticatedArtwork(client, it) }
            Column(
                Modifier.fillMaxWidth().align(Alignment.BottomStart)
                    .background(androidx.compose.ui.graphics.Color(0xCC09080D)).padding(12.dp),
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
        items(state.results, key = { "${it.entityType}:${it.id}" }) { result ->
            Card(Modifier.fillMaxWidth().focusable().clickable { open(result) }) {
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
private fun ErrorScreen(message: String, reset: () -> Unit) = Column(
    Modifier.fillMaxSize().padding(96.dp),
    verticalArrangement = Arrangement.Center,
) {
    Text("Tuvima needs attention", style = MaterialTheme.typography.headlineLarge)
    Spacer(Modifier.height(12.dp))
    Text(message)
    Spacer(Modifier.height(24.dp))
    Button(onClick = reset) { Text("Connect again") }
}

private const val DEFAULT_SCOPES =
    "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write"
