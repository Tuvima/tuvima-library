package com.tuvima.library.mobile

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.work.OneTimeWorkRequestBuilder
import androidx.work.WorkManager
import androidx.work.workDataOf
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
        val initialDetail = intent.data?.takeIf { it.scheme == "tuvima" && it.host == "details" }
            ?.pathSegments?.takeIf { it.size >= 2 }?.let { it[0] to it[1] }
        if (intent.data?.scheme == "tuvima" && intent.data?.host == "play") {
            intent.data?.pathSegments?.firstOrNull()?.let(::play)
        }
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                MobileRoot(application as TuvimaMobileApplication, initialDetail, ::play, ::download)
            }
        }
    }

    private fun play(assetId: String) {
        ContextCompat.startForegroundService(
            this,
            Intent(this, TuvimaMediaLibraryService::class.java)
                .setAction(TuvimaMediaLibraryService.ACTION_PLAY_ASSET)
                .putExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId),
        )
    }

    private fun download(assetId: String) {
        WorkManager.getInstance(this).enqueue(
            OneTimeWorkRequestBuilder<OfflineDownloadWorker>()
                .setInputData(workDataOf(OfflineDownloadWorker.KEY_ASSET_ID to assetId))
                .build(),
        )
    }
}

private sealed interface MobileState {
    data object Server : MobileState
    data class Pairing(val session: PairingSession) : MobileState
    data class Library(val page: DisplayPage) : MobileState
    data class Search(val query: String, val results: List<MobileSearchResult>) : MobileState
    data class Detail(val detail: MobileDetail, val playableAssetId: String?) : MobileState
    data class Message(val text: String) : MobileState
}

private data class MobileSearchResult(val id: String, val entityType: String, val title: String, val subtitle: String?)
private data class MobileDetail(val title: String, val subtitle: String?, val description: String?, val facts: String)

@Composable
private fun MobileRoot(
    application: TuvimaMobileApplication,
    initialDetail: Pair<String, String>?,
    play: (String) -> Unit,
    download: (String) -> Unit,
) {
    val scope = rememberCoroutineScope()
    var state: MobileState by remember { mutableStateOf(MobileState.Server) }
    var returnState: MobileState by remember { mutableStateOf(MobileState.Server) }
    var client: TuvimaClient? by remember { mutableStateOf(application.client) }

    suspend fun libraryOrDeepLink(value: TuvimaClient): MobileState = initialDetail?.let { (entityType, id) ->
        MobileState.Detail(parseMobileDetail(value.details(entityType, id)), null)
    } ?: MobileState.Library(value.home())

    fun openDetail(entityType: String, id: String, assetId: String?) {
        val api = client ?: return
        returnState = state
        state = MobileState.Message("Loading details…")
        scope.launch {
            state = runCatching { MobileState.Detail(parseMobileDetail(api.details(entityType, id)), assetId) }
                .getOrElse { MobileState.Message(it.message ?: "Details could not be loaded.") }
        }
    }

    fun search(query: String) {
        val api = client ?: return
        if (query.isBlank()) return
        returnState = state
        state = MobileState.Message("Searching…")
        scope.launch {
            state = runCatching { MobileState.Search(query, parseMobileSearch(api.search(query))) }
                .getOrElse { MobileState.Message(it.message ?: "Search could not be loaded.") }
        }
    }

    fun connect(address: String) {
        state = MobileState.Message("Connecting…")
        scope.launch {
            runCatching {
                application.connect(address).also {
                    client = it
                    it.discover()
                    state = if (it.isPaired()) libraryOrDeepLink(it)
                    else MobileState.Pairing(it.beginPairing(MOBILE_SCOPES))
                }
            }.onFailure { state = MobileState.Message(it.message ?: "Connection failed.") }
        }
    }

    LaunchedEffect(Unit) {
        application.savedServer()?.let(::connect)
    }

    when (val current = state) {
        MobileState.Server -> {
            var address by remember { mutableStateOf("") }
            Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
                Text("Tuvima Library", style = MaterialTheme.typography.displaySmall)
                Spacer(Modifier.height(20.dp))
                OutlinedTextField(address, { address = it }, label = { Text("Dashboard address") }, modifier = Modifier.fillMaxWidth())
                Spacer(Modifier.height(16.dp))
                Button(onClick = { connect(address) }) { Text("Connect") }
            }
        }
        is MobileState.Pairing -> MobilePairing(client!!, current.session) { result ->
            when (result) {
                is PairingPollResult.Authorized -> scope.launch { state = libraryOrDeepLink(client!!) }
                is PairingPollResult.Failed -> state = MobileState.Message(result.description ?: result.code)
                is PairingPollResult.Pending -> Unit
            }
        }
        is MobileState.Library -> LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp)) {
            item {
                Text(current.page.title, style = MaterialTheme.typography.displaySmall, modifier = Modifier.padding(vertical = 24.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    listOf("watch", "read", "listen").forEach { lane ->
                        Button(onClick = { scope.launch { state = MobileState.Library(client!!.browse(lane)) } }) { Text(lane) }
                    }
                }
                var query by remember { mutableStateOf("") }
                Row(Modifier.padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedTextField(query, { query = it }, label = { Text("Search") }, modifier = Modifier.weight(1f))
                    Button(onClick = { search(query) }) { Text("Search") }
                }
            }
            current.page.shelves.forEach { shelf ->
                item { Text(shelf.title, style = MaterialTheme.typography.headlineSmall, modifier = Modifier.padding(top = 24.dp, bottom = 8.dp)) }
                items(shelf.items, key = { it.id }) { card ->
                    val asset = card.actions.firstNotNullOfOrNull { it.assetId } ?: card.assetId
                    Card(Modifier.fillMaxWidth().padding(vertical = 6.dp).clickable {
                        val target = mobileCardDetailTarget(card)
                        openDetail(target.first, target.second, asset)
                    }) {
                        Column(Modifier.padding(16.dp)) {
                            Text(card.title)
                            card.subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                }
            }
        }
        is MobileState.Search -> {
            var query by remember(current.query) { mutableStateOf(current.query) }
            LazyColumn(Modifier.fillMaxSize().padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                item {
                    Text("Search", style = MaterialTheme.typography.displaySmall)
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(query, { query = it }, modifier = Modifier.weight(1f))
                        Button(onClick = { search(query) }) { Text("Search") }
                    }
                }
                items(current.results, key = { "${it.entityType}:${it.id}" }) { result ->
                    Card(Modifier.fillMaxWidth().clickable { openDetail(result.entityType, result.id, null) }) {
                        Column(Modifier.padding(16.dp)) {
                            Text(result.title)
                            result.subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                        }
                    }
                }
            }
        }
        is MobileState.Detail -> Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
            Text(current.detail.title, style = MaterialTheme.typography.displaySmall)
            current.detail.subtitle?.let { Text(it, style = MaterialTheme.typography.titleLarge) }
            if (current.detail.facts.isNotBlank()) Text(current.detail.facts, modifier = Modifier.padding(top = 8.dp))
            current.detail.description?.let { Text(it, modifier = Modifier.padding(top = 16.dp)) }
            Row(Modifier.padding(top = 20.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                current.playableAssetId?.let { asset ->
                    Button(onClick = { play(asset) }) { Text("Play") }
                    Button(onClick = { download(asset) }) { Text("Download") }
                }
                Button(onClick = { state = returnState }) { Text("Back") }
            }
        }
        is MobileState.Message -> Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
            Text(current.text)
            Spacer(Modifier.height(16.dp))
            Button(onClick = { state = MobileState.Server }) { Text("Try another server") }
        }
    }
}

@Composable
private fun MobilePairing(client: TuvimaClient, session: PairingSession, result: (PairingPollResult) -> Unit) {
    LaunchedEffect(session.deviceCode) {
        var interval = session.pollingIntervalSeconds
        repeat(session.expiresInSeconds / interval) {
            delay(interval * 1000L)
            when (val next = client.pollPairing(session)) {
                is PairingPollResult.Pending -> interval = next.retryAfterSeconds
                else -> { result(next); return@LaunchedEffect }
            }
        }
        result(PairingPollResult.Failed("expired_token", "The pairing code expired."))
    }
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
        Text("Pair this device", style = MaterialTheme.typography.displaySmall)
        Text(session.verificationUri)
        Text(session.userCode, style = MaterialTheme.typography.displayMedium)
        Text("Waiting for approval…")
    }
}

private const val MOBILE_SCOPES =
    "library.read artwork.read progress.read progress.write queue.read queue.write playback.read playback.write downloads.read downloads.write"

private fun parseMobileSearch(json: JSONObject): List<MobileSearchResult> = buildList {
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
            add(MobileSearchResult(id, entityType, value.optString("title"), value.optString("subtitle").takeIf(String::isNotBlank)))
        }
    }
}

private fun parseMobileDetail(json: JSONObject): MobileDetail {
    val facts = json.optJSONObject("facts")
    return MobileDetail(
        title = json.optString("title", "Tuvima Library"),
        subtitle = json.optString("subtitle").takeIf(String::isNotBlank),
        description = json.optString("description").takeIf(String::isNotBlank),
        facts = listOf("year", "rating", "contentRating", "runtime", "duration")
            .mapNotNull { facts?.optString(it)?.takeIf(String::isNotBlank) }
            .distinct().joinToString(" · "),
    )
}

private fun mobileDetailEntityType(mediaType: String): String {
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

private fun mobileCardDetailTarget(card: com.tuvima.library.core.DisplayCard): Pair<String, String> {
    card.actions.firstNotNullOfOrNull { action ->
        action.webUrl?.takeIf { "/details/" in it }?.split('/')?.filter(String::isNotBlank)?.takeIf { it.size >= 2 }
            ?.let { it[it.size - 2] to it.last() }
    }?.let { return it }
    card.collectionId?.let { return "collection" to it }
    return mobileDetailEntityType(card.mediaType) to (card.workId ?: card.id)
}
