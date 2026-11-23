package com.tuvima.library.mobile

import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import androidx.annotation.OptIn
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.core.content.ContextCompat
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaController
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionToken
import com.google.common.util.concurrent.ListenableFuture
import com.tuvima.library.core.AudioNow
import com.tuvima.library.core.ListenQueue
import com.tuvima.library.core.NowPlayingLogic
import com.tuvima.library.core.OfflineBookmarkLogic
import com.tuvima.library.core.PhoneBookmarks
import com.tuvima.library.core.PhoneDeviceChoices
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.PlayQueue
import com.tuvima.library.core.PlayQueueRequest
import com.tuvima.library.core.PlaybackPreferences
import com.tuvima.library.core.QueueRowAction
import com.tuvima.library.core.QueueTrack
import com.tuvima.library.core.RepeatCycle
import com.tuvima.library.core.TransportStep
import com.tuvima.library.core.TvBookmark
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvRepeatMode
import com.tuvima.library.core.TvSkip
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull

/**
 * The music and audiobook screens' hold on the media service (spec native-apps WP10c). It connects a media controller to the service the same way the
 * video screen does and shows what the player is doing as Compose state: playing or paused, the place in the file, Repeat, the queue and the speed.
 * What is playing, the Sleep timer, Shuffle and the skip seconds come from [AudioBridge]. Every press on the screens goes through here, so the
 * mini player and the Now playing screen always agree. It lives as long as the app.
 */
@OptIn(UnstableApi::class)
internal class AudioPlayerHost(private val app: TuvimaMobileApplication) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var future: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null
    private var prefs: PlaybackPreferences? = null

    var isPlaying by mutableStateOf(false)
        private set
    var ended by mutableStateOf(false)
        private set
    var positionMs by mutableLongStateOf(0L)
        private set
    var durationMs by mutableLongStateOf(0L)
        private set
    var repeat by mutableStateOf(TvRepeatMode.Off)
        private set
    var queue by mutableStateOf<List<QueueTrack>>(emptyList())
        private set
    var currentIndex by mutableIntStateOf(-1)
        private set
    var speed by mutableFloatStateOf(1f)
        private set

    /** What this phone keeps for the person playing (the audiobook speed, the skip seconds, the timeline choice, "Show on timeline"). */
    var device by mutableStateOf(PhoneDeviceChoices())
        private set

    val bookmarks = AudioBookmarks(app, scope)

    private val listener = object : Player.Listener {
        override fun onEvents(player: Player, events: Player.Events) {
            refresh(
                full = events.containsAny(
                    Player.EVENT_TIMELINE_CHANGED,
                    Player.EVENT_MEDIA_ITEM_TRANSITION,
                    Player.EVENT_MEDIA_METADATA_CHANGED,
                ),
            )
        }
    }

    private val disconnectListener = object : MediaController.Listener {
        override fun onDisconnected(controller: MediaController) {
            if (this@AudioPlayerHost.controller === controller) {
                this@AudioPlayerHost.controller = null
                future = null
                isPlaying = false
            }
        }
    }

    /** Joins the media service. Called once something is playing (the service tells the screens through [AudioBridge.now]). */
    fun connect() {
        if (future != null) return
        val token = SessionToken(app, ComponentName(app, TuvimaMediaLibraryService::class.java))
        val pending = MediaController.Builder(app, token).setListener(disconnectListener).buildAsync()
        future = pending
        pending.addListener({
            val connected = try {
                pending.get()
            } catch (error: Exception) {
                // The service could not be reached; the mini player simply does not draw until it can.
                null
            }
            if (future !== pending) {
                if (connected != null) MediaController.releaseFuture(pending)
                return@addListener
            }
            if (connected == null) {
                future = null
                return@addListener
            }
            controller = connected
            connected.addListener(listener)
            loadDevice()
            refresh(full = true)
        }, ContextCompat.getMainExecutor(app))
    }

    /** Lets go of the service when the app's screens go away. The music keeps playing: the service owns it. */
    fun release() {
        controller?.removeListener(listener)
        future?.let { MediaController.releaseFuture(it) }
        future = null
        controller = null
        isPlaying = false
    }

    private fun profileId(): String? = app.client?.currentProfileId()

    private fun loadDevice() {
        device = app.playerChoices.load(profileId())
        publishSkip()
        val client = app.client ?: return
        scope.launch {
            // The profile's own skip seconds and speed; with no answer the phone's choices and the defaults carry on.
            val fetched = try {
                withTimeoutOrNull(PREFS_WAIT_MS) { client.playbackPreferences() }
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                null
            }
            if (fetched != null) {
                prefs = fetched
                publishSkip()
            }
        }
    }

    private fun publishSkip() {
        AudioBridge.skip.value = PhonePlayerLogic.skip(prefs, device, audio = true)
    }

    /** The skip seconds in force for audio: this phone's choice, else the profile's, else 10 and 30. */
    fun skip(): TvSkip = AudioBridge.skip.value

    /** Changes what this phone keeps for the person (kept on the phone, never sent to the Engine). */
    fun updateDevice(change: (PhoneDeviceChoices) -> PhoneDeviceChoices) {
        device = app.playerChoices.update(profileId(), change)
        publishSkip()
    }

    // ---- reading the player ----

    private fun refresh(full: Boolean) {
        val c = controller ?: return
        isPlaying = c.isPlaying
        ended = c.playbackState == Player.STATE_ENDED
        repeat = when (c.repeatMode) {
            Player.REPEAT_MODE_ALL -> TvRepeatMode.All
            Player.REPEAT_MODE_ONE -> TvRepeatMode.One
            else -> TvRepeatMode.Off
        }
        speed = c.playbackParameters.speed
        currentIndex = c.currentMediaItemIndex
        if (full) queue = (0 until c.mediaItemCount).map { trackOf(c.getMediaItemAt(it)) }
        tick()
    }

    /** Reads the place in the file. The screens call it about twice a second while something is loaded. */
    fun tick() {
        val c = controller ?: return
        positionMs = c.currentPosition.coerceAtLeast(0L)
        durationMs = c.duration.takeIf { it != C.TIME_UNSET && it > 0 } ?: 0L
    }

    private fun trackOf(item: MediaItem): QueueTrack = QueueTrack(
        assetId = item.mediaId,
        title = item.mediaMetadata.title?.toString().orEmpty(),
        // The card's second line (artist · album) is what the queue shows under a song.
        artist = item.mediaMetadata.artist?.toString(),
        artworkUrl = item.mediaMetadata.extras?.getString(TuvimaMediaLibraryService.KEY_ART),
    )

    fun positionSeconds(): Double = positionMs / 1000.0

    fun durationSeconds(): Double? = durationMs.takeIf { it > 0 }?.let { it / 1000.0 }

    // ---- starting playback ----

    /** Plays these songs from the start song, in this order or shuffled; the service resolves each one and keeps the queue. */
    fun playQueue(request: PlayQueueRequest) {
        val cleaned = PlayQueue.normalise(request)
        if (cleaned.tracks.isEmpty()) return
        AudioBridge.problem.value = null
        ContextCompat.startForegroundService(
            app,
            Intent(app, TuvimaMediaLibraryService::class.java)
                .setAction(TuvimaMediaLibraryService.ACTION_PLAY_QUEUE)
                .putExtra(TuvimaMediaLibraryService.EXTRA_QUEUE, PlayQueue.toJson(cleaned)),
        )
    }

    // ---- the transport ----

    fun togglePlay() {
        val c = controller ?: return
        if (c.isPlaying) {
            c.pause()
        } else {
            if (c.playbackState == Player.STATE_ENDED) c.seekToDefaultPosition()
            c.play()
        }
    }

    private fun apply(step: TransportStep) {
        val c = controller ?: return
        when (step) {
            is TransportStep.SeekTo -> c.seekTo((step.seconds * 1000).toLong())
            TransportStep.PreviousItem -> c.seekToPreviousMediaItem()
            TransportStep.NextItem -> c.seekToNextMediaItem()
            TransportStep.Nothing -> Unit
        }
    }

    fun previous(now: AudioNow) {
        apply(NowPlayingLogic.previous(now.kind, positionSeconds(), NowPlayingLogic.hasPreviousSong(currentIndex, queue.size, repeat), now.chapters))
    }

    fun next(now: AudioNow) {
        apply(NowPlayingLogic.next(now.kind, positionSeconds(), NowPlayingLogic.hasNextSong(currentIndex, queue.size, repeat), now.chapters))
    }

    /** Skip back or forward by the profile's seconds (negative goes back). */
    fun skipBy(deltaSeconds: Int) {
        apply(NowPlayingLogic.skip(positionSeconds(), deltaSeconds, durationSeconds()))
    }

    fun seekToSeconds(seconds: Double) {
        controller?.seekTo((seconds.coerceAtLeast(0.0) * 1000).toLong())
        tick()
    }

    fun cycleRepeat() {
        val c = controller ?: return
        c.repeatMode = when (RepeatCycle.next(repeat)) {
            TvRepeatMode.All -> Player.REPEAT_MODE_ALL
            TvRepeatMode.One -> Player.REPEAT_MODE_ONE
            TvRepeatMode.Off -> Player.REPEAT_MODE_OFF
        }
    }

    fun setShuffle(on: Boolean) {
        val c = controller ?: return
        c.sendCustomCommand(
            SessionCommand(TuvimaMediaLibraryService.COMMAND_SET_SHUFFLE, Bundle.EMPTY),
            Bundle().apply { putBoolean(TuvimaMediaLibraryService.EXTRA_SHUFFLE, on) },
        )
    }

    /** Sets the book's speed and keeps it on this phone as the speed audiobooks start at. */
    fun setSpeed(value: Double) {
        val clamped = TvPlayerLogic.clampSpeed(value)
        controller?.setPlaybackSpeed(clamped.toFloat())
        speed = clamped.toFloat()
        updateDevice { it.copy(audiobookSpeed = clamped) }
    }

    // ---- the queue ----

    fun playIndex(index: Int) {
        val c = controller ?: return
        if (index !in 0 until c.mediaItemCount) return
        c.seekToDefaultPosition(index)
        c.play()
    }

    /** One of a Queue row's actions: Play next, Move up, Move down or Remove. */
    fun queueAction(action: QueueRowAction, index: Int) {
        val c = controller ?: return
        val move = ListenQueue.moveFor(action, index, c.currentMediaItemIndex)
        if (move == null) {
            if (index != c.currentMediaItemIndex && index in 0 until c.mediaItemCount) c.removeMediaItem(index)
        } else if (move.from in 0 until c.mediaItemCount && move.to in 0 until c.mediaItemCount) {
            c.moveMediaItem(move.from, move.to)
        }
    }

    /** Clear removes the songs still to come and never stops the one playing. */
    fun clearUpcoming() {
        val c = controller ?: return
        val from = c.currentMediaItemIndex + 1
        if (from < c.mediaItemCount) c.removeMediaItems(from, c.mediaItemCount)
    }

    private companion object {
        const val PREFS_WAIT_MS = 2_000L
    }
}

/**
 * The bookmarks of the audiobook in play: read once per title, "Bookmark at {0}" saved at once (kept on the phone and sent later for a download with
 * no connection), Edit through the Engine's update route and Delete. The same routes and offline functions the video player uses; the Engine decides
 * what a profile may see and a client never filters by its own profile.
 */
@OptIn(UnstableApi::class)
internal class AudioBookmarks(private val app: TuvimaMobileApplication, private val scope: CoroutineScope) {
    var items by mutableStateOf<List<TvBookmark>>(emptyList())
        private set
    var loaded by mutableStateOf(false)
        private set
    var saveFailed by mutableStateOf(false)
        private set
    private var forAsset: String? = null

    /** The work the bookmarks hang on, or null (a song, or a title the Engine named no work for). */
    fun workOf(now: AudioNow): String? = now.workId?.takeIf { PhoneBookmarks.canBookmark(it, isMusic = !now.isAudiobook) }

    /** Reads this title's bookmarks once; with no connection a downloaded title shows what this phone kept. */
    fun load(now: AudioNow) {
        val work = workOf(now) ?: return
        if (forAsset == now.assetId) return
        forAsset = now.assetId
        items = emptyList()
        loaded = false
        saveFailed = false
        val client = app.client
        scope.launch {
            val read = try {
                client?.bookmarks(work) ?: app.offline.bookmarksFor(work)
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // No connection: show the bookmarks this phone holds for the title (an empty list when it is not a download).
                app.offline.bookmarksFor(work)
            }
            if (forAsset == now.assetId) {
                items = read
                loaded = true
            }
        }
    }

    /** "Bookmark at {0}": saved at the moment it was pressed, with the note typed beside it when there is one. */
    fun add(now: AudioNow, note: String, positionSeconds: Double, durationSeconds: Double?) {
        val work = workOf(now) ?: return
        val body = PhoneBookmarks.createBody(now.assetId, positionSeconds, durationSeconds, note) ?: return
        val client = app.client
        saveFailed = false
        scope.launch {
            try {
                if (client == null) throw java.io.IOException("No connection")
                items = PhoneBookmarks.withAdded(items, client.createBookmark(work, body))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // Kept on the phone when this is a download (its note is not kept); otherwise the panel says it could not save.
                if (now.isDownload && app.offline.addBookmarkOffline(now.assetId, positionSeconds, durationSeconds)) {
                    items = app.offline.bookmarksFor(work)
                } else {
                    saveFailed = true
                }
            }
        }
    }

    fun rename(id: String, name: String, note: String) {
        val body = PhoneBookmarks.updateBody(name, note) ?: return
        val client = app.client ?: return
        saveFailed = false
        scope.launch {
            try {
                items = PhoneBookmarks.replace(items, client.updateBookmark(id, body))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // The panel says it could not save; the list stays as it was.
                saveFailed = true
            }
        }
    }

    fun delete(id: String) {
        saveFailed = false
        if (OfflineBookmarkLogic.isWaiting(id)) {
            // Made on this phone and not sent yet: nothing exists at the Engine to delete.
            app.offline.removeWaitingBookmark(id)
            items = PhoneBookmarks.without(items, id)
            return
        }
        val client = app.client ?: return
        scope.launch {
            try {
                if (client.deleteBookmark(id) in 200..299) items = PhoneBookmarks.without(items, id) else saveFailed = true
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                // The panel says it could not delete; the list stays as it was.
                saveFailed = true
            }
        }
    }
}
