package com.tuvima.library.mobile

import android.content.Intent
import android.net.ConnectivityManager
import android.net.Network
import android.net.Uri
import android.os.Bundle
import androidx.annotation.OptIn
import androidx.concurrent.futures.CallbackToFutureAdapter
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
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
import com.tuvima.library.core.AudioNow
import com.tuvima.library.core.HeartbeatRules
import com.tuvima.library.core.ListenQueue
import com.tuvima.library.core.LockAction
import com.tuvima.library.core.LockScreen
import com.tuvima.library.core.NowPlayingLogic
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.PlayQueue
import com.tuvima.library.core.PlayQueueRequest
import com.tuvima.library.core.PlaybackManifest
import com.tuvima.library.core.QueueShuffle
import com.tuvima.library.core.QueueTrack
import com.tuvima.library.core.SleepTimer
import com.tuvima.library.core.SleepTimerLogic
import com.tuvima.library.core.TransportStep
import com.tuvima.library.core.TvMediaKind
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
import kotlinx.coroutines.flow.collectLatest
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

    // The music and audiobook players (spec native-apps WP10c). The session shows the player through [AudioCardPlayer], so the lock screen's
    // buttons are the contract's; [audioInfo] says what each queued file is, [originalOrder] is what Shuffle off returns to.
    private lateinit var sessionPlayer: AudioCardPlayer
    // The item the screens were last told about; an End of chapter timer is dropped only when this changes (see publishNow).
    private var publishedAssetId: String? = null
    private var queueJob: Job? = null
    private var audioTickJob: Job? = null
    private var originalOrder: List<String> = emptyList()
    private val audioInfo = mutableMapOf<String, AudioNow>()
    private val artCache = LinkedHashMap<String, ByteArray>()
    private var shownChapter: String? = null
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
                // A new network may be home or away: settle on the address that answers before asking for the stream.
                client?.refreshServerAddress()
                resolvePlayable(assetId)?.let { resolved ->
                    // A video or a queued song or book keeps its type, title and cover so the screens and the notification stay as they were.
                    val item = if (current != null) {
                        resolved.buildUpon().setMediaMetadata(current.mediaMetadata).build()
                    } else {
                        resolved
                    }
                    swapCurrent(item, position)
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
        // The address is read the same way: a URL built while the other (home or outside) address was in use is moved onto the
        // one in use now, so a queued item, a playlist or a segment link survives a switch.
        val dataSource = ResolvingDataSource.Factory(DefaultDataSource.Factory(this, DefaultHttpDataSource.Factory())) { dataSpec ->
            if (dataSpec.uri.scheme.equals("file", ignoreCase = true)) {
                dataSpec
            } else {
                val api = client
                val located = if (api == null) dataSpec else dataSpec.withUri(Uri.parse(api.resolveUrl(dataSpec.uri.toString())))
                // Only the server's own addresses get the token; a link to any other host is fetched without it.
                val token = if (api == null) null else runBlocking { api.streamBearerTokenFor(located.uri.toString()) }
                if (token == null) located else located.withAdditionalHeaders(mapOf("Authorization" to "Bearer $token"))
            }
        }
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(dataSource))
            // A call or another app pauses playback (and gives it back), and unplugging headphones pauses instead of playing aloud.
            .setAudioAttributes(mediaAudioAttributes(C.AUDIO_CONTENT_TYPE_MUSIC), true)
            .setHandleAudioBecomingNoisy(true)
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

            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                mediaItem?.mediaId?.let { activeAssetId = it }
                publishNow(mediaItem)
            }

            override fun onPositionDiscontinuity(oldPosition: Player.PositionInfo, newPosition: Player.PositionInfo, reason: Int) {
                // A song that ran to its end and moved on to the next one in the queue is reported as finished (the end of the whole queue is
                // reported by STATE_ENDED below).
                if (reason == Player.DISCONTINUITY_REASON_AUTO_TRANSITION) oldPosition.mediaItem?.let { reportFinished(it, oldPosition.positionMs) }
            }

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
        sessionPlayer = AudioCardPlayer(player)
        session = MediaLibrarySession.Builder(this, sessionPlayer, LibraryCallback()).build()
        getSystemService(ConnectivityManager::class.java).registerDefaultNetworkCallback(networkCallback)
        // The Sleep timer ticks here, where the player is, so it works with the screen off: a timed one pauses when its time is up and an end-of-chapter
        // one at the chapter's end. Setting it back to Off (or starting another) restarts the watch.
        scope.launch {
            AudioBridge.sleepTimer.collectLatest { timer -> if (timer != SleepTimer.Off) watchSleepTimer(timer) }
        }
        refreshLibrary()
    }

    // ---- the music and audiobook players (spec native-apps WP10c) ----

    /**
     * Makes the card the system draws (title, second line, buttons) follow the contract's lock-screen table: the player the session shows is this
     * one. Music has previous and next track and no skip buttons; an audiobook has the profile's skip seconds and, with chapters, Previous and Next
     * chapter; a video is left as the media player has it.
     */
    private inner class AudioCardPlayer(wrapped: Player) : ForwardingPlayer(wrapped) {
        private fun audio(): AudioNow? =
            AudioBridge.now.value.takeIf { !it.isNone && !isVideoItem(wrappedPlayer.currentMediaItem) }

        private fun skip() = AudioBridge.skip.value

        // The session reads the commands again only when the player says they changed, and the wrapped player knows nothing of what this card
        // allows, so the listeners are kept here and told when a different song or book (or its chapters) takes over.
        private val listeners = mutableSetOf<Player.Listener>()

        override fun addListener(listener: Player.Listener) {
            listeners += listener
            super.addListener(listener)
        }

        override fun removeListener(listener: Player.Listener) {
            listeners -= listener
            super.removeListener(listener)
        }

        fun commandsChanged() {
            val commands = availableCommands
            listeners.toList().forEach { it.onAvailableCommandsChanged(commands) }
        }

        override fun getAvailableCommands(): Player.Commands {
            val base = super.getAvailableCommands()
            val now = audio() ?: return base
            val actions = LockScreen.card(NowPlayingLogic.factsFor(now, 0.0, hasNext = false)).actions
            val builder = base.buildUpon()
            fun allow(action: String, vararg commands: Int) {
                if (action in actions) commands.forEach { builder.add(it) } else commands.forEach { builder.remove(it) }
            }
            allow(LockAction.SEEK_BACKWARD, Player.COMMAND_SEEK_BACK)
            allow(LockAction.SEEK_FORWARD, Player.COMMAND_SEEK_FORWARD)
            allow(LockAction.PREVIOUS_TRACK, Player.COMMAND_SEEK_TO_PREVIOUS, Player.COMMAND_SEEK_TO_PREVIOUS_MEDIA_ITEM)
            allow(LockAction.NEXT_TRACK, Player.COMMAND_SEEK_TO_NEXT, Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
            return builder.build()
        }

        override fun isCommandAvailable(command: Int): Boolean = availableCommands.contains(command)

        override fun getSeekBackIncrement(): Long = if (audio() != null) skip().back * 1000L else super.getSeekBackIncrement()

        override fun getSeekForwardIncrement(): Long = if (audio() != null) skip().forward * 1000L else super.getSeekForwardIncrement()

        override fun seekBack() {
            if (audio() == null) return super.seekBack()
            stepAudio(NowPlayingLogic.skip(positionSeconds(), -skip().back, durationSeconds()))
        }

        override fun seekForward() {
            if (audio() == null) return super.seekForward()
            stepAudio(NowPlayingLogic.skip(positionSeconds(), skip().forward, durationSeconds()))
        }

        // An audiobook goes by chapter; a song keeps the player's own rule (a restart after 3 seconds, otherwise the song before).
        override fun seekToPrevious() {
            val now = audio()
            if (now == null || !now.isAudiobook) return super.seekToPrevious()
            stepAudio(NowPlayingLogic.previous(now.kind, positionSeconds(), false, now.chapters))
        }

        override fun seekToPreviousMediaItem() {
            val now = audio()
            if (now == null || !now.isAudiobook) return super.seekToPreviousMediaItem()
            stepAudio(NowPlayingLogic.previous(now.kind, positionSeconds(), false, now.chapters))
        }

        override fun seekToNext() {
            val now = audio()
            if (now == null || !now.isAudiobook) return super.seekToNext()
            stepAudio(NowPlayingLogic.next(now.kind, positionSeconds(), false, now.chapters))
        }

        override fun seekToNextMediaItem() {
            val now = audio()
            if (now == null || !now.isAudiobook) return super.seekToNextMediaItem()
            stepAudio(NowPlayingLogic.next(now.kind, positionSeconds(), false, now.chapters))
        }

        private fun positionSeconds(): Double = wrappedPlayer.currentPosition / 1000.0

        private fun durationSeconds(): Double? = wrappedPlayer.duration.takeIf { it > 0 && it != C.TIME_UNSET }?.let { it / 1000.0 }

        private fun stepAudio(step: TransportStep) {
            if (step is TransportStep.SeekTo) wrappedPlayer.seekTo((step.seconds * 1000).toLong())
        }
    }

    /**
     * Tells the screens what is playing now (nothing, for a video or an empty player), tells the session which buttons the card now has, and drops an
     * end-of-chapter timer that belonged to another item. The same item put back in place (a network change, a fresh stream address) keeps its timer.
     */
    private fun publishNow(item: MediaItem?) {
        val info = if (item == null || isVideoItem(item)) null else audioInfo[item.mediaId]
        AudioBridge.now.value = info?.copy(isDownload = isLocalItem(item)) ?: AudioNow.None
        shownChapter = null
        AudioBridge.sleepTimer.value = NowPlayingLogic.timerAfterItemChange(AudioBridge.sleepTimer.value, publishedAssetId, item?.mediaId)
        publishedAssetId = item?.mediaId
        audioTickJob?.cancel()
        if (info != null && info.isAudiobook && info.chapters.isNotEmpty()) startAudioTick()
        if (::sessionPlayer.isInitialized) sessionPlayer.commandsChanged()
    }

    /** While a book with chapters plays, the card's second line follows the chapter (author · chapter name). */
    private fun startAudioTick() {
        audioTickJob?.cancel()
        audioTickJob = scope.launch {
            while (true) {
                delay(CHAPTER_TICK_MS)
                refreshChapterLine()
            }
        }
    }

    private fun refreshChapterLine() {
        val item = player.currentMediaItem ?: return
        val now = AudioBridge.now.value
        if (now.isNone || !now.isAudiobook || now.assetId != item.mediaId) return
        val name = NowPlayingLogic.chapterName(now.chapters, player.currentPosition / 1000.0) ?: return
        if (name == shownChapter) return
        val first = shownChapter == null
        shownChapter = name
        // The first read only records the chapter the card was built for; later ones replace the line the system draws.
        if (first) return
        val card = LockScreen.card(NowPlayingLogic.factsFor(now, player.currentPosition / 1000.0, hasNext = false))
        val metadata = item.mediaMetadata.buildUpon().setArtist(card.line2).setSubtitle(card.line2).build()
        player.replaceMediaItem(player.currentMediaItemIndex, item.buildUpon().setMediaMetadata(metadata).build())
    }

    private suspend fun watchSleepTimer(timer: SleepTimer) {
        while (true) {
            delay(if (timer is SleepTimer.EndOfChapter) 250L else 1_000L)
            if (AudioBridge.sleepTimer.value != timer) return
            if (SleepTimerLogic.isDue(timer, System.currentTimeMillis(), player.currentPosition / 1000.0)) {
                player.pause()
                AudioBridge.sleepTimer.value = SleepTimer.Off
                return
            }
        }
    }

    /**
     * Puts a freshly resolved copy of the item in play back in its place without throwing the queue away: a single item is simply replaced, one in a
     * queue is swapped where it stands and the position is put back.
     */
    private fun swapCurrent(item: MediaItem, positionMs: Long) {
        if (player.mediaItemCount > 1) {
            val index = player.currentMediaItemIndex
            player.replaceMediaItem(index, item)
            player.seekTo(index, positionMs)
        } else {
            player.setMediaItem(item, positionMs)
        }
        player.prepare()
    }

    /** A song that finished and moved on is reported to the Engine as finished (a downloaded one keeps its on-phone progress). */
    private fun reportFinished(item: MediaItem, positionMs: Long) {
        if (!HeartbeatRules.shouldSend(isLocalItem(item), item.mediaId)) return
        val api = client ?: return
        val report = HeartbeatRules.report(
            assetId = item.mediaId,
            isPlaying = false,
            positionMs = positionMs,
            durationMs = positionMs.takeIf { it > 0 },
            playbackRate = player.playbackParameters.speed.toDouble(),
            ended = true,
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
                // Best effort, like every heartbeat: the next song's beats carry on and playback never waits on this.
            }
        }
    }

    /** Shuffle on mixes the songs after the one in play; off puts them back as the queue started. Only moves are used, so the song in play never stops. */
    private fun setShuffle(on: Boolean) {
        val count = player.mediaItemCount
        if (count > 1) {
            val ids = (0 until count).map { player.getMediaItemAt(it).mediaId }
            // While the queue is still filling the list is partial; the request's full order (kept when it started) is what Shuffle off returns to.
            if (on && !AudioBridge.shuffle.value && queueJob?.isActive != true) originalOrder = ids
            val target = if (on) {
                QueueShuffle.shuffledOrder(ids, player.currentMediaItemIndex, kotlin.random.Random.Default)
            } else {
                QueueShuffle.restoredOrder(ids, player.currentMediaItemIndex, originalOrder)
            }
            QueueShuffle.moves(ids, target).forEach { player.moveMediaItem(it.from, it.to) }
        }
        AudioBridge.shuffle.value = on
    }

    /** The cover of a song or book as bytes for the lock screen (the Engine wants the device's own sign-in, so the system cannot fetch it). */
    private suspend fun artworkBytes(url: String?): ByteArray? {
        if (url.isNullOrBlank()) return null
        artCache[url]?.let { return it }
        val api = client ?: return null
        val bytes = try {
            kotlinx.coroutines.withTimeoutOrNull(ART_WAIT_MS) { api.authenticatedBytes(url) }
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            // No cover on the lock screen is better than no song.
            null
        }
        if (bytes != null) {
            artCache[url] = bytes
            while (artCache.size > ART_CACHE_SIZE) artCache.remove(artCache.keys.first())
        }
        return bytes
    }

    /** The metadata of a song or book: the contract's two lines, the cover bytes and the facts the screens read back. */
    private suspend fun audioMetadata(now: AudioNow, positionSeconds: Double, hasNext: Boolean): MediaMetadata {
        val card = LockScreen.card(NowPlayingLogic.factsFor(now, positionSeconds, hasNext))
        val extras = Bundle().apply { putString(KEY_ART, now.artworkUrl) }
        val builder = MediaMetadata.Builder()
            .setIsPlayable(true)
            .setIsBrowsable(false)
            .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
            .setExtras(extras)
        if (card.visible) {
            builder.setTitle(card.title).setDisplayTitle(card.title).setArtist(card.line2).setSubtitle(card.line2)
            artworkBytes(now.artworkUrl)?.let { builder.setArtworkData(it, MediaMetadata.PICTURE_TYPE_FRONT_COVER) }
        }
        return builder.build()
    }

    /**
     * Resolves one file of a queue into a playable item with its card, or null when the Engine cannot give a stream for it (it is then left out of
     * the queue). A downloaded copy plays from the phone.
     */
    private suspend fun queueItem(track: QueueTrack, source: String?, hasNext: Boolean): MediaItem? {
        val now = AudioNow(
            assetId = track.assetId,
            kind = TvMediaKind.Music,
            title = track.title,
            artist = track.artist,
            album = track.album,
            workId = track.workId,
            artworkUrl = track.artworkUrl,
            sourceName = source,
        )
        val metadata = audioMetadata(now, 0.0, hasNext)
        audioInfo[track.assetId] = now
        localItem(track.assetId)?.let { return it.buildUpon().setMediaMetadata(metadata).build() }
        val api = client ?: return null
        val path: String? = try {
            api.playbackManifest(track.assetId, "local").playablePath()
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            // This song cannot be reached now; the rest of the queue carries on without it.
            null
        }
        if (path == null) return null
        return MediaItem.Builder().setMediaId(track.assetId).setUri(path).setMediaMetadata(metadata).build()
    }

    /**
     * Starts a queue: the song the person picked plays as soon as it is ready, then the songs after it and before it are added one by one as the
     * Engine prepares them, so a long list never makes the first song wait.
     */
    private fun startQueue(request: PlayQueueRequest) {
        queueJob?.cancel()
        queueJob = scope.launch {
            val arranged = QueueShuffle.arranged(request, kotlin.random.Random.Default)
            originalOrder = ListenQueue.originalIds(request)
            AudioBridge.shuffle.value = request.shuffle
            AudioBridge.sleepTimer.value = SleepTimer.Off
            AudioBridge.problem.value = null
            player.setAudioAttributes(mediaAudioAttributes(C.AUDIO_CONTENT_TYPE_MUSIC), true)
            player.setPlaybackSpeed(1f)
            val tracks = arranged.tracks
            val start = arranged.startIndex.coerceIn(0, (tracks.size - 1).coerceAtLeast(0))
            val order = (start until tracks.size) + (0 until start)
            var started = false
            var before = 0
            for (index in order) {
                val item = queueItem(tracks[index], request.sourceName, hasNext = index < tracks.size - 1) ?: continue
                if (!started) {
                    started = true
                    activeAssetId = item.mediaId
                    player.setMediaItem(item, 0L)
                    player.prepare()
                    player.play()
                } else if (index > start) {
                    player.addMediaItem(item)
                } else {
                    // A song before the picked one goes in front of it, in its own order.
                    player.addMediaItem(before, item)
                    before += 1
                }
            }
            if (!started) AudioBridge.problem.value = SONG_NOT_READY
        }
    }

    /**
     * Starts one song or audiobook (a press on a Play with no queue). A book resumes where the Engine (or, for a download, this phone) left off and
     * starts at the speed the phone last used for books; its chapters come from the playback manifest.
     */
    private suspend fun startAudioAsset(assetId: String, title: String?, subtitle: String?, workId: String?, entityType: String?, artworkUrl: String?) {
        AudioBridge.shuffle.value = false
        AudioBridge.sleepTimer.value = SleepTimer.Off
        AudioBridge.problem.value = null
        activeAssetId = assetId
        val api = client
        val local = localItem(assetId)
        // A download plays from the phone, but its chapters and kind still come from the manifest when the Engine can be reached (briefly asked,
        // so a phone with no connection starts the file at once and shows no chapters).
        val manifest = if (api != null) {
            try {
                if (local == null) api.playbackManifest(assetId, "local")
                else kotlinx.coroutines.withTimeoutOrNull(MANIFEST_WAIT_MS) { api.playbackManifest(assetId, "local") }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // The Engine could not be reached: a download still plays (without chapters); a stream is reported below.
                null
            }
        } else {
            null
        }
        val kind = NowPlayingLogic.kindOf(manifest?.mediaType, entityType ?: offline.store().record(assetId)?.entityType)
        val now = AudioNow(
            assetId = assetId,
            kind = kind,
            title = title ?: local?.mediaMetadata?.title?.toString().orEmpty().ifEmpty { "Library" },
            artist = if (kind == TvMediaKind.Music) subtitle else null,
            author = if (kind == TvMediaKind.Audiobook) subtitle else null,
            workId = workId ?: offline.store().record(assetId)?.workId,
            artworkUrl = artworkUrl ?: offline.store().record(assetId)?.artworkUrl,
            chapters = manifest?.chapters.orEmpty(),
        )
        audioInfo[assetId] = now
        val metadata = audioMetadata(now, 0.0, hasNext = false)
        val item: MediaItem
        val startMs: Long
        if (local != null) {
            item = local.buildUpon().setMediaMetadata(metadata).build()
            startMs = OfflineLogic.resumeStartMs(offline.store().progress(assetId))
        } else {
            val path = manifest?.playablePath()
            if (manifest == null || path == null) {
                AudioBridge.problem.value = SONG_NOT_READY
                return
            }
            item = MediaItem.Builder().setMediaId(assetId).setUri(path).setMediaMetadata(metadata).build()
            startMs = StreamResume.startMs(manifest.resumeSeconds, manifest.durationSeconds)
        }
        // Speed and skip seconds belong to this phone and this person (the profile's preferences have no write route for apps).
        val prefs = kotlinx.coroutines.withTimeoutOrNull(PREFS_WAIT_MS) { api?.playbackPreferences() }
        val device = (application as TuvimaMobileApplication).playerChoices.load(api?.currentProfileId())
        AudioBridge.skip.value = PhonePlayerLogic.skip(prefs, device, audio = true)
        player.setAudioAttributes(mediaAudioAttributes(C.AUDIO_CONTENT_TYPE_MUSIC), true)
        player.setPlaybackSpeed(NowPlayingLogic.startSpeed(kind, prefs, device).toFloat())
        player.setMediaItem(item, startMs)
        player.prepare()
        player.play()
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
                swapCurrent(swapped, position)
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
                val title = intent.getStringExtra(EXTRA_TITLE)
                val subtitle = intent.getStringExtra(EXTRA_SUBTITLE)
                val workId = intent.getStringExtra(EXTRA_WORK_ID)
                val entityType = intent.getStringExtra(EXTRA_ENTITY_TYPE)
                val art = intent.getStringExtra(EXTRA_ARTWORK_URL)
                queueJob?.cancel()
                queueJob = scope.launch {
                    try {
                        startAudioAsset(assetId, title, subtitle, workId, entityType, art)
                    } catch (cancelled: CancellationException) {
                        throw cancelled
                    } catch (failure: Exception) {
                        // The Engine could not be asked: the screen says the title could not be played, and nothing else changes.
                        AudioBridge.problem.value = SONG_NOT_READY
                    }
                }
            }
        }
        if (intent?.action == ACTION_PLAY_QUEUE) {
            PlayQueue.fromJson(intent.getStringExtra(EXTRA_QUEUE))?.let { request -> startQueue(request) }
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
            Futures.immediateFuture(LibraryResult.ofItem(folder(ROOT, "Library"), params))

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
                .add(SessionCommand(COMMAND_RELOAD_SUBTITLES, Bundle.EMPTY))
                .add(SessionCommand(COMMAND_SET_SHUFFLE, Bundle.EMPTY))
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
            COMMAND_RELOAD_SUBTITLES -> reloadSubtitles(args.getString(EXTRA_ASSET_ID).orEmpty())
            COMMAND_SET_SHUFFLE -> {
                setShuffle(args.getBoolean(EXTRA_SHUFFLE))
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
            "Resolve Library playback manifests"
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

    private fun mediaAudioAttributes(contentType: Int): AudioAttributes =
        AudioAttributes.Builder().setUsage(C.USAGE_MEDIA).setContentType(contentType).build()

    private fun clearVideo() {
        player.setAudioAttributes(mediaAudioAttributes(C.AUDIO_CONTENT_TYPE_MUSIC), true)
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
        // A song queue still filling in must not add songs behind a video that is starting.
        queueJob?.cancel()
        leavePreviousVideo(assetId, previousEnded)
        // Video is film audio, not music, for the system's mixing and focus rules; clearVideo puts the music setting back.
        player.setAudioAttributes(mediaAudioAttributes(C.AUDIO_CONTENT_TYPE_MOVIE), true)
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
                    val item = MediaItem.Builder().setMediaId(assetId).setUri(step.path).setMediaMetadata(metadata)
                        .setSubtitleConfigurations(sidecarSubtitles(manifest)).build()
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

    /** The Engine's subtitle files as sidecars for a video item (one per track that has a delivery address). */
    private fun sidecarSubtitles(manifest: PlaybackManifest): List<MediaItem.SubtitleConfiguration> =
        manifest.subtitleTracks.mapNotNull { track ->
            val url = track.deliveryUrl ?: return@mapNotNull null
            MediaItem.SubtitleConfiguration.Builder(Uri.parse(url))
                .setMimeType(SubtitleStyleRules.mimeFor(track.codec, url))
                .setLanguage(track.language)
                .setLabel(track.displayName ?: track.language)
                .setSelectionFlags(if (track.isForced) C.SELECTION_FLAG_FORCED else 0)
                .build()
        }

    /**
     * Find another used a result: ask the Engine for the playback manifest again and put the video back in at the same
     * position and play state with the new subtitle files (spec "Find another on TV", WP8). A downloaded copy has none of the
     * Engine's subtitle files, so it is left as it is. The result is an error for anything that could not be done, and the
     * screen then carries on with the video as it was.
     */
    private fun reloadSubtitles(assetId: String): ListenableFuture<SessionResult> {
        val result = SettableFuture.create<SessionResult>()
        scope.launch {
            try {
                val item = player.currentMediaItem
                val api = client
                if (api == null || item == null || item.mediaId != assetId || isLocalItem(item)) {
                    result.set(SessionResult(SessionResult.RESULT_ERROR_BAD_VALUE))
                    return@launch
                }
                val manifest = api.playbackManifest(assetId, "local")
                val position = player.currentPosition
                val wasPlaying = player.playWhenReady
                // The title and type stay as they were; the stream address is the manifest's own, and the subtitle files are the new ones.
                val builder = item.buildUpon().setSubtitleConfigurations(sidecarSubtitles(manifest))
                manifest.playablePath()?.let { builder.setUri(Uri.parse(it)) }
                player.setMediaItem(builder.build(), position)
                player.prepare()
                player.playWhenReady = wasPlaying
                result.set(SessionResult(SessionResult.RESULT_SUCCESS))
            } catch (cancelled: CancellationException) {
                result.set(SessionResult(SessionResult.RESULT_ERROR_UNKNOWN))
                throw cancelled
            } catch (failure: Exception) {
                // Reported to the screen as an error result; the video keeps playing as it was.
                result.set(SessionResult(SessionResult.RESULT_ERROR_UNKNOWN))
            }
        }
        return result
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
                                .setTitle(item.optString("title", "Library"))
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
        queueJob?.cancel()
        audioTickJob?.cancel()
        AudioBridge.now.value = AudioNow.None
        saveLocalProgress(ended = false, sync = true)
        getSystemService(ConnectivityManager::class.java).unregisterNetworkCallback(networkCallback)
        session.release()
        player.release()
        scope.cancel()
        super.onDestroy()
    }

    companion object {
        const val ACTION_PLAY_ASSET = "com.tuvima.library.PLAY_ASSET"

        /** Starts a queue of songs (extra: [EXTRA_QUEUE], a `PlayQueue.toJson` text). */
        const val ACTION_PLAY_QUEUE = "com.tuvima.library.PLAY_QUEUE"
        const val EXTRA_QUEUE = "queue"
        const val EXTRA_WORK_ID = "work_id"
        const val EXTRA_ENTITY_TYPE = "entity_type"
        const val EXTRA_ARTWORK_URL = "artwork_url"
        const val EXTRA_SHUFFLE = "shuffle"

        /** Sent by the Now playing screen: Shuffle on or off for the queue (extra: [EXTRA_SHUFFLE]). */
        const val COMMAND_SET_SHUFFLE = "com.tuvima.library.SET_SHUFFLE"

        /** Where a song's cover lives, kept in its metadata extras for the Queue panel. */
        const val KEY_ART = "art"
        const val SONG_NOT_READY = "This title could not be played."
        private const val CHAPTER_TICK_MS = 1_000L
        private const val ART_WAIT_MS = 3_000L
        private const val ART_CACHE_SIZE = 8
        const val EXTRA_ASSET_ID = "asset_id"
        const val EXTRA_TITLE = "title"
        const val EXTRA_SUBTITLE = "subtitle"

        /** Sent with PLAY_VIDEO when the video being left was finished (the next-episode card), so it is reported as ended first. */
        const val EXTRA_PREVIOUS_ENDED = "previous_ended"

        /** Custom session commands the video screen sends through its media controller. */
        const val COMMAND_PLAY_VIDEO = "com.tuvima.library.PLAY_VIDEO"
        const val COMMAND_CANCEL_VIDEO = "com.tuvima.library.CANCEL_VIDEO"
        const val COMMAND_CLOSE_VIDEO = "com.tuvima.library.CLOSE_VIDEO"

        /** Sent by the video screen after Find another used a result (extra: [EXTRA_ASSET_ID]); reloads the subtitle files at the same position. */
        const val COMMAND_RELOAD_SUBTITLES = "com.tuvima.library.RELOAD_SUBTITLES"

        /** Connection hint set by the video screen's media controller, so the service knows a video screen is open. */
        const val HINT_VIDEO_SCREEN = "video_screen"
        const val RESULT_FAILURE = "failure"
        const val RESULT_MESSAGE = "message"
        const val RESULT_CANCELLED = "cancelled"
        private const val ROOT = "tuvima-root"
        private const val PROGRESS_SAVE_MS = 10_000L
        private const val MIN_RECORDED_SECONDS = 5.0
        private const val PREFS_WAIT_MS = 2_000L
        private const val MANIFEST_WAIT_MS = 2_500L
    }
}
