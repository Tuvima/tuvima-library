package com.tuvima.library.mobile

import androidx.compose.foundation.background
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
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.Stable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateMapOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.DownloadKind
import com.tuvima.library.core.OfflineBatchLogic
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.Playlist
import com.tuvima.library.core.PlaylistSong
import com.tuvima.library.core.PlaylistsLogic
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TuvimaHttpException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

// Playlists on the phone (spec native-apps WP19): the Listen section's Playlists tab and one playlist's page. Read only: playlists are made,
// renamed and changed on the Dashboard, and the Engine alone decides which songs a profile may see (a Kids limit applies).

/** The profile's playlists read a page at a time, with each tile's cover taken lazily from the first songs' covers (the list has no artwork). */
@Stable
internal class PlaylistsController(private val client: TuvimaClient, private val scope: CoroutineScope) {
    var items by mutableStateOf<List<Playlist>>(emptyList())
        private set
    var loading by mutableStateOf(false)
        private set
    var loaded by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    /** The covers each tile draws, by playlist id; a playlist with no entry has not been read yet, an empty list means it has no cover to draw. */
    val covers = mutableStateMapOf<String, List<String>>()

    private var cursor: String? = null
    private var hasMore = true
    private val coverRequested = HashSet<String>()

    fun loadMore() {
        if (loading || !hasMore) return
        loading = true
        error = null
        scope.launch {
            try {
                val page = client.playlistsPage(cursor)
                val before = items.size
                items = PlaylistsLogic.mergePlaylists(items, page.items)
                cursor = PlaylistsLogic.cursorFor(page)
                // A page that added nothing, or gave no cursor, is the end, so scrolling can never loop.
                hasMore = cursor != null && items.size > before
                loaded = true
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                error = playlistMessage(failure)
            } finally {
                loading = false
            }
        }
    }

    fun retry() {
        error = null
        loadMore()
    }

    /** Reads the first songs of a playlist once, for its tile's cover. */
    fun ensureCover(playlist: Playlist) {
        if (playlist.itemCount <= 0 || !coverRequested.add(playlist.id)) return
        scope.launch {
            try {
                val page = client.playlistSongsPage(playlist.id, null, COVER_READ)
                covers[playlist.id] = PlaylistsLogic.coverLayout(playlist.coverMode, page.items.map { it.coverUrl })
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // A cover that cannot be read leaves the tile's plain placeholder; the playlist itself still opens and shows its own error.
                covers[playlist.id] = emptyList()
            }
        }
    }

    private companion object {
        const val COVER_READ = 12
    }
}

/** Listen's Playlists tab: the profile's own playlists as tiles, pinned first. A press opens the playlist. */
@Composable
internal fun PlaylistsPane(client: TuvimaClient, openPlaylist: (Playlist) -> Unit) {
    val scope = rememberCoroutineScope()
    val controller = remember(client) { PlaylistsController(client, scope) }
    val online by rememberIsOnline()
    LaunchedEffect(controller) { controller.loadMore() }
    val failure = controller.error
    when {
        failure != null && controller.items.isEmpty() ->
            ErrorBlock(if (!online) PlaylistsLogic.OFFLINE_LIST else failure) { controller.retry() }
        controller.items.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            CircularProgressIndicator()
        }
        controller.items.isEmpty() -> ErrorBlock(PlaylistsLogic.EMPTY_LIST, null)
        else -> LazyVerticalGrid(
            columns = GridCells.Adaptive(minSize = 150.dp),
            contentPadding = PaddingValues(12.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
            verticalArrangement = Arrangement.spacedBy(14.dp),
            modifier = Modifier.fillMaxSize(),
        ) {
            itemsIndexed(controller.items, key = { _, playlist -> playlist.id }) { index, playlist ->
                if (index >= controller.items.size - 4) {
                    LaunchedEffect(controller.items.size) { controller.loadMore() }
                }
                LaunchedEffect(playlist.id) { controller.ensureCover(playlist) }
                PlaylistTile(client, playlist, controller.covers[playlist.id].orEmpty()) { openPlaylist(playlist) }
            }
            if (controller.loading || failure != null) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) {
                        if (failure != null) {
                            TextButton(onClick = { controller.retry() }, modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget)) { Text("Try again") }
                        } else {
                            CircularProgressIndicator()
                        }
                    }
                }
            }
        }
    }
}

/** One playlist: a square cover (a mosaic of four song covers, or one picture), the name, and the count and length. Pinned and Smart say so. */
@Composable
private fun PlaylistTile(client: TuvimaClient, playlist: Playlist, covers: List<String>, onClick: () -> Unit) {
    Column(Modifier.fillMaxWidth().clickable(onClickLabel = "Open ${playlist.name}", onClick = onClick)) {
        Box(Modifier.fillMaxWidth().aspectRatio(1f).clip(RoundedCornerShape(8.dp))) {
            PlaylistCover(client, covers, Modifier.fillMaxSize())
            if (playlist.isPinned) {
                Text(
                    PlaylistsLogic.PINNED,
                    color = TuvimaTokens.textPrimary,
                    fontSize = 10.sp,
                    fontWeight = FontWeight.SemiBold,
                    modifier = Modifier
                        .align(Alignment.TopStart)
                        .padding(4.dp)
                        .background(TuvimaTokens.bgSurfaceElevated, RoundedCornerShape(TuvimaTokens.radiusXs))
                        .padding(horizontal = 6.dp, vertical = 2.dp),
                )
            }
        }
        Text(
            playlist.name,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.bodyMedium,
            modifier = Modifier.padding(top = 4.dp),
        )
        Text(
            PlaylistsLogic.tileLine(playlist),
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            style = MaterialTheme.typography.bodySmall,
            color = TuvimaTokens.textMuted,
        )
    }
}

/** Four covers fill a 2 by 2 mosaic, one cover fills the square, none leaves the placeholder. */
@Composable
private fun PlaylistCover(client: TuvimaClient, covers: List<String>, modifier: Modifier) {
    if (covers.size >= 4) {
        Column(modifier) {
            covers.take(4).chunked(2).forEach { line ->
                Row(Modifier.weight(1f).fillMaxWidth()) {
                    line.forEach { RemoteImage(client, it, Modifier.weight(1f).fillMaxSize()) }
                }
            }
        }
    } else {
        RemoteImage(client, covers.firstOrNull(), modifier)
    }
}

/** One playlist's songs read page after page (up to 200 each) until the end; a page that fails keeps what is shown and can be asked again. */
@Stable
internal class PlaylistSongsController(
    private val client: TuvimaClient,
    private val scope: CoroutineScope,
    private val playlistId: String,
) {
    val songs = mutableStateListOf<PlaylistSong>()
    var total by mutableStateOf(0)
        private set
    var loading by mutableStateOf(false)
        private set
    var loaded by mutableStateOf(false)
        private set
    var error by mutableStateOf<String?>(null)
        private set

    private var cursor: String? = null
    private var hasMore by mutableStateOf(true)
    private var pagesRead = 0

    /** True when the page limit stopped the reading while the Engine still had more songs: what is shown is the first part only. */
    var partial by mutableStateOf(false)
        private set

    /** True once every page is read, so totals that need every song (the length, Download all) are complete. A partial list is never complete. */
    val complete: Boolean get() = loaded && !hasMore && !partial

    fun load() {
        if (loading || !hasMore) return
        loading = true
        error = null
        scope.launch {
            try {
                while (hasMore) {
                    val page = client.playlistSongsPage(playlistId, cursor)
                    val before = songs.size
                    val merged = PlaylistsLogic.mergeSongs(songs.toList(), page.items)
                    songs.clear()
                    songs.addAll(merged)
                    total = page.totalCount
                    pagesRead += 1
                    cursor = PlaylistsLogic.cursorFor(page)
                    // A page that added nothing, or gave no cursor, is the end, so paging can never loop.
                    val continues = PlaylistsLogic.hasNextSongPage(page, pagesRead) && cursor != null && songs.size > before
                    hasMore = continues
                    partial = PlaylistsLogic.isPartial(page, continues)
                    loaded = true
                }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                error = playlistMessage(failure)
            } finally {
                loading = false
            }
        }
    }

    fun retry() {
        error = null
        load()
    }
}

/** Plain words for a failed read: a playlist that is not the profile's own (or is gone) answers 404 and says so in the playlist's words. */
private fun playlistMessage(failure: Exception): String =
    if (failure is TuvimaHttpException && failure.statusCode == 404) PlaylistsLogic.NOT_AVAILABLE else userMessage(failure)

/**
 * One playlist: its name and count, Play and Shuffle, Download all, and its songs in the saved order. A press on a song plays the playlist
 * from there through the music queue. Offline, only the songs on the phone play.
 */
@Composable
internal fun PlaylistScreen(
    application: TuvimaMobileApplication,
    client: TuvimaClient,
    playlistId: String,
    name: String,
    smart: Boolean,
    onBack: () -> Unit,
) {
    val scope = rememberCoroutineScope()
    val controller = remember(client, playlistId) { PlaylistSongsController(client, scope, playlistId) }
    val online by rememberIsOnline()
    val held = LocalDownloadedAssets.current
    // Online every song with a file plays; offline only the downloaded ones do (null means no restriction).
    val playable: Set<String>? = if (online) null else held
    var note by remember { mutableStateOf<String?>(null) }
    LaunchedEffect(controller) { controller.load() }

    val songs = controller.songs
    val failure = controller.error
    val heldCount = songs.count { it.assetId != null && it.assetId in held }
    val downloadable = remember(songs.size, controller.complete) { PlaylistsLogic.downloadChildren(songs.toList()) }

    fun playFrom(pressed: PlaylistSong?) {
        val request = PlaylistsLogic.playFrom(songs.toList(), pressed, name, playable)
        if (request == null) {
            note = if (online) PlaylistsLogic.NO_FILE else PlaylistsLogic.OFFLINE_NOTHING_HELD
        } else {
            note = null
            application.audio.playQueue(request)
        }
    }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onClick = onBack, modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget)) { Text("Back") }
            Text(name, style = MaterialTheme.typography.titleLarge, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.weight(1f))
        }
        val line = PlaylistsLogic.headerLine(controller.total, songs.toList(), controller.complete, smart)
        if (line.isNotEmpty()) {
            Text(line, style = MaterialTheme.typography.bodyMedium, color = TuvimaTokens.textMuted, modifier = Modifier.padding(horizontal = 12.dp))
        }
        if (songs.isNotEmpty()) {
            Row(
                Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Button(onClick = { playFrom(null) }, modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget)) { Text(PlaylistsLogic.PLAY_LABEL) }
                OutlinedButton(
                    onClick = {
                        val request = PlaylistsLogic.shuffled(songs.toList(), name, playable, kotlin.random.Random.Default)
                        if (request == null) {
                            note = if (online) PlaylistsLogic.NO_FILE else PlaylistsLogic.OFFLINE_NOTHING_HELD
                        } else {
                            note = null
                            application.audio.playQueue(request)
                        }
                    },
                    modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget),
                ) { Text(PlaylistsLogic.SHUFFLE_LABEL) }
                if (downloadable.isNotEmpty() && controller.complete) {
                    TextButton(
                        onClick = {
                            note = "Starting the downloads…"
                            scope.launch {
                                // Queuing writes one record per file to disk, so it is done off the main thread.
                                val quality = OfflineLogic.defaultQualityFor(DownloadKind.Audio, application.downloadQuality())
                                val outcome = withContext(Dispatchers.IO) { application.offline.enqueueBatch(name, null, downloadable, quality) }
                                note = if (outcome.notPaired) {
                                    enqueueMessage(EnqueueResult.NotPaired)
                                } else {
                                    OfflineBatchLogic.message(outcome.plan, OfflineBatchLogic.noun(downloadable, outcome.plan.toQueue.size))
                                }
                            }
                        },
                        modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget),
                    ) { Text(PlaylistsLogic.DOWNLOAD_ALL) }
                }
            }
        }
        if (controller.partial) {
            Text(
                PlaylistsLogic.partialNote(songs.size, controller.total),
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp),
            )
        }
        PlaylistsLogic.offlineNote(online, heldCount)?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp))
        }
        note?.let {
            Text(it, style = MaterialTheme.typography.bodySmall, modifier = Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp))
        }
        when {
            failure != null && songs.isEmpty() -> ErrorBlock(if (!online) PlaylistsLogic.OFFLINE_LIST else failure) { controller.retry() }
            songs.isEmpty() && !controller.loaded -> Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) { CircularProgressIndicator() }
            songs.isEmpty() -> ErrorBlock(PlaylistsLogic.EMPTY_PLAYLIST, null)
            else -> LazyColumn(Modifier.fillMaxSize()) {
                itemsIndexed(songs, key = { _, song -> song.id }) { _, song ->
                    val available = PlaylistsLogic.isPlayable(song) && (playable == null || song.assetId in playable)
                    PlaylistSongRow(client, song, available, held = song.assetId != null && song.assetId in held) { playFrom(song) }
                }
                if (controller.loading) {
                    item { Box(Modifier.fillMaxWidth().padding(16.dp), contentAlignment = Alignment.Center) { CircularProgressIndicator() } }
                } else if (failure != null) {
                    // A later page failed: the songs already loaded stay, with a way to ask again.
                    item {
                        Column(Modifier.fillMaxWidth().padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                            Text(failure, color = MaterialTheme.colorScheme.error)
                            TextButton(onClick = { controller.retry() }, modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget)) { Text("Try again") }
                        }
                    }
                }
            }
        }
    }
}

/** One song of a playlist: cover, title, artist and album, length. A song the phone cannot play right now is dimmed and does nothing. */
@Composable
private fun PlaylistSongRow(client: TuvimaClient, song: PlaylistSong, available: Boolean, held: Boolean, onPlay: () -> Unit) {
    val line = PlaylistsLogic.songLine(song)
    val length = PlaylistsLogic.songLength(song.durationSeconds)
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 60.dp)
            .alpha(if (available) 1f else 0.5f)
            .then(if (available) Modifier.clickable(onClickLabel = "Play ${song.title}", onClick = onPlay) else Modifier)
            .padding(horizontal = 12.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RemoteImage(client, song.coverUrl, Modifier.size(48.dp).clip(RoundedCornerShape(4.dp)))
        Column(Modifier.weight(1f)) {
            Text(song.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
            if (line.isNotEmpty()) {
                Text(line, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
            }
            // An album row of a smart playlist has no file of its own: it is listed, dimmed, and says why it does not play.
            if (!PlaylistsLogic.isPlayable(song)) {
                Text(PlaylistsLogic.NO_FILE_ROW, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
            }
        }
        if (held) DownloadedBadge()
        if (length.isNotEmpty()) Text(length, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
    }
}
