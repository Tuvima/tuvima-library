package com.tuvima.library.mobile

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color as AndroidColor
import android.graphics.drawable.ColorDrawable
import android.media.AudioManager
import android.os.Bundle
import android.provider.Settings
import android.view.ViewGroup
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.detectVerticalDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.PlaybackException
import androidx.media3.common.PlaybackParameters
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.TrackSelectionParameters
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.session.MediaController
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import androidx.media3.session.SessionToken
import androidx.media3.ui.CaptionStyleCompat
import androidx.media3.ui.PlayerView
import com.google.common.util.concurrent.ListenableFuture
import com.tuvima.library.core.AudioTrack
import com.tuvima.library.core.Chapter
import com.tuvima.library.core.ClockText
import com.tuvima.library.core.CloseAction
import com.tuvima.library.core.NextEpisode
import com.tuvima.library.core.NextEpisodeLogic
import com.tuvima.library.core.NextEpisodeMessages
import com.tuvima.library.core.NextEpisodeOffer
import com.tuvima.library.core.OrientationPolicy
import com.tuvima.library.core.OrientationRule
import com.tuvima.library.core.PlaybackPreferences
import com.tuvima.library.core.PlaybackSegment
import com.tuvima.library.core.PlaybackTrackPicker
import com.tuvima.library.core.ScreenKind
import com.tuvima.library.core.SlideKind
import com.tuvima.library.core.SubtitleStyleRules
import com.tuvima.library.core.SubtitleTrack
import com.tuvima.library.core.VideoFailure
import com.tuvima.library.core.VideoGestures
import com.tuvima.library.core.VideoMessages
import com.tuvima.library.core.VideoSessionPolicy
import com.tuvima.library.core.segmentToOffer
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.util.Locale

/** What the video screen is showing over the picture. */
private sealed interface VideoUiState {
    /** Waiting for the player, or for the Engine to prepare a stream. */
    data object Connecting : VideoUiState
    data object Ready : VideoUiState
    data class Failed(val message: String, val canRetry: Boolean) : VideoUiState
}

private enum class Sheet { None, Audio, Subtitles, Speed, Chapters, Episodes }

/** One selectable audio or subtitle track the player reports. */
private data class TrackOption(
    val group: Tracks.Group,
    val index: Int,
    val label: String,
    val language: String?,
    val isDefault: Boolean,
    val isForced: Boolean,
    val selected: Boolean,
)

/**
 * Full-screen movie and episode player. It shows the same ExoPlayer the media service owns, through a media controller,
 * so the lock screen, headset buttons, progress code and offline replay stay in one place. Landscape comes from
 * [OrientationPolicy]; rotating never restarts playback because the activity handles those configuration changes itself.
 * The main screen stays locked to portrait, so closing this returns to portrait without any code here.
 *
 * Video never plays in the background: going to Home pauses it, and closing the screen (or the video ending) stops the
 * player and clears the video from the session, so no notification or headset button can start its audio.
 */
@OptIn(UnstableApi::class)
class VideoPlayerActivity : ComponentActivity() {
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller by mutableStateOf<MediaController?>(null)
    private var uiState by mutableStateOf<VideoUiState>(VideoUiState.Connecting)
    private var isPlaying by mutableStateOf(false)
    private var buffering by mutableStateOf(false)
    private var tracks by mutableStateOf(Tracks.EMPTY)
    private var textDisabled by mutableStateOf(false)
    private var speed by mutableStateOf(1f)
    private var prefs by mutableStateOf(PlaybackPreferences.Default)
    private var prefsLoaded = false
    private var chapters by mutableStateOf<List<Chapter>>(emptyList())
    private var segments by mutableStateOf<List<PlaybackSegment>>(emptyList())
    private var manifestDurationSeconds by mutableStateOf<Double?>(null)
    private var manifestRequested = false
    private var initialApplied = false
    private var speedApplied = false
    private var assetId = ""
    private var title: String? = null
    private var subtitle: String? = null
    private var showId: String? = null

    // Next episode: the list is asked for once, part way in, and kept for this screen. A failure just hides the feature.
    private var nextEpisodes by mutableStateOf<List<NextEpisode>>(emptyList())
    private var nextRequested = false
    private var cancelledFor by mutableStateOf<String?>(null)
    private var finished by mutableStateOf(false)
    private var advancing = false
    // True between onStart and onStop. The next-episode countdown only runs while the screen is showing.
    private var started by mutableStateOf(false)

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            this@VideoPlayerActivity.isPlaying = isPlaying
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            buffering = playbackState == Player.STATE_BUFFERING
            // The service clears a finished video. With a next episode to offer the screen stays for the card; otherwise it closes
            // (a movie or the last episode returns to the details page).
            if (playbackState == Player.STATE_ENDED) {
                finished = true
                if (NextEpisodeLogic.nextOf(nextEpisodes, assetId) == null) finish()
            }
        }

        override fun onTracksChanged(tracks: Tracks) {
            this@VideoPlayerActivity.tracks = tracks
            applyInitialSelection()
        }

        override fun onTrackSelectionParametersChanged(parameters: TrackSelectionParameters) {
            textDisabled = parameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
        }

        override fun onPlaybackParametersChanged(playbackParameters: PlaybackParameters) {
            speed = playbackParameters.speed
        }

        override fun onPlayerError(error: PlaybackException) {
            uiState = when (error.errorCode) {
                PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED,
                PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT,
                -> VideoUiState.Failed(VideoMessages.NETWORK, canRetry = true)
                PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND,
                PlaybackException.ERROR_CODE_IO_NO_PERMISSION,
                -> VideoUiState.Failed(VideoMessages.UNAVAILABLE, canRetry = false)
                else -> VideoUiState.Failed(VideoMessages.OTHER, canRetry = true)
            }
        }
    }

    // If the media service goes away, forget the controller so Retry connects again instead of talking to nothing.
    private val disconnectListener = object : MediaController.Listener {
        override fun onDisconnected(controller: MediaController) {
            if (this@VideoPlayerActivity.controller === controller) {
                this@VideoPlayerActivity.controller = null
                controllerFuture = null
                isPlaying = false
                uiState = VideoUiState.Failed(VideoMessages.OTHER, canRetry = true)
            }
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        assetId = intent.getStringExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID).orEmpty()
        title = intent.getStringExtra(TuvimaMediaLibraryService.EXTRA_TITLE)
        subtitle = intent.getStringExtra(TuvimaMediaLibraryService.EXTRA_SUBTITLE)
        showId = intent.getStringExtra(EXTRA_SHOW_ID)?.takeIf(String::isNotBlank)
        if (assetId.isBlank()) {
            finish()
            return
        }
        requestedOrientation = when (OrientationPolicy.ruleFor(ScreenKind.Video)) {
            OrientationRule.SensorLandscape -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            OrientationRule.Sensor -> ActivityInfo.SCREEN_ORIENTATION_SENSOR
            OrientationRule.Portrait -> ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        window.setBackgroundDrawable(ColorDrawable(AndroidColor.BLACK))
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        window.attributes = window.attributes.apply {
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
        // Preferences never hold playback up: wait a moment, then carry on with the defaults.
        lifecycleScope.launch {
            val client = (application as TuvimaMobileApplication).client
            prefs = withTimeoutOrNull(PREFS_WAIT_MS) { client?.playbackPreferences() } ?: PlaybackPreferences.Default
            prefsLoaded = true
            applyInitialSelection()
            applyInitialSpeed()
        }
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                VideoScreen()
            }
        }
    }

    override fun onStart() {
        super.onStart()
        started = true
        connect()
    }

    override fun onStop() {
        started = false
        releaseController(VideoSessionPolicy.onScreenStop(isFinishing))
        super.onStop()
    }

    override fun onDestroy() {
        releaseController(VideoSessionPolicy.onScreenStop(isFinishing))
        super.onDestroy()
    }

    private fun connect() {
        if (controllerFuture != null || assetId.isBlank()) return
        uiState = VideoUiState.Connecting
        val token = SessionToken(this, ComponentName(this, TuvimaMediaLibraryService::class.java))
        val hints = Bundle().apply { putBoolean(TuvimaMediaLibraryService.HINT_VIDEO_SCREEN, true) }
        val future = MediaController.Builder(this, token)
            .setConnectionHints(hints)
            .setListener(disconnectListener)
            .buildAsync()
        controllerFuture = future
        future.addListener({
            val connected = try {
                future.get()
            } catch (error: Exception) {
                // The service could not be reached; the screen says so in words and offers Retry.
                null
            }
            if (controllerFuture !== future) {
                // The screen stopped while connecting; nothing is left to attach to.
                if (connected != null) MediaController.releaseFuture(future)
                return@addListener
            }
            if (connected == null) {
                controllerFuture = null
                uiState = VideoUiState.Failed(VideoMessages.OTHER, canRetry = true)
                return@addListener
            }
            controller = connected
            connected.addListener(playerListener)
            isPlaying = connected.isPlaying
            buffering = connected.playbackState == Player.STATE_BUFFERING
            tracks = connected.currentTracks
            textDisabled = connected.trackSelectionParameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
            speed = connected.playbackParameters.speed
            if (connected.currentMediaItem?.mediaId == assetId && connected.playbackState != Player.STATE_IDLE) {
                // Coming back to a video that is already loaded: show it paused where it was.
                uiState = VideoUiState.Ready
                loadManifestExtras()
            } else {
                requestPlay()
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun requestPlay(previousEnded: Boolean = false, isNextEpisode: Boolean = false) {
        val active = controller
        if (active == null) {
            controllerFuture = null
            connect()
            return
        }
        uiState = VideoUiState.Connecting
        initialApplied = false
        val args = Bundle().apply {
            putString(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId)
            putString(TuvimaMediaLibraryService.EXTRA_TITLE, title)
            putString(TuvimaMediaLibraryService.EXTRA_SUBTITLE, subtitle)
            putBoolean(TuvimaMediaLibraryService.EXTRA_PREVIOUS_ENDED, previousEnded)
        }
        val pending = active.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_PLAY_VIDEO, Bundle.EMPTY), args)
        pending.addListener({
            if (controller !== active) return@addListener
            val result: SessionResult? = try {
                pending.get()
            } catch (error: Exception) {
                // The command could not be delivered; treated as a connection problem below.
                null
            }
            if (result == null) {
                uiState = VideoUiState.Failed(VideoMessages.NETWORK, canRetry = true)
                return@addListener
            }
            val extras = result.extras
            if (result.resultCode == SessionResult.RESULT_SUCCESS) {
                uiState = VideoUiState.Ready
                advancing = false
                loadManifestExtras()
                applyInitialSelection()
                applyInitialSpeed()
            } else if (!extras.getBoolean(TuvimaMediaLibraryService.RESULT_CANCELLED)) {
                val failure = runCatching {
                    VideoFailure.valueOf(extras.getString(TuvimaMediaLibraryService.RESULT_FAILURE).orEmpty())
                }.getOrDefault(VideoFailure.Other)
                advancing = false
                // No connection while moving to the next episode means it is not on this phone.
                val message = if (isNextEpisode && failure == VideoFailure.Network) NextEpisodeMessages.NOT_DOWNLOADED
                else extras.getString(TuvimaMediaLibraryService.RESULT_MESSAGE) ?: VideoMessages.forFailure(failure)
                uiState = VideoUiState.Failed(message, canRetry = failure != VideoFailure.Unavailable)
            }
        }, ContextCompat.getMainExecutor(this))
    }

    /** Asks which episodes follow this one, once. Offline or refused just means no next-episode card and no Episodes panel. */
    private fun loadNextEpisodes() {
        val show = showId ?: return
        if (nextRequested) return
        nextRequested = true
        val client = (application as TuvimaMobileApplication).client ?: return
        val current = assetId
        lifecycleScope.launch {
            try {
                val result = client.nextEpisodes(show, current, NEXT_EPISODES_COUNT)
                if (assetId == current) nextEpisodes = result.episodes
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Optional extra: the video keeps playing without it.
            }
        }
    }

    /**
     * Moves to another episode through the same PLAY_VIDEO path, so resume, heartbeats and on-phone progress are not
     * duplicated. [previousEnded] is true from the next-episode card (the one leaving was finished) and false from the
     * Episodes panel (it just keeps where it was).
     */
    private fun playEpisode(episode: NextEpisode, previousEnded: Boolean) {
        // Video never plays in the background: a countdown that ends while the screen is stopped does nothing.
        if (advancing || !started) return
        advancing = true
        assetId = episode.assetId
        title = episode.title.ifBlank { title }
        subtitle = com.tuvima.library.core.OfflineLogic.episodeLabel(episode.seasonNumber, episode.episodeNumber)
        chapters = emptyList()
        segments = emptyList()
        manifestDurationSeconds = null
        manifestRequested = false
        speedApplied = false
        nextEpisodes = emptyList()
        nextRequested = false
        cancelledFor = null
        finished = false
        requestPlay(previousEnded = previousEnded, isNextEpisode = true)
    }

    /** True when this phone holds a finished, unexpired copy of the episode in play (so it has only that file's own tracks). */
    private fun playingDownload(): Boolean {
        val record = (application as TuvimaMobileApplication).offline.store().record(assetId) ?: return false
        return record.isComplete && !com.tuvima.library.core.OfflineLogic.isExpired(record, System.currentTimeMillis())
    }

    /** Chapters and skip segments come from the manifest; without them the buttons simply do not appear. */
    private fun loadManifestExtras() {
        if (manifestRequested) return
        manifestRequested = true
        val client = (application as TuvimaMobileApplication).client ?: return
        lifecycleScope.launch {
            try {
                val manifest = client.playbackManifest(assetId, "local")
                chapters = manifest.chapters
                segments = manifest.segments
                manifestDurationSeconds = manifest.durationSeconds
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Offline or refused: chapters and skip buttons are optional extras, so the video plays without them.
            }
        }
    }

    private fun optionsFor(type: Int): List<TrackOption> = buildList {
        tracks.groups.forEach { group ->
            if (group.type != type) return@forEach
            for (i in 0 until group.length) {
                if (!group.isTrackSupported(i)) continue
                val format = group.getTrackFormat(i)
                add(
                    TrackOption(
                        group = group,
                        index = i,
                        label = trackLabel(format, size + 1),
                        language = format.language?.takeIf { it != "und" },
                        isDefault = format.selectionFlags and C.SELECTION_FLAG_DEFAULT != 0,
                        isForced = format.selectionFlags and C.SELECTION_FLAG_FORCED != 0,
                        selected = group.isTrackSelected(i),
                    ),
                )
            }
        }
    }

    private fun trackLabel(format: Format, number: Int): String {
        format.label?.takeIf(String::isNotBlank)?.let { return it }
        val language = format.language?.takeIf { it != "und" }
        if (language != null) {
            val name = Locale.forLanguageTag(language).getDisplayLanguage(Locale.ENGLISH)
            if (name.isNotBlank() && !name.equals(language, ignoreCase = true)) return name
            return language
        }
        return "Track $number"
    }

    /** Picks the starting audio and subtitles from the profile's preferences, once per video, once tracks are known. */
    private fun applyInitialSelection() {
        val active = controller ?: return
        if (initialApplied || !prefsLoaded || active.currentMediaItem?.mediaId != assetId) return
        val audio = optionsFor(C.TRACK_TYPE_AUDIO)
        val text = optionsFor(C.TRACK_TYPE_TEXT)
        if (audio.isEmpty() && active.currentTracks.groups.none { it.type == C.TRACK_TYPE_VIDEO }) return
        initialApplied = true
        val choice = PlaybackTrackPicker.pick(
            audioTracks = audio.mapIndexed { i, o -> AudioTrack(i, o.language, null, o.label, o.isDefault, null) },
            subtitleTracks = text.mapIndexed { i, o -> SubtitleTrack(i, o.language, null, o.label, o.isDefault, o.isForced, null) },
            audioLanguage = prefs.audioLanguage,
            subtitleLanguage = prefs.subtitleLanguage,
            showSubtitles = prefs.subtitleMode,
        )
        select(
            audio = choice.audioIndex?.let { audio.getOrNull(it) },
            subtitle = choice.subtitleIndex?.let { text.getOrNull(it) },
            subtitlesOff = choice.subtitleIndex == null,
        )
    }

    private fun applyInitialSpeed() {
        val active = controller ?: return
        if (speedApplied || !prefsLoaded || uiState != VideoUiState.Ready) return
        speedApplied = true
        val wanted = prefs.defaultPlaybackSpeed.toFloat()
        if (wanted != 1f) active.setPlaybackSpeed(wanted)
    }

    private fun select(audio: TrackOption?, subtitle: TrackOption?, subtitlesOff: Boolean) {
        val active = controller ?: return
        val builder = active.trackSelectionParameters.buildUpon()
        audio?.let { builder.setOverrideForType(TrackSelectionOverride(it.group.mediaTrackGroup, it.index)) }
        if (subtitlesOff) {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
        } else if (subtitle != null) {
            builder.setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
            builder.setOverrideForType(TrackSelectionOverride(subtitle.group.mediaTrackGroup, subtitle.index))
        }
        active.trackSelectionParameters = builder.build()
    }

    private fun cancelAndClose() {
        controller?.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_CANCEL_VIDEO, Bundle.EMPTY), Bundle.EMPTY)
        finish()
    }

    /**
     * Lets go of the controller. Closing for good tells the service to stop and clear the video; going to Home only pauses
     * (and cancels any wait for the Engine), so a video never keeps playing in the background.
     */
    private fun releaseController(action: CloseAction) {
        val active = controller
        if (active != null) {
            active.removeListener(playerListener)
            when (action) {
                CloseAction.StopAndClear -> active.sendCustomCommand(
                    SessionCommand(TuvimaMediaLibraryService.COMMAND_CLOSE_VIDEO, Bundle.EMPTY),
                    Bundle.EMPTY,
                )
                CloseAction.Pause -> {
                    if (uiState == VideoUiState.Connecting) {
                        active.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_CANCEL_VIDEO, Bundle.EMPTY), Bundle.EMPTY)
                    }
                    active.pause()
                }
            }
        }
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
        isPlaying = false
    }

    private fun seekBy(deltaMs: Long, durationMs: Long) {
        val active = controller ?: return
        val limit = if (durationMs > 0) durationMs else Long.MAX_VALUE
        active.seekTo((active.currentPosition + deltaMs).coerceIn(0L, limit))
    }

    private fun systemBrightness(): Float {
        val current = window.attributes.screenBrightness
        if (current >= 0f) return current
        return Settings.System.getInt(contentResolver, Settings.System.SCREEN_BRIGHTNESS, 128) / 255f
    }

    @Composable
    private fun VideoScreen() {
        val active = controller
        var controlsVisible by remember { mutableStateOf(true) }
        // Bumped on every touch of a control so the 3 second hide timer starts over.
        var touch by remember { mutableIntStateOf(0) }
        var positionMs by remember { mutableLongStateOf(0L) }
        var durationMs by remember { mutableLongStateOf(0L) }
        var dragging by remember { mutableStateOf<Float?>(null) }
        var sheet by remember { mutableStateOf(Sheet.None) }
        var hint by remember { mutableStateOf<String?>(null) }
        var hintTick by remember { mutableIntStateOf(0) }
        val audioManager = remember { getSystemService(AUDIO_SERVICE) as AudioManager }

        fun showHint(text: String) {
            hint = text
            hintTick += 1
        }

        LaunchedEffect(active) {
            while (true) {
                if (active != null && dragging == null) {
                    positionMs = active.currentPosition.coerceAtLeast(0L)
                    durationMs = active.duration.takeIf { it > 0 && it != C.TIME_UNSET } ?: 0L
                    if (positionMs >= NEXT_FETCH_AFTER_MS) loadNextEpisodes()
                }
                delay(500)
            }
        }
        LaunchedEffect(controlsVisible, isPlaying, touch, sheet) {
            if (controlsVisible && isPlaying && sheet == Sheet.None) {
                delay(CONTROLS_HIDE_MS)
                controlsVisible = false
            }
        }
        LaunchedEffect(hintTick) {
            if (hint != null) {
                delay(HINT_MS)
                hint = null
            }
        }

        val durationSeconds = if (durationMs > 0) durationMs / 1000.0 else manifestDurationSeconds
        val next = NextEpisodeLogic.nextOf(nextEpisodes, assetId)
        val nextOffer = if (next == null) NextEpisodeOffer.None else NextEpisodeLogic.nextEpisodeOffer(
            positionSeconds = positionMs / 1000.0,
            durationSeconds = durationSeconds,
            segments = segments,
            autoplay = prefs.autoplayNextEpisode,
            ended = finished,
            cancelled = cancelledFor == assetId,
        )
        // The card replaces the skip button while it is showing.
        val offer = if (nextOffer == NextEpisodeOffer.None) segmentToOffer(positionMs / 1000.0, segments, durationSeconds) else null

        Box(Modifier.fillMaxSize().background(Color.Black)) {
            AndroidView(
                modifier = Modifier.fillMaxSize(),
                factory = { context ->
                    PlayerView(context).apply {
                        layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
                        useController = false
                        keepScreenOn = true
                    }
                },
                update = { view ->
                    view.player = active
                    val background = prefs.subtitleBackground
                    view.subtitleView?.apply {
                        setFractionalTextSize(SubtitleStyleRules.sizeFraction(prefs.subtitleSize))
                        setStyle(
                            CaptionStyleCompat(
                                AndroidColor.WHITE,
                                if (background) 0x99000000.toInt() else AndroidColor.TRANSPARENT,
                                AndroidColor.TRANSPARENT,
                                if (background) CaptionStyleCompat.EDGE_TYPE_NONE else CaptionStyleCompat.EDGE_TYPE_OUTLINE,
                                AndroidColor.BLACK,
                                null,
                            ),
                        )
                    }
                },
            )
            // One tap shows or hides the controls; a double tap on a half seeks; a vertical slide changes brightness or volume.
            Box(
                Modifier.fillMaxSize()
                    .pointerInput(prefs) {
                        detectTapGestures(
                            onTap = { controlsVisible = !controlsVisible },
                            onDoubleTap = { offset ->
                                val delta = VideoGestures.doubleTapSeekMs(
                                    offset.x, size.width.toFloat(), prefs.skipBackSeconds, prefs.skipForwardSeconds,
                                )
                                seekBy(delta, durationMs)
                                showHint(if (delta < 0) "-${prefs.skipBackSeconds} s" else "+${prefs.skipForwardSeconds} s")
                            },
                        )
                    }
                    .pointerInput(Unit) {
                        var kind = SlideKind.Volume
                        var start = 0f
                        var total = 0f
                        detectVerticalDragGestures(
                            onDragStart = { offset ->
                                kind = VideoGestures.slideKind(offset.x, size.width.toFloat())
                                total = 0f
                                start = if (kind == SlideKind.Volume) {
                                    audioManager.getStreamVolume(AudioManager.STREAM_MUSIC).toFloat() /
                                        audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
                                } else {
                                    systemBrightness()
                                }
                            },
                            onVerticalDrag = { change, dragAmount ->
                                change.consume()
                                total += dragAmount
                                val fraction = VideoGestures.slideFraction(start, total, size.height.toFloat())
                                if (kind == SlideKind.Volume) {
                                    val max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC)
                                    audioManager.setStreamVolume(AudioManager.STREAM_MUSIC, VideoGestures.volumeIndex(fraction, max), 0)
                                    showHint("Volume ${Math.round(fraction * 100)}%")
                                } else {
                                    window.attributes = window.attributes.apply { screenBrightness = VideoGestures.brightness(fraction) }
                                    showHint("Brightness ${Math.round(fraction * 100)}%")
                                }
                            },
                        )
                    },
            )
            when (val state = uiState) {
                VideoUiState.Connecting -> ConnectingPanel()
                is VideoUiState.Failed -> FailedPanel(state)
                VideoUiState.Ready -> {
                    if (buffering) {
                        CircularProgressIndicator(Modifier.align(Alignment.Center).size(48.dp), color = Color.White)
                    }
                    hint?.let { text ->
                        Box(Modifier.align(Alignment.TopCenter).safeDrawingPadding().padding(top = 64.dp)) {
                            Text(
                                text,
                                color = Color.White,
                                modifier = Modifier.background(Color(0xCC000000), CircleShape).padding(horizontal = 16.dp, vertical = 8.dp),
                            )
                        }
                    }
                    offer?.let { skip ->
                        Box(Modifier.align(Alignment.BottomEnd).safeDrawingPadding().padding(end = 16.dp, bottom = if (controlsVisible) 150.dp else 24.dp)) {
                            Button(
                                onClick = {
                                    controller?.seekTo((skip.targetSeconds * 1000).toLong())
                                    touch += 1
                                },
                                modifier = Modifier.sizeMinimum(),
                            ) { Text(skip.label) }
                        }
                    }
                    if (next != null && nextOffer != NextEpisodeOffer.None) {
                        NextEpisodeCard(
                            next = next,
                            countdown = nextOffer == NextEpisodeOffer.Countdown,
                            raised = controlsVisible,
                            onPlay = { playEpisode(next, previousEnded = true) },
                            onCancel = { cancelledFor = assetId },
                        )
                    }
                    if (controlsVisible) {
                        Controls(
                            positionMs = positionMs,
                            durationMs = durationMs,
                            dragging = dragging,
                            hasChapters = chapters.isNotEmpty(),
                            hasEpisodes = nextEpisodes.isNotEmpty(),
                            onDrag = { dragging = it; touch += 1 },
                            onDragEnd = {
                                dragging?.let { active?.seekTo(it.toLong()) }
                                dragging = null
                            },
                            onSeekBy = { delta ->
                                seekBy(delta, durationMs)
                                touch += 1
                            },
                            onTogglePlay = {
                                active?.let { if (it.isPlaying) it.pause() else it.play() }
                                touch += 1
                            },
                            onOpenSheet = {
                                sheet = it
                                touch += 1
                            },
                        )
                    }
                }
            }
            when (sheet) {
                Sheet.None -> Unit
                Sheet.Audio -> {
                    val options = optionsFor(C.TRACK_TYPE_AUDIO)
                    PickerSheet("Audio", onDismiss = { sheet = Sheet.None }) {
                        if (options.isEmpty()) Text("No audio choices for this video.", color = Color.White, modifier = Modifier.padding(16.dp))
                        options.forEach { option ->
                            OptionRow(option.label, option.selected) {
                                select(audio = option, subtitle = null, subtitlesOff = false)
                                sheet = Sheet.None
                            }
                        }
                    }
                }
                Sheet.Subtitles -> {
                    val options = optionsFor(C.TRACK_TYPE_TEXT)
                    PickerSheet("Subtitles", onDismiss = { sheet = Sheet.None }) {
                        // A downloaded copy keeps its first video and audio only (no sidecar or embedded subtitles): say so
                        // rather than offer a picker that cannot work.
                        if (options.isEmpty() && playingDownload()) {
                            Text("Not included in this download.", color = Color.White, modifier = Modifier.padding(16.dp))
                            return@PickerSheet
                        }
                        OptionRow("Off", textDisabled || options.none { it.selected }) {
                            select(audio = null, subtitle = null, subtitlesOff = true)
                            sheet = Sheet.None
                        }
                        options.forEach { option ->
                            OptionRow(option.label, !textDisabled && option.selected) {
                                select(audio = null, subtitle = option, subtitlesOff = false)
                                sheet = Sheet.None
                            }
                        }
                    }
                }
                Sheet.Speed -> PickerSheet("Playback speed", onDismiss = { sheet = Sheet.None }) {
                    SPEEDS.forEach { value ->
                        OptionRow(speedLabel(value), kotlin.math.abs(speed - value) < 0.01f) {
                            controller?.setPlaybackSpeed(value)
                            sheet = Sheet.None
                        }
                    }
                }
                Sheet.Episodes -> PickerSheet("Episodes", onDismiss = { sheet = Sheet.None }) {
                    OptionRow("Now playing: " + listOfNotNull(subtitle, title).joinToString(" · "), true) { sheet = Sheet.None }
                    nextEpisodes.forEach { episode ->
                        OptionRow(NextEpisodeLogic.cardLine(episode), false) {
                            sheet = Sheet.None
                            playEpisode(episode, previousEnded = false)
                        }
                    }
                }
                Sheet.Chapters -> PickerSheet("Chapters", onDismiss = { sheet = Sheet.None }) {
                    val currentIndex = chapters.indexOfLast { it.startSeconds * 1000 <= positionMs }
                    chapters.forEachIndexed { i, chapter ->
                        OptionRow(
                            "${ClockText.format((chapter.startSeconds * 1000).toLong())}  ${chapter.title.ifBlank { "Chapter ${chapter.index + 1}" }}",
                            i == currentIndex,
                        ) {
                            controller?.seekTo((chapter.startSeconds * 1000).toLong())
                            sheet = Sheet.None
                        }
                    }
                }
            }
        }
    }

    @kotlin.OptIn(ExperimentalMaterial3Api::class)
    @Composable
    private fun PickerSheet(title: String, onDismiss: () -> Unit, content: @Composable ColumnScope.() -> Unit) {
        ModalBottomSheet(
            onDismissRequest = onDismiss,
            sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
            containerColor = Color(0xFF1B1A22),
        ) {
            Column(Modifier.fillMaxWidth().verticalScroll(rememberScrollState()).padding(bottom = 16.dp)) {
                Text(title, color = Color.White, style = MaterialTheme.typography.titleMedium, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
                content()
            }
        }
    }

    @Composable
    private fun OptionRow(label: String, selected: Boolean, onClick: () -> Unit) {
        Row(
            Modifier.fillMaxWidth().defaultMinSize(minHeight = 48.dp).clickable(onClick = onClick).padding(horizontal = 16.dp)
                .semantics { contentDescription = if (selected) "$label, selected" else label },
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(if (selected) "✓" else "", color = Color.White, modifier = Modifier.size(28.dp))
            Text(label, color = Color.White)
        }
    }

    @Composable
    private fun ConnectingPanel() {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Text(title.orEmpty(), color = Color.White, style = MaterialTheme.typography.titleLarge, textAlign = TextAlign.Center)
            subtitle?.let { Text(it, color = Color.White.copy(alpha = 0.7f), textAlign = TextAlign.Center) }
            CircularProgressIndicator(Modifier.padding(top = 20.dp), color = Color.White)
            Text("Getting your video ready", color = Color.White.copy(alpha = 0.8f), modifier = Modifier.padding(top = 12.dp))
            OutlinedButton(onClick = { cancelAndClose() }, modifier = Modifier.padding(top = 16.dp).sizeMinimum()) {
                Text("Cancel")
            }
        }
    }

    @Composable
    private fun FailedPanel(state: VideoUiState.Failed) {
        Column(
            Modifier.fillMaxSize().safeDrawingPadding().padding(24.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.Center,
        ) {
            Text(state.message, color = Color.White, style = MaterialTheme.typography.titleMedium, textAlign = TextAlign.Center)
            Row(Modifier.padding(top = 20.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                if (state.canRetry) {
                    Button(onClick = { requestPlay() }, modifier = Modifier.sizeMinimum()) { Text("Retry") }
                }
                OutlinedButton(onClick = { finish() }, modifier = Modifier.sizeMinimum()) { Text("Close") }
            }
        }
    }

    @Composable
    private fun Controls(
        positionMs: Long,
        durationMs: Long,
        dragging: Float?,
        hasChapters: Boolean,
        hasEpisodes: Boolean,
        onDrag: (Float) -> Unit,
        onDragEnd: () -> Unit,
        onSeekBy: (Long) -> Unit,
        onTogglePlay: () -> Unit,
        onOpenSheet: (Sheet) -> Unit,
    ) {
        val scrim = Color(0x99000000)
        Box(Modifier.fillMaxSize().safeDrawingPadding()) {
            Row(
                Modifier.fillMaxWidth().background(scrim).align(Alignment.TopStart).padding(horizontal = 8.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(
                    onClick = { finish() },
                    modifier = Modifier.size(48.dp).semantics { contentDescription = "Close video" },
                ) { Text("✕", color = Color.White, fontSize = 20.sp) }
                Text(
                    title.orEmpty(),
                    color = Color.White,
                    style = MaterialTheme.typography.titleMedium,
                    maxLines = 1,
                    modifier = Modifier.padding(start = 8.dp),
                )
            }
            Row(
                Modifier.align(Alignment.Center),
                horizontalArrangement = Arrangement.spacedBy(24.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(
                    onClick = { onSeekBy(-prefs.skipBackSeconds * 1000L) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Back ${prefs.skipBackSeconds} seconds" },
                ) { Text("-${prefs.skipBackSeconds}", color = Color.White, fontSize = 18.sp) }
                IconButton(
                    onClick = onTogglePlay,
                    modifier = Modifier.size(72.dp).background(scrim, CircleShape)
                        .semantics { contentDescription = if (isPlaying) "Pause" else "Play" },
                ) { Text(if (isPlaying) "❚❚" else "▶", color = Color.White, fontSize = 26.sp) }
                IconButton(
                    onClick = { onSeekBy(prefs.skipForwardSeconds * 1000L) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Forward ${prefs.skipForwardSeconds} seconds" },
                ) { Text("+${prefs.skipForwardSeconds}", color = Color.White, fontSize = 18.sp) }
            }
            Column(Modifier.fillMaxWidth().background(scrim).align(Alignment.BottomStart).padding(horizontal = 16.dp, vertical = 8.dp)) {
                Row(horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    SheetButton("Audio", "Choose audio") { onOpenSheet(Sheet.Audio) }
                    SheetButton("Subtitles", "Choose subtitles") { onOpenSheet(Sheet.Subtitles) }
                    SheetButton(speedLabel(speed), "Playback speed") { onOpenSheet(Sheet.Speed) }
                    if (hasChapters) SheetButton("Chapters", "Chapters") { onOpenSheet(Sheet.Chapters) }
                    if (hasEpisodes) SheetButton("Episodes", "Episodes") { onOpenSheet(Sheet.Episodes) }
                }
                val shown = dragging ?: positionMs.toFloat()
                Slider(
                    value = shown.coerceIn(0f, durationMs.coerceAtLeast(1L).toFloat()),
                    onValueChange = onDrag,
                    onValueChangeFinished = onDragEnd,
                    valueRange = 0f..durationMs.coerceAtLeast(1L).toFloat(),
                    enabled = durationMs > 0,
                    colors = SliderDefaults.colors(thumbColor = Color.White, activeTrackColor = Color.White),
                    modifier = Modifier.fillMaxWidth().semantics { contentDescription = "Seek bar" },
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(ClockText.format(shown.toLong()), color = Color.White, style = MaterialTheme.typography.bodySmall)
                    Text(ClockText.format(durationMs), color = Color.White, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }

    /**
     * The next-episode card: the next title with its season and episode label. With autoplay on it counts down
     * ([NextEpisodeLogic.COUNTDOWN_SECONDS] seconds) with Play now and Cancel; with it off, or after Cancel, just Play next.
     */
    @Composable
    private fun NextEpisodeCard(next: NextEpisode, countdown: Boolean, raised: Boolean, onPlay: () -> Unit, onCancel: () -> Unit) {
        var elapsed by remember(next.assetId, countdown) { mutableIntStateOf(0) }
        LaunchedEffect(next.assetId, countdown) {
            if (!countdown) return@LaunchedEffect
            while (NextEpisodeLogic.secondsLeft(elapsed) > 0) {
                delay(1_000L)
                // Waits (without resetting) while the screen is stopped or the person has paused; carries on when they come back.
                if (NextEpisodeLogic.shouldTick(started, isPlaying, finished)) elapsed += 1
            }
            onPlay()
        }
        val scrim = Color(0xCC000000)
        Box(Modifier.fillMaxSize().safeDrawingPadding()) {
            Column(
                Modifier.align(Alignment.BottomEnd).padding(end = 16.dp, bottom = if (raised) 150.dp else 24.dp)
                    .background(scrim, androidx.compose.foundation.shape.RoundedCornerShape(12.dp)).padding(16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text("Next episode", color = Color.White, style = MaterialTheme.typography.labelMedium)
                Text(NextEpisodeLogic.cardLine(next), color = Color.White, style = MaterialTheme.typography.titleSmall, maxLines = 2)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(onClick = onPlay, modifier = Modifier.sizeMinimum()) {
                        Text(if (countdown) "Play now (${NextEpisodeLogic.secondsLeft(elapsed)})" else "Play next")
                    }
                    if (countdown) OutlinedButton(onClick = onCancel, modifier = Modifier.sizeMinimum()) { Text("Cancel") }
                }
            }
        }
    }

    @Composable
    private fun SheetButton(label: String, description: String, onClick: () -> Unit) {
        TextButton(
            onClick = onClick,
            modifier = Modifier.sizeMinimum().semantics { contentDescription = description },
        ) { Text(label, color = Color.White) }
    }

    companion object {
        private const val CONTROLS_HIDE_MS = 3_000L
        private const val HINT_MS = 800L
        private const val PREFS_WAIT_MS = 2_000L
        private const val NEXT_FETCH_AFTER_MS = 30_000L
        private const val NEXT_EPISODES_COUNT = 10
        private const val EXTRA_SHOW_ID = "show_id"
        private val SPEEDS = listOf(0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f)

        private fun speedLabel(value: Float): String =
            if (value == Math.round(value).toFloat()) "${Math.round(value)}x" else "${value}x"

        fun intent(context: Context, assetId: String, title: String?, subtitle: String?, showId: String? = null): Intent =
            Intent(context, VideoPlayerActivity::class.java)
                .putExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId)
                .putExtra(TuvimaMediaLibraryService.EXTRA_TITLE, title)
                .putExtra(TuvimaMediaLibraryService.EXTRA_SUBTITLE, subtitle)
                .putExtra(EXTRA_SHOW_ID, showId)
    }
}

/** Buttons on the video screen are at least 48dp tall and wide. */
private fun Modifier.sizeMinimum(): Modifier = defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
