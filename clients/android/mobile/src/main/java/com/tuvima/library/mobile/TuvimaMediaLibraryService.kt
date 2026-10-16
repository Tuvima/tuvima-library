package com.tuvima.library.mobile

import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.os.Bundle
import androidx.annotation.OptIn
import androidx.concurrent.futures.CallbackToFutureAdapter
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.HttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionCommands
import androidx.media3.session.SessionResult
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import com.tuvima.library.core.HeartbeatRules
import com.tuvima.library.core.PreparingPoll
import com.tuvima.library.core.PreparingStep
import com.tuvima.library.core.StreamRecovery
import com.tuvima.library.core.StreamRecoveryDecision
import com.tuvima.library.core.StreamResume
import com.tuvima.library.core.SubtitleStyleRules
import com.tuvima.library.core.VideoSessionPolicy
import com.tuvima.library.core.VideoErrors
import com.tuvima.library.core.VideoFailure
import com.tuvima.library.core.VideoMessages
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.OfflineLogic
import com.tuvima.library.core.OfflineProgressEntry
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
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
    private var heartbeatJob: Job? = null
    private var videoJob: Job? = null
    // Controllers that belong to an open video screen; with none attached a paused video can never be started from outside.
    private val videoScreens = mutableSetOf<MediaSession.ControllerInfo>()
    private var streamedResumeMs = 0L
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
                val current = player.currentMediaItem
                val wasPlaying = player.playWhenReady
                resolvePlayable(assetId)?.let { resolved ->
                    // A video keeps its type and title so the video screen and the notification stay as they were.
                    val item = if (current?.mediaMetadata?.mediaType == MediaMetadata.MEDIA_TYPE_VIDEO) {
                        resolved.buildUpon().setMediaMetadata(current.mediaMetadata).build()
                    } else {
                        resolved
                    }
                    player.setMediaItem(item, position)
                    player.prepare()
                    // A video the person paused or left stays paused; it must not start playing in the background.
                    if (wasPlaying || item.mediaMetadata.mediaType != MediaMetadata.MEDIA_TYPE_VIDEO) player.play()
                }
            }
        }
    }

    override fun onCreate() {
        super.onCreate()
        // The access token lives about ten minutes, so it is read when each request is made (never kept in the factory) and
        // refreshed by the client when it has expired. That covers streams, seeks, reconnects, the next item and sidecar subtitles.
        // The default source also reads file:// URIs, which is how a downloaded copy plays without a connection; those carry no header.
        val dataSource = ResolvingDataSource.Factory(DefaultDataSource.Factory(this, DefaultHttpDataSource.Factory())) { dataSpec ->
            if (dataSpec.uri.scheme.equals("file", ignoreCase = true)) {
                dataSpec
            } else {
                val api = client
                val token = if (api == null) null else runBlocking { api.streamBearerToken() }
                if (token == null) dataSpec else dataSpec.withAdditionalHeaders(mapOf("Authorization" to "Bearer $token"))
            }
        }
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(dataSource))
            .build()
        player.addListener(object : Player.Listener {
            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (isPlaying) {
                    startProgressLoop()
                    startHeartbeatLoop()
                } else {
                    progressJob?.cancel()
                    heartbeatJob?.cancel()
                    saveLocalProgress(ended = false, sync = true)
                    sendHeartbeat(ended = false)
                }
            }

            override fun onPlayerError(error: PlaybackException) = onStreamError(error)

            override fun onPlaybackStateChanged(playbackState: Int) {
                // Playing again: the next failure of this item (if any) gets its own recovery attempt.
                if (playbackState == Player.STATE_READY) recoveryAttempts.clear()
                if (playbackState == Player.STATE_ENDED) {
                    saveLocalProgress(ended = true, sync = true)
                    sendHeartbeat(ended = true)
                    // A finished video leaves nothing behind for the notification or a headset button to restart.
                    if (VideoSessionPolicy.clearAfterEnd(isVideoItem(player.currentMediaItem))) clearVideo()
                }
            }
        })
        session = MediaLibrarySession.Builder(this, player, LibraryCallback()).build()
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(networkCallback)
        refreshLibrary()
    }

    // Recovery attempts per item id since it last reached the ready state; cleared when playback is ready again.
    private val recoveryAttempts = mutableMapOf<String, Int>()

    /**
     * A streamed item failed. When it was a 401 or 403 (the short-lived token, or the stream grant tied to it, ran out) and
     * this item has not already been retried, ask the Engine for a fresh playback manifest and swap the item in at the same
     * place and play state. Anything else, and a second failure, is left for the screen to show with Retry.
     */
    private fun onStreamError(error: PlaybackException) {
        val item = player.currentMediaItem ?: return
        val status = httpStatusOf(error)
        val attempts = recoveryAttempts[item.mediaId] ?: 0
        if (StreamRecovery.decide(status, isLocalItem(item), attempts) != StreamRecoveryDecision.Recover) return
        recoveryAttempts[item.mediaId] = attempts + 1
        scope.launch {
            val api = client ?: return@launch
            try {
                val manifest = api.playbackManifest(item.mediaId, "local")
                val path = manifest.playablePath() ?: return@launch
                val position = player.currentPosition
                val wasPlaying = player.playWhenReady
                // The title, type and subtitle configurations stay as they were; only the stream address is new.
                val swapped = item.buildUpon().setUri(Uri.parse(path)).build()
                player.setMediaItem(swapped, position)
                player.prepare()
                player.playWhenReady = StreamRecovery.resumePlaying(wasPlaying, isVideoItem(item))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // No fresh stream could be had: the original error stays on screen with Retry, which is the same as before.
            }
        }
    }

    /** The HTTP status behind a stream error, when the failure was a refused request. */
    private fun httpStatusOf(error: Throwable?): Int? {
        var current: Throwable? = error
        while (current != null) {
            if (current is HttpDataSource.InvalidResponseCodeException) return current.responseCode
            current = current.cause
        }
        return null
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
                        val start = if (isLocalItem(it)) OfflineLogic.resumeStartMs(offline.store().progress(assetId)) else streamedResumeMs
                        streamedResumeMs = 0L
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

        override fun onConnect(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
        ): MediaSession.ConnectionResult {
            val commands: SessionCommands = MediaSession.ConnectionResult.DEFAULT_SESSION_AND_LIBRARY_COMMANDS.buildUpon()
                .add(SessionCommand(COMMAND_PLAY_VIDEO, Bundle.EMPTY))
                .add(SessionCommand(COMMAND_CANCEL_VIDEO, Bundle.EMPTY))
                .add(SessionCommand(COMMAND_CLOSE_VIDEO, Bundle.EMPTY))
                .build()
            if (controller.connectionHints.getBoolean(HINT_VIDEO_SCREEN)) videoScreens.add(controller)
            return MediaSession.ConnectionResult.AcceptedResultBuilder(session)
                .setAvailableSessionCommands(commands)
                .build()
        }

        override fun onDisconnected(session: MediaSession, controller: MediaSession.ControllerInfo) {
            videoScreens.remove(controller)
        }

        override fun onPlayerCommandRequest(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            playerCommand: Int,
        ): Int {
            val blocked = playerCommand == Player.COMMAND_PLAY_PAUSE &&
                !controller.connectionHints.getBoolean(HINT_VIDEO_SCREEN) &&
                VideoSessionPolicy.shouldBlockPlay(
                    currentIsVideo = isVideoItem(player.currentMediaItem),
                    videoScreenAttached = videoScreens.isNotEmpty(),
                    alreadyPlaying = player.playWhenReady,
                )
            return if (blocked) SessionResult.RESULT_ERROR_PERMISSION_DENIED else SessionResult.RESULT_SUCCESS
        }

        override fun onCustomCommand(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            customCommand: SessionCommand,
            args: Bundle,
        ): ListenableFuture<SessionResult> = when (customCommand.customAction) {
            COMMAND_PLAY_VIDEO -> playVideo(
                assetId = args.getString(EXTRA_ASSET_ID).orEmpty(),
                title = args.getString(EXTRA_TITLE),
                subtitle = args.getString(EXTRA_SUBTITLE),
                previousEnded = args.getBoolean(EXTRA_PREVIOUS_ENDED),
            )
            COMMAND_CANCEL_VIDEO -> {
                videoJob?.cancel()
                Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
            }
            COMMAND_CLOSE_VIDEO -> {
                videoJob?.cancel()
                closeVideo()
                Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
            }
            else -> Futures.immediateFuture(SessionResult(SessionResult.RESULT_ERROR_NOT_SUPPORTED))
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

    private fun isVideoItem(item: MediaItem?): Boolean = item?.mediaMetadata?.mediaType == MediaMetadata.MEDIA_TYPE_VIDEO

    /**
     * The video screen was closed. Progress is saved and reported once more, then the player is stopped and the video is
     * removed from the session, so the notification, a headset button and the lock screen have nothing to start.
     */
    private fun closeVideo() {
        if (!::player.isInitialized || !isVideoItem(player.currentMediaItem)) return
        player.pause()
        saveLocalProgress(ended = false, sync = true)
        sendHeartbeat(ended = false)
        clearVideo()
    }

    private fun clearVideo() {
        player.clearMediaItems()
        player.stop()
        // Speed and track choices belong to the video that was open, not to whatever plays next.
        player.setPlaybackSpeed(1f)
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon().clearOverrides()
            .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false).build()
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

    /**
     * Starts a movie or episode for the video screen. The returned result completes when playback has started, or with
     * a reason: `failure` is a [VideoFailure] name and `message` the plain words to show. Waiting for the Engine to
     * prepare a stream keeps the result pending (the screen shows its preparing state with Cancel); cancelling
     * completes it with `cancelled`.
     */
    private fun playVideo(assetId: String, title: String?, subtitle: String?, previousEnded: Boolean): ListenableFuture<SessionResult> {
        val result = SettableFuture.create<SessionResult>()
        videoJob?.cancel()
        if (assetId.isBlank()) {
            result.set(failureResult(VideoFailure.Other, VideoMessages.OTHER))
            return result
        }
        val job = scope.launch {
            try {
                startVideo(assetId, title, subtitle, previousEnded)
                result.set(SessionResult(SessionResult.RESULT_SUCCESS))
            } catch (error: CancellationException) {
                result.set(SessionResult(SessionResult.RESULT_ERROR_UNKNOWN, Bundle().apply { putBoolean(RESULT_CANCELLED, true) }))
                throw error
            } catch (error: VideoStartException) {
                result.set(failureResult(VideoFailure.Other, error.message))
            } catch (error: Exception) {
                // Anything else from the Engine call is classified for the screen's words; nothing is logged with a token.
                val failure = VideoErrors.classify(error)
                result.set(failureResult(failure, VideoMessages.forFailure(failure)))
            }
        }
        videoJob = job
        return result
    }

    private fun failureResult(failure: VideoFailure, message: String?): SessionResult =
        SessionResult(
            SessionResult.RESULT_ERROR_UNKNOWN,
            Bundle().apply {
                putString(RESULT_FAILURE, failure.name)
                putString(RESULT_MESSAGE, message ?: VideoMessages.forFailure(failure))
            },
        )

    private class VideoStartException(message: String) : Exception(message)

    /**
     * A different video is starting while one is loaded (the next episode, or one picked from the Episodes panel): the one
     * leaving is saved and reported first. [ended] is true only when it was finished (the next-episode card), so History
     * marks it watched before the next one begins; picking another episode midway just keeps where it was.
     */
    private fun leavePreviousVideo(newAssetId: String, ended: Boolean) {
        if (!::player.isInitialized) return
        val item = player.currentMediaItem ?: return
        if (!isVideoItem(item) || item.mediaId == newAssetId) return
        saveLocalProgress(ended = ended, sync = true)
        sendHeartbeat(ended = ended)
    }

    private suspend fun startVideo(assetId: String, title: String?, subtitle: String?, previousEnded: Boolean) {
        leavePreviousVideo(assetId, previousEnded)
        activeAssetId = assetId
        // "Resume where I left off" off means every start is from the beginning, streamed or downloaded.
        val resume = kotlinx.coroutines.withTimeoutOrNull(PREFS_WAIT_MS) { client?.playbackPreferences() }?.resumePlayback ?: true
        val metadata = MediaMetadata.Builder()
            .setTitle(title)
            .setSubtitle(subtitle)
            .setIsPlayable(true)
            .setIsBrowsable(false)
            .setMediaType(MediaMetadata.MEDIA_TYPE_VIDEO)
            .build()
        val local = localItem(assetId)
        if (local != null) {
            // A downloaded copy plays from the file and keeps its on-phone progress.
            val item = local.buildUpon().setMediaMetadata(metadata).build()
            player.setMediaItem(item, if (resume) OfflineLogic.resumeStartMs(offline.store().progress(assetId)) else 0L)
            player.prepare()
            player.play()
            return
        }
        val api = client ?: throw VideoStartException(VideoMessages.OTHER)
        var attempt = 0
        while (true) {
            attempt += 1
            val manifest = try {
                api.playbackManifest(assetId, "local")
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // An expired download that cannot stream (no connection) is explained instead of shown as a bare network error.
                val held = offline.store().record(assetId)
                if (held != null && OfflineLogic.isExpired(held, System.currentTimeMillis())) {
                    throw VideoStartException(VideoMessages.DOWNLOAD_EXPIRED)
                }
                throw error
            }
            when (val step = PreparingPoll.next(manifest, attempt)) {
                is PreparingStep.Play -> {
                    // Sidecar subtitles are fetched through the same data source as the video, so they carry the same bearer token.
                    val sidecars = manifest.subtitleTracks.mapNotNull { track ->
                        val url = track.deliveryUrl ?: return@mapNotNull null
                        MediaItem.SubtitleConfiguration.Builder(Uri.parse(url))
                            .setMimeType(SubtitleStyleRules.mimeFor(track.codec, url))
                            .setLanguage(track.language)
                            .setLabel(track.displayName ?: track.language)
                            .setSelectionFlags(if (track.isForced) C.SELECTION_FLAG_FORCED else 0)
                            .build()
                    }
                    val item = MediaItem.Builder().setMediaId(assetId).setUri(step.path).setMediaMetadata(metadata)
                        .setSubtitleConfigurations(sidecars).build()
                    player.setMediaItem(item, if (resume) StreamResume.startMs(manifest.resumeSeconds, manifest.durationSeconds) else 0L)
                    player.prepare()
                    player.play()
                    return
                }
                PreparingStep.Wait -> delay(PreparingPoll.INTERVAL_MS)
                is PreparingStep.Failed -> throw VideoStartException(step.message)
                PreparingStep.TimedOut -> throw VideoStartException(VideoMessages.TIMED_OUT)
            }
        }
    }

    private fun startHeartbeatLoop() {
        heartbeatJob?.cancel()
        heartbeatJob = scope.launch {
            while (true) {
                delay(HeartbeatRules.INTERVAL_MS)
                sendHeartbeat(ended = false)
            }
        }
    }

    /** Tells the Engine where a streamed item is up to. A downloaded item never sends; a failed send never stops playback. */
    private fun sendHeartbeat(ended: Boolean) {
        if (!::player.isInitialized) return
        val item = player.currentMediaItem ?: return
        if (!HeartbeatRules.shouldSend(isLocalItem(item), item.mediaId)) return
        val api = client ?: return
        val report = HeartbeatRules.report(
            assetId = item.mediaId,
            isPlaying = player.isPlaying,
            positionMs = player.currentPosition,
            durationMs = player.duration.takeIf { it != C.TIME_UNSET },
            playbackRate = player.playbackParameters.speed.toDouble(),
            ended = ended,
        )
        scope.launch {
            try {
                api.heartbeat(
                    JSONObject()
                        .put("assetId", report.assetId)
                        .put("isPlaying", report.isPlaying)
                        .put("positionSeconds", report.positionSeconds)
                        .put("durationSeconds", report.durationSeconds ?: JSONObject.NULL)
                        .put("playbackRate", report.playbackRate),
                )
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Best effort: progress is re-sent on the next beat, and playback must never wait on it.
            }
        }
    }

    private suspend fun resolvePlayable(assetId: String): MediaItem? {
        localItem(assetId)?.let { return it }
        val api = client ?: return null
        val manifest = api.playbackManifest(assetId, "local")
        val path = manifest.playablePath() ?: return null
        streamedResumeMs = StreamResume.startMs(manifest.resumeSeconds, manifest.durationSeconds)
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
        heartbeatJob?.cancel()
        videoJob?.cancel()
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
        const val EXTRA_TITLE = "title"
        const val EXTRA_SUBTITLE = "subtitle"

        /** Sent with PLAY_VIDEO when the video being left was finished (the next-episode card), so it is reported as ended first. */
        const val EXTRA_PREVIOUS_ENDED = "previous_ended"

        /** Custom session commands the video screen sends through its media controller. */
        const val COMMAND_PLAY_VIDEO = "com.tuvima.library.PLAY_VIDEO"
        const val COMMAND_CANCEL_VIDEO = "com.tuvima.library.CANCEL_VIDEO"
        const val COMMAND_CLOSE_VIDEO = "com.tuvima.library.CLOSE_VIDEO"

        /** Connection hint set by the video screen's media controller, so the service knows a video screen is open. */
        const val HINT_VIDEO_SCREEN = "video_screen"
        const val RESULT_FAILURE = "failure"
        const val RESULT_MESSAGE = "message"
        const val RESULT_CANCELLED = "cancelled"
        private const val ROOT = "tuvima-root"
        private const val PROGRESS_SAVE_MS = 10_000L
        private const val MIN_RECORDED_SECONDS = 5.0
        private const val PREFS_WAIT_MS = 2_000L
    }
}
