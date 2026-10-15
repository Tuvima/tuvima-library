package com.tuvima.library.mobile

import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import androidx.annotation.OptIn
import androidx.concurrent.futures.CallbackToFutureAdapter
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.OfflineProgressEntry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import org.json.JSONObject
import java.io.File

@OptIn(UnstableApi::class)
class TuvimaMediaLibraryService : MediaLibraryService() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var player: ExoPlayer
    private lateinit var session: MediaLibrarySession
    private val client get() = (application as TuvimaMobileApplication).client
    private val children = mutableMapOf<String, List<MediaItem>>()
    private var activeAssetId: String? = null
    private var progressJob: Job? = null
    private val offline get() = (application as TuvimaMobileApplication).offline
    private val networkCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            val assetId = activeAssetId ?: return
            // The player may only be used on the main thread, and this callback runs on a connectivity thread.
            scope.launch {
                if (!::player.isInitialized) return@launch
                // A downloaded file keeps playing as it is; only a streamed item is re-resolved when a network returns.
                if (isLocalItem(player.currentMediaItem)) return@launch
                val position = player.currentPosition
                resolvePlayable(assetId)?.let {
                    player.setMediaItem(it, position)
                    player.prepare()
                    player.play()
                }
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        val token = com.tuvima.library.core.SecureTokenStore(this).load()?.second?.accessToken
        val httpSource = DefaultHttpDataSource.Factory().apply {
            token?.let { setDefaultRequestProperties(mapOf("Authorization" to "Bearer $it")) }
        }
        // The default source also reads file:// URIs, which is how a downloaded copy plays without a connection.
        val dataSource = DefaultDataSource.Factory(this, httpSource)
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(dataSource))
            .build()
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (isPlaying) {
                    startProgressLoop()
                } else {
                    progressJob?.cancel()
                    saveLocalProgress(ended = false, sync = true)
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                if (playbackState == Player.STATE_ENDED) saveLocalProgress(ended = true, sync = true)
            }
        })
        session = MediaLibrarySession.Builder(this, player, LibraryCallback()).build()
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(networkCallback)
        refreshLibrary()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession = session

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val result = super.onStartCommand(intent, flags, startId)
        if (intent?.action == ACTION_PLAY_ASSET) {
            intent.getStringExtra(EXTRA_ASSET_ID)?.let { assetId ->
                activeAssetId = assetId
                scope.launch {
                    resolvePlayable(assetId)?.let {
                        // A downloaded copy resumes where this phone (or the Engine, once synced) left off.
                        val start = if (isLocalItem(it)) OfflineLogic.resumeStartMs(offline.store().progress(assetId)) else 0L
                        player.setMediaItem(it, start)
                        player.prepare()
                        player.play()
                    }
                }
            }
        }
        return result
    }

    private fun refreshLibrary() {
        val api = client ?: return
        scope.launch {
            val categories = listOf(
                "music" to "Music",
                "audiobooks" to "Audiobooks",
                "playlists" to "Playlists",
                "queue" to "Queue",
                "recent" to "Recent items",
            )
            children[ROOT] = categories.map { (id, title) -> folder(id, title) }
            categories.forEach { (id, _) ->
                val page = runCatching {
                    when (id) {
                        "audiobooks" -> api.browse("listen", "Audiobooks")
                        "playlists" -> api.browse("listen", "Playlists")
                        "recent" -> api.continuePage("listen")
                        "queue" -> null
                        else -> api.browse("listen", "Music")
                    }
                }.getOrNull()
                children[id] = if (id == "queue") {
                    runCatching { queueItems(api.playerState()) }.getOrDefault(emptyList())
                } else {
                    page?.shelves?.flatMap { it.items }?.distinctBy { it.id }?.mapNotNull(::playable).orEmpty()
                }
                session.notifyChildrenChanged(id, children[id]?.size ?: 0, null)
            }
            session.notifyChildrenChanged(ROOT, categories.size, null)
        }
    }

    private inner class LibraryCallback : MediaLibrarySession.Callback {
        override fun onGetLibraryRoot(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<MediaItem>> =
            Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT, "Tuvima Library"), params))

        override fun onGetChildren(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            parentId: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?,
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> {
            val source = children[parentId].orEmpty()
            val start = (page * pageSize).coerceAtMost(source.size)
            val end = (start + pageSize).coerceAtMost(source.size)
            return Futures.immediateFuture(LibraryResult.ofItemList(source.subList(start, end), params))
        }

        override fun onAddMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: List<MediaItem>,
        ): ListenableFuture<List<MediaItem>> = CallbackToFutureAdapter.getFuture { completer ->
            scope.launch {
                val resolved = mediaItems.mapNotNull { resolvePlayable(it.mediaId) }
                activeAssetId = mediaItems.firstOrNull()?.mediaId
                completer.set(resolved)
            }
            "Resolve Tuvima playback manifests"
        }
    }

    private fun isLocalItem(item: MediaItem?): Boolean = item?.localConfiguration?.uri?.scheme == "file"

    /** The downloaded copy of an asset when this phone holds a finished, unexpired one for the person using it. */
    private fun localItem(assetId: String): MediaItem? {
        val record = offline.store().record(assetId) ?: return null
        val path = record.filePath ?: return null
        val file = File(path)
        if (!record.isComplete || !file.isFile || OfflineLogic.isExpired(record, System.currentTimeMillis())) return null
        return MediaItem.Builder()
            .setMediaId(assetId)
            .setUri(Uri.fromFile(file))
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(record.title)
                    .setSubtitle(record.subtitle)
                    .build(),
            )
            .build()
    }

    private fun startProgressLoop() {
        progressJob?.cancel()
        progressJob = scope.launch {
            while (true) {
                delay(PROGRESS_SAVE_MS)
                saveLocalProgress(ended = false, sync = false)
            }
        }
    }

    /**
     * Keeps where a downloaded file is up to, on the phone only. The sync worker replays it to the Engine with
     * offline set and this time; nothing is written to History from here.
     */
    private fun saveLocalProgress(ended: Boolean, sync: Boolean) {
        if (!::player.isInitialized) return
        val item = player.currentMediaItem ?: return
        if (!isLocalItem(item)) return
        val store = offline.store()
        val duration = player.duration.takeIf { it > 0 && it != C.TIME_UNSET }?.let { it / 1000.0 }
        val position = if (ended && duration != null) duration else player.currentPosition / 1000.0
        // Too little to be worth recording (the player just opened or just stopped at the start).
        if (position < MIN_RECORDED_SECONDS && !ended) return
        store.saveProgress(
            OfflineProgressEntry(
                assetId = item.mediaId,
                positionSeconds = position,
                durationSeconds = duration,
                progressPct = OfflineLogic.progressPercent(position, duration),
                recordedAtMs = System.currentTimeMillis(),
                pending = true,
                revision = 0L,
            ),
        )
        if (sync) offline.scheduleSync()
    }

    private suspend fun resolvePlayable(assetId: String): MediaItem? {
        localItem(assetId)?.let { return it }
        val api = client ?: return null
        val manifest = api.playbackManifest(assetId, "local")
        val path = manifest.playablePath() ?: return null
        return MediaItem.Builder().setMediaId(assetId).setUri(path).build()
    }

    private fun folder(id: String, title: String): MediaItem = MediaItem.Builder()
        .setMediaId(id)
        .setMediaMetadata(
            MediaMetadata.Builder()
                .setTitle(title)
                .setIsBrowsable(true)
                .setIsPlayable(false)
                .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                .build(),
        )
        .build()

    private fun playable(card: DisplayCard): MediaItem? {
        val assetId = card.actions.firstNotNullOfOrNull { it.assetId } ?: card.assetId ?: return null
        return MediaItem.Builder()
            .setMediaId(assetId)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(card.title)
                    .setSubtitle(card.subtitle)
                    .setIsBrowsable(false)
                    .setIsPlayable(true)
                    .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                    .build(),
            )
            .build()
    }

    private fun queueItems(state: JSONObject): List<MediaItem> {
        val queue = state.optJSONArray("queue") ?: return emptyList()
        return buildList {
            for (index in 0 until queue.length()) {
                val item = queue.optJSONObject(index) ?: continue
                val mediaType = item.optString("mediaType")
                if (!mediaType.equals("Music", true) && !mediaType.equals("Audiobooks", true)) continue
                val assetId = item.optString("assetId").takeIf(String::isNotBlank) ?: continue
                add(
                    MediaItem.Builder()
                        .setMediaId(assetId)
                        .setMediaMetadata(
                            MediaMetadata.Builder()
                                .setTitle(item.optString("title", "Tuvima Library"))
                                .setSubtitle(item.optString("subtitle").takeIf(String::isNotBlank))
                                .setIsBrowsable(false)
                                .setIsPlayable(true)
                                .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                                .build(),
                        )
                        .build(),
                )
            }
        }
    }

    override fun onDestroy() {
        progressJob?.cancel()
        saveLocalProgress(ended = false, sync = true)
        getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(networkCallback)
        session.release()
        player.release()
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_PLAY_ASSET = "com.tuvima.library.PLAY_ASSET"
        const val EXTRA_ASSET_ID = "asset_id"
        private const val ROOT = "tuvima-root"
        private const val PROGRESS_SAVE_MS = 10_000L
        private const val MIN_RECORDED_SECONDS = 5.0
    }
}
