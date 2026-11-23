package com.tuvima.library.mobile

import android.app.PendingIntent
import android.app.PictureInPictureParams
import android.app.RemoteAction
import android.content.BroadcastReceiver
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.content.res.Configuration
import android.content.pm.ActivityInfo
import android.graphics.Color as AndroidColor
import android.graphics.drawable.ColorDrawable
import android.graphics.drawable.Icon
import android.media.AudioManager
import android.os.Bundle
import android.os.PowerManager
import android.util.Rational
import android.view.KeyEvent
import android.view.accessibility.AccessibilityManager
import android.view.accessibility.CaptioningManager
import androidx.activity.OnBackPressedCallback
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
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.semantics.CustomAccessibilityAction
import androidx.compose.ui.semantics.LiveRegionMode
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.customActions
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.liveRegion
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
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
import com.tuvima.library.core.CoroutineDwellScheduler
import com.tuvima.library.core.FindAnotherLanguages
import com.tuvima.library.core.FindAnotherModel
import com.tuvima.library.core.FindAnotherSheet
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.FindAnotherWording
import com.tuvima.library.core.DownloadedEpisodes
import com.tuvima.library.core.NextEpisode
import com.tuvima.library.core.NextEpisodeLogic
import com.tuvima.library.core.NextEpisodeMessages
import com.tuvima.library.core.NextEpisodeOffer
import com.tuvima.library.core.OfflineBookmarkLogic
import com.tuvima.library.core.CloseStep
import com.tuvima.library.core.OrientationPolicy
import com.tuvima.library.core.PhoneBookmarks
import com.tuvima.library.core.PhoneDeviceChoices
import com.tuvima.library.core.PhoneMenuRow
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.PhoneTool
import com.tuvima.library.core.PictureInPicturePolicy
import com.tuvima.library.core.SubtitleMemory
import com.tuvima.library.core.TvAudioTrack
import com.tuvima.library.core.TvBookmark
import com.tuvima.library.core.TvControl
import com.tuvima.library.core.TvDeviceChoices
import com.tuvima.library.core.TvEpisode
import com.tuvima.library.core.TvEpisodes
import com.tuvima.library.core.TvMediaKind
import com.tuvima.library.core.TvNote
import com.tuvima.library.core.TvPanel
import com.tuvima.library.core.TvPanels
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvPlayerWords
import com.tuvima.library.core.TvPlayingNow
import com.tuvima.library.core.TvSeason
import com.tuvima.library.core.TvSkip
import com.tuvima.library.core.TvSubtitleAppearance
import com.tuvima.library.core.TvSubtitleTrack
import com.tuvima.library.core.VideoAccessibility
import com.tuvima.library.core.VideoKeyCommand
import com.tuvima.library.core.VideoKeys
import com.tuvima.library.core.OrientationRule
import com.tuvima.library.core.PlaybackPreferences
import com.tuvima.library.core.PlaybackSegment
import com.tuvima.library.core.PlaybackTrackPicker
import com.tuvima.library.core.ScreenKind
import com.tuvima.library.core.ShowSubtitles
import com.tuvima.library.core.SlideKind
import com.tuvima.library.core.SubtitleStyleRules
import com.tuvima.library.core.SubtitleTrack
import com.tuvima.library.core.TextTrackKind
import com.tuvima.library.core.VideoFailure
import com.tuvima.library.core.VideoGestures
import com.tuvima.library.core.VideoMessages
import com.tuvima.library.core.VideoSessionPolicy
import com.tuvima.library.core.findAnotherApi
import com.tuvima.library.core.segmentToOffer
import com.tuvima.library.core.StreamRecovery
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Job
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

/** The one panel open over the video (the contract's Panels table); opening another closes the first. */
private enum class Sheet { None, Episodes, AudioSubtitles, Appearance, Bookmarks, Settings, Speed, SkipBack, SkipForward, Info }

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
    private var workId: String? = null
    private var showTitle by mutableStateOf<String?>(null)
    private var kind by mutableStateOf(TvMediaKind.Film)

    // What this phone keeps for the person playing (skip seconds, Autoplay, speed, subtitle look, the subtitle last chosen): the Engine has no write route for these.
    private var device by mutableStateOf(PhoneDeviceChoices())
    private var prefsFromEngine = false

    // The show's seasons (Episodes panel, Previous and Next) and this title's bookmarks. Failures just hide the feature.
    private var seasons by mutableStateOf<List<TvSeason>>(emptyList())
    private var seasonsRequested = false
    private var seasonShown by mutableIntStateOf(0)
    private var bookmarks by mutableStateOf<List<TvBookmark>>(emptyList())
    private var bookmarksLoaded by mutableStateOf(false)
    private var bookmarkSaveFailed by mutableStateOf(false)
    private var bookmarksRequested = false
    private var sheet by mutableStateOf(Sheet.None)

    // Next episode: the list is asked for once, part way in, and kept for this screen. A failure just hides the feature.
    private var nextEpisodes by mutableStateOf<List<NextEpisode>>(emptyList())
    private var nextRequested = false
    private var cancelledFor by mutableStateOf<String?>(null)
    private var finished by mutableStateOf(false)
    private var advancing = false
    // The controls' visibility lives here (not in the screen) so Back and Escape can close them before leaving.
    private var controlsVisible by mutableStateOf(true)
    // Picture-in-Picture: the floating window is the one visible exception to "video never plays in the background".
    private var inPip by mutableStateOf(false)
    private var countdownShowing by mutableStateOf(false)
    private var pipReceiver: BroadcastReceiver? = null
    private var errorGraceJob: Job? = null
    // True between onStart and onStop. The next-episode countdown only runs while the screen is showing.
    private var started by mutableStateOf(false)

    // Find another (spec "Find another on TV", WP8): the bottom sheet over the video, opened from the Audio & subtitles view.
    // The entry shows only when the sources check said Ready. The shared model owns the search, the reads and the choice.
    private var canFindSubtitles by mutableStateOf(false)
    private var findState by mutableStateOf<FindAnotherState?>(null)
    private var findModel: FindAnotherModel? = null
    private var findLanguages: List<String> = emptyList()
    // False once the sheet is closed; the model can outlive it only while a choice is still being applied.
    private var findSheetOpen = false
    private var resumeAfterFind = false
    // Set after the Engine's new subtitle files are loaded: the next tracks update switches the new file on.
    private var pendingSubtitleReselect = false

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            this@VideoPlayerActivity.isPlaying = isPlaying
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            buffering = playbackState == Player.STATE_BUFFERING
            // The service is swapping in a fresh stream (or playback is back): a held-back stream error is not shown.
            if (playbackState == Player.STATE_BUFFERING || playbackState == Player.STATE_READY) errorGraceJob?.cancel()
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
            reselectSubtitlesIfPending()
        }

        override fun onTrackSelectionParametersChanged(parameters: TrackSelectionParameters) {
            textDisabled = parameters.disabledTrackTypes.contains(C.TRACK_TYPE_TEXT)
        }

        override fun onPlaybackParametersChanged(playbackParameters: PlaybackParameters) {
            speed = playbackParameters.speed
        }

        override fun onPlayerError(error: PlaybackException) {
            // An expired token shows up as a bad HTTP status. The media service recovers from that once, so the message waits
            // a few seconds and is dropped if playback starts again.
            if (StreamRecovery.defersErrorScreen(error.errorCode == PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS)) {
                errorGraceJob?.cancel()
                errorGraceJob = lifecycleScope.launch {
                    delay(StreamRecovery.ERROR_GRACE_MS)
                    uiState = VideoUiState.Failed(VideoMessages.OTHER, canRetry = true)
                }
                return
            }
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
        workId = intent.getStringExtra(EXTRA_WORK_ID)?.takeIf(String::isNotBlank)
        kind = if (showId != null) TvMediaKind.Episode else TvMediaKind.Film
        if (assetId.isBlank()) {
            finish()
            return
        }
        val app = application as TuvimaMobileApplication
        device = app.playerChoices.load(app.client?.currentProfileId())
        // A downloaded title knows its own work, so its bookmarks work with no help from the screen that started it.
        if (workId == null) workId = app.offline.store().record(assetId)?.workId?.takeIf(String::isNotBlank)
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
        // Back closes the controls first, then the screen (a screen reader or keyboard user is never trapped behind hidden controls).
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() {
                if (VideoKeys.closeAction(controlsVisible) == CloseStep.HideControls) controlsVisible = false else finish()
            }
        })
        // Preferences never hold playback up: wait a moment, then carry on with the defaults.
        lifecycleScope.launch {
            val client = (application as TuvimaMobileApplication).client
            val fetched = withTimeoutOrNull(PREFS_WAIT_MS) { client?.playbackPreferences() }
            // Subtitles start off unless the profile's own preferences were read (or a choice was remembered on this phone).
            prefsFromEngine = fetched != null
            prefs = fetched ?: PlaybackPreferences.Default
            prefsLoaded = true
            applyInitialSelection()
            applyInitialSpeed()
        }
        setContent {
            TuvimaTheme {
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
        // A window on screen keeps playing; with the screen off nothing does.
        val interactive = getSystemService(PowerManager::class.java)?.isInteractive != false
        releaseController(VideoSessionPolicy.onScreenStop(isFinishing, inPictureInPicture = inPip && interactive))
        super.onStop()
    }

    // Home while a video plays shrinks it to a floating window instead of pausing, where the phone supports that.
    override fun onUserLeaveHint() {
        super.onUserLeaveHint()
        val enter = PictureInPicturePolicy.shouldEnter(
            supported = packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE),
            isPlaying = isPlaying,
            isPreparing = uiState == VideoUiState.Connecting,
            hasError = uiState is VideoUiState.Failed,
            countdownAboutToAdvance = countdownShowing,
        )
        if (!enter) return
        try {
            enterPictureInPictureMode(pipParams())
        } catch (unsupported: IllegalStateException) {
            // The system refused (feature off for this window); the ordinary pause-on-Home rule applies.
        } catch (unsupported: IllegalArgumentException) {
            // A bad parameter must never crash the screen; the ordinary pause-on-Home rule applies.
        }
    }

    /** The "..." menu's Picture in picture: floats the video like Home does, where the phone allows it. */
    private fun enterPip() {
        try {
            enterPictureInPictureMode(pipParams())
        } catch (unsupported: IllegalStateException) {
            // The system refused (feature off for this window); the video stays full screen.
        } catch (unsupported: IllegalArgumentException) {
            // A bad parameter must never crash the screen; the video stays full screen.
        }
    }

    private fun pipParams(): PictureInPictureParams {
        val size = controller?.videoSize
        val (width, height) = PictureInPicturePolicy.aspectRatio(size?.width ?: 0, size?.height ?: 0)
        val toggle = RemoteAction(
            Icon.createWithResource(this, if (isPlaying) android.R.drawable.ic_media_pause else android.R.drawable.ic_media_play),
            if (isPlaying) "Pause" else "Play",
            if (isPlaying) "Pause video" else "Play video",
            PendingIntent.getBroadcast(this, 0, Intent(ACTION_PIP_TOGGLE).setPackage(packageName), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT),
        )
        return PictureInPictureParams.Builder().setAspectRatio(Rational(width, height)).setActions(listOf(toggle)).build()
    }

    override fun onPictureInPictureModeChanged(isInPictureInPictureMode: Boolean, newConfig: Configuration) {
        super.onPictureInPictureModeChanged(isInPictureInPictureMode, newConfig)
        inPip = isInPictureInPictureMode
        if (isInPictureInPictureMode) {
            controlsVisible = false
            val receiver = object : BroadcastReceiver() {
                override fun onReceive(context: android.content.Context?, intent: Intent?) {
                    controller?.let { if (it.isPlaying) it.pause() else it.play() }
                    if (inPip) setPictureInPictureParams(pipParams())
                }
            }
            pipReceiver = receiver
            ContextCompat.registerReceiver(this, receiver, IntentFilter(ACTION_PIP_TOGGLE), ContextCompat.RECEIVER_NOT_EXPORTED)
        } else {
            pipReceiver?.let { unregisterReceiver(it) }
            pipReceiver = null
            // Expanding brings the screen back (started); closing the window leaves it stopped and ends the video like Close.
            if (VideoSessionPolicy.onPictureInPictureGone(lifecycle.currentState.isAtLeast(Lifecycle.State.STARTED)) != null) {
                releaseController(CloseAction.StopAndClear)
                finish()
            }
        }
    }

    // A keyboard, D-pad or remote: Space and Play/Pause toggle, the arrows seek, Escape or Back closes the controls and then the screen.
    override fun onKeyDown(keyCode: Int, event: KeyEvent): Boolean {
        val duration = controller?.duration?.takeIf { it > 0 && it != C.TIME_UNSET } ?: 0L
        return when (VideoKeys.commandFor(keyCode)) {
            VideoKeyCommand.TogglePlay -> { controller?.let { if (it.isPlaying) it.pause() else it.play() }; true }
            VideoKeyCommand.SeekBack -> { seekBy(-skip.back * 1000L, duration); true }
            VideoKeyCommand.SeekForward -> { seekBy(skip.forward * 1000L, duration); true }
            VideoKeyCommand.Close ->
                // Back is also handled by the back dispatcher; Escape comes here.
                if (keyCode == VideoKeys.KEYCODE_ESCAPE) {
                    if (VideoKeys.closeAction(controlsVisible) == CloseStep.HideControls) controlsVisible = false else finish()
                    true
                } else {
                    super.onKeyDown(keyCode, event)
                }
            VideoKeyCommand.None -> super.onKeyDown(keyCode, event)
        }
    }

    override fun onDestroy() {
        findModel?.close()
        pipReceiver?.let { runCatching { unregisterReceiver(it) } }
        pipReceiver = null
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
        val app = application as TuvimaMobileApplication
        val client = app.client ?: return
        val current = assetId
        lifecycleScope.launch {
            try {
                val result = client.nextEpisodes(show, current, NEXT_EPISODES_COUNT)
                if (assetId == current) nextEpisodes = result.episodes
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // The Engine cannot be reached: the next episode is offered from the downloads on this phone, when there is one.
                if (assetId == current) {
                    nextEpisodes = DownloadedEpisodes.after(app.offline.store().snapshot(), show, current, System.currentTimeMillis())
                }
            }
        }
    }

    /** Reads the show's seasons once: they feed the Episodes panel, Previous and Next, and the episode's own work for its bookmarks. */
    private fun loadSeasons() {
        val show = showId ?: return
        if (seasonsRequested) return
        seasonsRequested = true
        val client = (application as TuvimaMobileApplication).client ?: return
        lifecycleScope.launch {
            try {
                val json = client.details("tvShow", show)
                seasons = TvEpisodes.seasons(json) { it }
                json.optString("title").takeIf { it.isNotBlank() }?.let { showTitle = it }
                seasonShown = TvEpisodes.seasonIndexOf(seasons, assetId)
                TvEpisodes.find(seasons, assetId)?.workId?.takeIf { it.isNotBlank() }?.let { workId = it }
                loadBookmarks()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Offline or refused: no Episodes panel, Previous stays dimmed, and the video plays on.
            }
        }
    }

    private fun toNextEpisode(episode: TvEpisode) = NextEpisode(
        workId = episode.workId.takeIf { it.isNotBlank() },
        assetId = episode.assetId,
        title = episode.title,
        seasonNumber = episode.season,
        episodeNumber = episode.number,
        stillUrl = episode.stillUrl,
    )

    // ---- the phone's own choices ------------------------------------------------------------------------------------

    private val skip: TvSkip get() = PhonePlayerLogic.skip(prefs, device, audio = false)

    private fun updateDevice(change: (PhoneDeviceChoices) -> PhoneDeviceChoices) {
        val app = application as TuvimaMobileApplication
        device = app.playerChoices.update(app.client?.currentProfileId(), change)
    }

    /** Sets the speed now and remembers it for this person on this phone. */
    private fun setSpeed(value: Double) {
        val speedNow = TvPlayerLogic.clampSpeed(value)
        controller?.setPlaybackSpeed(speedNow.toFloat())
        updateDevice { it.copy(videoSpeed = speedNow) }
    }

    private fun positionSeconds(): Double = (controller?.currentPosition ?: 0L).coerceAtLeast(0L) / 1000.0

    private fun durationSeconds(): Double? =
        controller?.duration?.takeIf { it > 0 && it != C.TIME_UNSET }?.let { it / 1000.0 } ?: manifestDurationSeconds

    private fun seekToSeconds(seconds: Double) {
        controller?.seekTo((seconds * 1000).toLong())
    }

    // ---- what a tap on a panel row does -----------------------------------------------------------------------------

    private fun onEpisodesRow(key: String) {
        if (key == "season") {
            if (seasons.size > 1) seasonShown = (seasonShown + 1) % seasons.size
        } else if (key.startsWith("ep:")) {
            val picked = TvEpisodes.find(seasons, key.removePrefix("ep:"))
            sheet = Sheet.None
            if (picked != null && !picked.assetId.equals(assetId, ignoreCase = true)) playEpisode(toNextEpisode(picked), previousEnded = false)
        }
    }

    private fun onEpisodesSwitch(key: String, autoplayNow: Boolean) {
        if (key == "autoplay") updateDevice { it.copy(autoplay = !autoplayNow) }
    }

    private fun onAudioSubtitlesRow(key: String, audioOptions: List<TrackOption>, textOptions: List<TrackOption>) {
        when {
            key.startsWith("audio:") ->
                audioOptions.getOrNull(key.removePrefix("audio:").toIntOrNull() ?: -1)?.let { select(audio = it, subtitle = null, subtitlesOff = false) }
            key == "sub:off" -> {
                select(audio = null, subtitle = null, subtitlesOff = true)
                updateDevice { it.copy(rememberedSubtitle = SubtitleMemory.remember(pickedOff = true, pickedLanguage = null)) }
            }
            key.startsWith("sub:") -> textOptions.getOrNull(key.removePrefix("sub:").toIntOrNull() ?: -1)?.let { option ->
                select(audio = null, subtitle = option, subtitlesOff = false)
                // The next video starts with this language; a track with no language keeps what was remembered before.
                updateDevice {
                    it.copy(rememberedSubtitle = SubtitleMemory.remember(pickedOff = false, pickedLanguage = option.language) ?: it.rememberedSubtitle)
                }
            }
            key == "appearance" -> sheet = Sheet.Appearance
            key == "find" -> {
                sheet = Sheet.None
                openFind()
            }
        }
    }

    private fun onAppearanceRow(key: String) {
        when {
            key.startsWith("style:") -> updateDevice { it.copy(subtitleStyle = key.removePrefix("style:")) }
            key.startsWith("background:") -> updateDevice { it.copy(subtitleBackground = key.removePrefix("background:")) }
            key == "reset-appearance" -> updateDevice { it.copy(subtitleStyle = null, subtitleBackground = null) }
        }
    }

    private fun onSettingsRow(key: String) {
        when (key) {
            "speed" -> sheet = Sheet.Speed
            "skip-back" -> sheet = Sheet.SkipBack
            "skip-forward" -> sheet = Sheet.SkipForward
            "info" -> sheet = Sheet.Info
            "reset-preferences" -> {
                updateDevice {
                    it.copy(skipBack = null, skipForward = null, autoplay = null, videoSpeed = null, subtitleStyle = null, subtitleBackground = null)
                }
                controller?.setPlaybackSpeed(PhonePlayerLogic.startingSpeed(prefs, null, audiobook = false).toFloat())
            }
        }
    }

    private fun onSkipRow(forward: Boolean, key: String) {
        val seconds = key.removePrefix("seconds:").toIntOrNull() ?: return
        updateDevice { if (forward) it.copy(skipForward = seconds) else it.copy(skipBack = seconds) }
        sheet = Sheet.Settings
    }

    private fun infoLines(): List<Pair<String, String>> {
        val size = controller?.videoSize
        return buildList {
            if (size != null && size.width > 0 && size.height > 0) add("Video" to "${size.width} × ${size.height}")
            add(TvPlayerWords.PLAYBACK_SPEED to TvPlayerLogic.speedLabel(speed.toDouble()))
            add("Source" to if (playingDownload()) "Downloaded on this phone" else "Streamed from your Library")
        }
    }

    // ---- bookmarks --------------------------------------------------------------------------------------------------

    /** Reads this title's bookmarks once. With no connection a downloaded title shows what this phone kept for it. */
    private fun loadBookmarks() {
        val work = workId ?: return
        if (bookmarksRequested) return
        bookmarksRequested = true
        val app = application as TuvimaMobileApplication
        val client = app.client
        lifecycleScope.launch {
            bookmarks = try {
                client?.bookmarks(work) ?: app.offline.bookmarksFor(work)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // No connection: show the bookmarks this phone holds for the title (an empty list when it is not a download).
                app.offline.bookmarksFor(work)
            }
            bookmarksLoaded = true
        }
    }

    /** "Bookmark at {0}": saved at the moment it was pressed, at once; on a download with no connection it waits on the phone. */
    private fun addBookmark(note: String) {
        val work = workId ?: return
        val position = positionSeconds()
        val duration = durationSeconds()
        val body = PhoneBookmarks.createBody(assetId, position, duration, note) ?: return
        val app = application as TuvimaMobileApplication
        val client = app.client
        val forAsset = assetId
        bookmarkSaveFailed = false
        lifecycleScope.launch {
            try {
                if (client == null) throw java.io.IOException("No connection")
                bookmarks = PhoneBookmarks.withAdded(bookmarks, client.createBookmark(work, body))
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // Saved on the phone when this is a download (its note is not kept); otherwise the panel says it could not save.
                if (playingDownload() && app.offline.addBookmarkOffline(forAsset, position, duration)) {
                    bookmarks = app.offline.bookmarksFor(work)
                } else {
                    bookmarkSaveFailed = true
                }
            }
        }
    }

    private fun renameBookmark(id: String, name: String, note: String) {
        val body = PhoneBookmarks.updateBody(name, note) ?: return
        val client = (application as TuvimaMobileApplication).client ?: return
        bookmarkSaveFailed = false
        lifecycleScope.launch {
            try {
                bookmarks = PhoneBookmarks.replace(bookmarks, client.updateBookmark(id, body))
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // The panel says it could not save; the list stays as it was.
                bookmarkSaveFailed = true
            }
        }
    }

    private fun deleteBookmark(id: String) {
        val app = application as TuvimaMobileApplication
        bookmarkSaveFailed = false
        if (OfflineBookmarkLogic.isWaiting(id)) {
            // Made on this phone and not sent yet: nothing exists at the Engine to delete.
            app.offline.removeWaitingBookmark(id)
            bookmarks = PhoneBookmarks.without(bookmarks, id)
            return
        }
        val client = app.client ?: return
        lifecycleScope.launch {
            try {
                if (client.deleteBookmark(id) in 200..299) bookmarks = PhoneBookmarks.without(bookmarks, id) else bookmarkSaveFailed = true
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // The panel says it could not delete; the list stays as it was.
                bookmarkSaveFailed = true
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
        workId = episode.workId?.takeIf { it.isNotBlank() }
        bookmarks = emptyList()
        bookmarksLoaded = false
        bookmarkSaveFailed = false
        bookmarksRequested = false
        chapters = emptyList()
        segments = emptyList()
        manifestDurationSeconds = null
        resetFind()
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
        // "Find subtitles…" is offered only when a subtitle source is switched on; any failed answer keeps it hidden.
        val checked = assetId
        canFindSubtitles = false
        loadSeasons()
        loadBookmarks()
        lifecycleScope.launch {
            val sources = client.textTrackSources(checked, TextTrackKind.Subtitles)
            if (assetId == checked) canFindSubtitles = FindAnotherSheet.entryVisible(sources, playingDownload())
        }
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
        // Subtitles start off, then follow what this person last chose here (or, before any choice, their own profile's preference).
        val choice = SubtitleMemory.choose(
            remembered = device.rememberedSubtitle,
            prefsFromEngine = prefsFromEngine,
            prefs = prefs,
            audio = audio.mapIndexed { i, o -> AudioTrack(i, o.language, null, o.label, o.isDefault, null) },
            subtitles = text.mapIndexed { i, o -> SubtitleTrack(i, o.language, null, o.label, o.isDefault, o.isForced, null) },
        )
        select(
            audio = choice.audioIndex?.let { audio.getOrNull(it) },
            subtitle = choice.subtitleIndex?.let { text.getOrNull(it) },
            subtitlesOff = choice.subtitleIndex == null,
        )
    }

    // ---- Find another ----------------------------------------------------------------------------------------------

    /** Opens the sheet: the video pauses behind it and comes back as it was when the sheet closes. */
    private fun openFind() {
        val api = (application as TuvimaMobileApplication).client ?: return
        if (findModel != null) return
        val active = controller
        resumeAfterFind = active?.playWhenReady ?: false
        active?.pause()
        val languages = FindAnotherLanguages.choices(
            preferred = prefs.subtitleLanguage,
            others = listOf(prefs.audioLanguage) + optionsFor(C.TRACK_TYPE_TEXT).map { it.language },
        )
        findLanguages = languages
        val model = FindAnotherModel(
            api = api.findAnotherApi(assetId, TextTrackKind.Subtitles),
            scope = lifecycleScope,
            scheduler = CoroutineDwellScheduler(lifecycleScope),
            kind = TextTrackKind.Subtitles,
            initialLanguage = FindAnotherLanguages.nameFor(prefs.subtitleLanguage),
            languageChoices = languages,
            itemNoun = if (showId != null) "episode" else "film",
            onChange = { next ->
                if (findSheetOpen) {
                    findState = next
                } else if (!next.applying) {
                    // The sheet was closed while a choice was being applied and it was refused: nothing more to wait for.
                    endFind()
                    controller?.playWhenReady = resumeAfterFind
                }
            },
            onApplied = { onFindApplied() },
            onClosed = { closeFind() },
        )
        findModel = model
        findSheetOpen = true
        findState = model.state
        model.start()
    }

    /** Cancel or a swipe down: the sheet goes and the video carries on as it was. While a choice is being applied the model is kept so the video still reloads. */
    private fun closeFind() {
        findSheetOpen = false
        findState = null
        if (findModel?.state?.applying == true) return
        endFind()
        controller?.playWhenReady = resumeAfterFind
    }

    private fun endFind() {
        findModel?.close()
        findModel = null
        findSheetOpen = false
        findState = null
    }

    /** Leaves nothing of Find another behind (another episode is starting). */
    private fun resetFind() {
        endFind()
        canFindSubtitles = false
        pendingSubtitleReselect = false
    }

    /** Use this worked: the sheet closes and the video goes on from the same place with the new subtitles. */
    private fun onFindApplied() {
        endFind()
        reloadSubtitles()
    }

    private fun reloadSubtitles() {
        val active = controller ?: return
        val current = assetId
        val args = Bundle().apply { putString(TuvimaMediaLibraryService.EXTRA_ASSET_ID, current) }
        val pending = active.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_RELOAD_SUBTITLES, Bundle.EMPTY), args)
        pending.addListener({
            if (controller !== active || assetId != current) return@addListener
            val result: SessionResult? = try {
                pending.get()
            } catch (error: Exception) {
                // The command could not be delivered; the video simply carries on as it was.
                null
            }
            if (result?.resultCode == SessionResult.RESULT_SUCCESS) {
                // The chosen file is the profile's own now: text is switched back on and the new track picked when it arrives.
                pendingSubtitleReselect = true
                active.trackSelectionParameters = active.trackSelectionParameters.buildUpon()
                    .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                    .clearOverridesOfType(C.TRACK_TYPE_TEXT)
                    .build()
            }
            active.playWhenReady = resumeAfterFind
        }, ContextCompat.getMainExecutor(this))
    }

    /** Once the reloaded video reports its text tracks, switches subtitles on and picks the file for the profile's language (audio is left alone). */
    private fun reselectSubtitlesIfPending() {
        if (!pendingSubtitleReselect) return
        val text = optionsFor(C.TRACK_TYPE_TEXT)
        if (text.isEmpty()) return
        pendingSubtitleReselect = false
        val choice = PlaybackTrackPicker.pick(
            audioTracks = emptyList(),
            subtitleTracks = text.mapIndexed { i, o -> SubtitleTrack(i, o.language, null, o.label, o.isDefault, o.isForced, null) },
            audioLanguage = prefs.audioLanguage,
            subtitleLanguage = prefs.subtitleLanguage,
            showSubtitles = ShowSubtitles.Always,
        )
        select(audio = null, subtitle = choice.subtitleIndex?.let { text.getOrNull(it) } ?: text.first(), subtitlesOff = false)
    }

    private fun applyInitialSpeed() {
        val active = controller ?: return
        if (speedApplied || !prefsLoaded || uiState != VideoUiState.Ready) return
        speedApplied = true
        // The speed this person last set on this phone, else their profile's default.
        val wanted = PhonePlayerLogic.startingSpeed(prefs, device, audiobook = false).toFloat()
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
        if (action == CloseAction.KeepPlaying) return
        val active = controller
        if (active != null) {
            active.removeListener(playerListener)
            when (action) {
                CloseAction.KeepPlaying -> Unit
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
        // Bumped on every touch of a control so the 3 second hide timer starts over.
        var touch by remember { mutableIntStateOf(0) }
        var positionMs by remember { mutableLongStateOf(0L) }
        var durationMs by remember { mutableLongStateOf(0L) }
        var dragging by remember { mutableStateOf<Float?>(null) }
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
        val accessibilityManager = remember { getSystemService(AccessibilityManager::class.java) }
        LaunchedEffect(controlsVisible, isPlaying, touch, sheet) {
            // Controls that hide themselves would trap TalkBack and switch access, so they stay while those are on.
            val mayHide = VideoAccessibility.controlsMayAutoHide(accessibilityManager?.isTouchExplorationEnabled == true)
            if (mayHide && controlsVisible && isPlaying && sheet == Sheet.None) {
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
        val neighbours = PhonePlayerLogic.videoNeighbours(seasons, assetId)
        val autoplay = PhonePlayerLogic.autoplayOn(prefs, device)
        val nextOffer = if (next == null) NextEpisodeOffer.None else NextEpisodeLogic.nextEpisodeOffer(
            positionSeconds = positionMs / 1000.0,
            durationSeconds = durationSeconds,
            segments = segments,
            autoplay = autoplay,
            ended = finished,
            cancelled = cancelledFor == assetId,
        )
        androidx.compose.runtime.SideEffect { countdownShowing = nextOffer == NextEpisodeOffer.Countdown }
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
                    // The look this person chose in Subtitle appearance on this phone, else what their profile implies.
                    val appearance = TvSubtitleAppearance.of(
                        prefs,
                        TvDeviceChoices(subtitleStyle = device.subtitleStyle, subtitleBackground = device.subtitleBackground),
                    )
                    val contrast = appearance.style == TvPlayerWords.STYLE_CONTRAST
                    val boxed = appearance.background == TvPlayerWords.BACKGROUND_BOX
                    val systemCaptions = view.context.getSystemService(CaptioningManager::class.java)
                    if (systemCaptions?.isEnabled == true) {
                        // The system's own caption style and size win over the profile's when the person turned them on.
                        view.subtitleView?.setUserDefaultStyle()
                        view.subtitleView?.setUserDefaultTextSize()
                        return@AndroidView
                    }
                    view.subtitleView?.apply {
                        setFractionalTextSize(TvSubtitleAppearance.sizeFraction(appearance, prefs.subtitleSize))
                        setStyle(
                            CaptionStyleCompat(
                                AndroidColor.WHITE,
                                when {
                                    contrast -> AndroidColor.BLACK
                                    boxed -> 0x99000000.toInt()
                                    else -> AndroidColor.TRANSPARENT
                                },
                                AndroidColor.TRANSPARENT,
                                when {
                                    boxed -> CaptionStyleCompat.EDGE_TYPE_NONE
                                    appearance.background == TvPlayerWords.BACKGROUND_SHADOW -> CaptionStyleCompat.EDGE_TYPE_DROP_SHADOW
                                    else -> CaptionStyleCompat.EDGE_TYPE_OUTLINE
                                },
                                AndroidColor.BLACK,
                                null,
                            ),
                        )
                    }
                },
            )
            if (!inPip) {
            // One tap shows or hides the controls; a double tap on a half seeks; a vertical slide changes brightness or volume.
            Box(
                Modifier.fillMaxSize()
                    .pointerInput(skip) {
                        detectTapGestures(
                            onTap = { controlsVisible = !controlsVisible },
                            onDoubleTap = { offset ->
                                val delta = VideoGestures.doubleTapSeekMs(
                                    offset.x, size.width.toFloat(), skip.back, skip.forward,
                                )
                                seekBy(delta, durationMs)
                                showHint(if (delta < 0) "-${skip.back} s" else "+${skip.forward} s")
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
                    } else if (!isPlaying && !buffering && !finished && sheet == Sheet.None) {
                        PausedDetails(durationSeconds, raised = controlsVisible)
                    }
                    if (controlsVisible) {
                        val previousEpisode = neighbours.previous
                        val nextForButton = next ?: neighbours.next?.let(::toNextEpisode)
                        Controls(
                            positionMs = positionMs,
                            durationMs = durationMs,
                            dragging = dragging,
                            ticks = PhonePlayerLogic.timelineTicks(bookmarks, durationSeconds, device.showBookmarksOnTimeline),
                            transport = PhonePlayerLogic.transport(
                                kind, skip, isPlaying, canPrevious = previousEpisode != null, canNext = nextForButton != null,
                            ),
                            tools = PhonePlayerLogic.tools(
                                kind,
                                hasEpisodes = seasons.isNotEmpty(),
                                canBookmark = PhoneBookmarks.canBookmark(workId, isMusic = false),
                            ),
                            menu = PhonePlayerLogic.menu(
                                kind,
                                canPictureInPicture = packageManager.hasSystemFeature(PackageManager.FEATURE_PICTURE_IN_PICTURE),
                            ),
                            onDrag = { dragging = it; touch += 1 },
                            onDragEnd = {
                                dragging?.let { active?.seekTo(it.toLong()) }
                                dragging = null
                            },
                            onControl = { key ->
                                when (key) {
                                    "play-pause" -> active?.let { if (it.isPlaying) it.pause() else it.play() }
                                    "skip-back" -> seekBy(-skip.back * 1000L, durationMs)
                                    "skip-forward" -> seekBy(skip.forward * 1000L, durationMs)
                                    "previous" -> previousEpisode?.let { playEpisode(toNextEpisode(it), previousEnded = false) }
                                    "next" -> nextForButton?.let { playEpisode(it, previousEnded = false) }
                                }
                                touch += 1
                            },
                            onTool = { key ->
                                if (key == "episodes") seasonShown = TvEpisodes.seasonIndexOf(seasons, assetId)
                                sheet = when (key) {
                                    "episodes" -> Sheet.Episodes
                                    "audio-subtitles" -> Sheet.AudioSubtitles
                                    "bookmarks" -> Sheet.Bookmarks
                                    else -> Sheet.None
                                }
                                touch += 1
                            },
                            onMenu = { key ->
                                when (key) {
                                    "playback-settings" -> sheet = Sheet.Settings
                                    "picture-in-picture" -> enterPip()
                                }
                                touch += 1
                            },
                        )
                    }
                }
            }
            val sheetClient = (application as TuvimaMobileApplication).client
            when (sheet) {
                Sheet.None -> Unit
                Sheet.Episodes -> PanelSheet(
                    client = sheetClient,
                    panel = TvPanels.episodes(
                        seasons, seasonShown, kind == TvMediaKind.Lesson,
                        TvPlayingNow(assetId, isPlaying, positionMs / 1000.0, durationSeconds ?: 0.0, next?.assetId),
                        autoplay,
                    ),
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.None },
                    onRow = { key, _ -> onEpisodesRow(key) },
                    onSwitch = { key -> onEpisodesSwitch(key, autoplay) },
                )
                Sheet.AudioSubtitles -> {
                    val audioOptions = optionsFor(C.TRACK_TYPE_AUDIO)
                    val textOptions = optionsFor(C.TRACK_TYPE_TEXT)
                    val built = TvPanels.audioSubtitles(
                        audio = audioOptions.mapIndexed { i, o -> TvAudioTrack(i.toString(), o.label, selected = o.selected) },
                        subtitles = textOptions.mapIndexed { i, o ->
                            TvSubtitleTrack(i.toString(), o.label, selected = !textDisabled && o.selected, forced = o.isForced)
                        },
                        canFind = canFindSubtitles,
                    )
                    // A downloaded copy keeps its first video and audio only (no sidecar or embedded subtitles): say so
                    // rather than offer a list that cannot work.
                    val shown = if (textOptions.isEmpty() && playingDownload()) built.copy(body = built.body + TvNote("Not included in this download.")) else built
                    PanelSheet(
                        client = sheetClient,
                        panel = shown,
                        onClose = { sheet = Sheet.None },
                        onBack = { sheet = Sheet.None },
                        onRow = { key, _ -> onAudioSubtitlesRow(key, audioOptions, textOptions) },
                    )
                }
                Sheet.Appearance -> {
                    val look = TvSubtitleAppearance.of(
                        prefs,
                        TvDeviceChoices(subtitleStyle = device.subtitleStyle, subtitleBackground = device.subtitleBackground),
                    )
                    PanelSheet(
                        client = sheetClient,
                        panel = TvPanels.subtitleAppearance(look.style, look.background),
                        onClose = { sheet = Sheet.None },
                        onBack = { sheet = Sheet.AudioSubtitles },
                        onRow = { key, _ -> onAppearanceRow(key) },
                    )
                }
                Sheet.Settings -> PanelSheet(
                    client = sheetClient,
                    panel = TvPanels.playbackSettings(speed.toDouble(), skip),
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.None },
                    onRow = { key, _ -> onSettingsRow(key) },
                )
                Sheet.Speed -> SpeedSheet(
                    speed = speed.toDouble(),
                    onSet = { setSpeed(it) },
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.Settings },
                )
                Sheet.SkipBack -> PanelSheet(
                    client = sheetClient,
                    panel = TvPanels.skipChoices(forward = false, current = skip.back),
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.Settings },
                    onRow = { key, _ -> onSkipRow(forward = false, key = key) },
                )
                Sheet.SkipForward -> PanelSheet(
                    client = sheetClient,
                    panel = TvPanels.skipChoices(forward = true, current = skip.forward),
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.Settings },
                    onRow = { key, _ -> onSkipRow(forward = true, key = key) },
                )
                Sheet.Info -> PanelSheet(
                    client = sheetClient,
                    panel = TvPanels.playbackInformation(infoLines()),
                    onClose = { sheet = Sheet.None },
                    onBack = { sheet = Sheet.Settings },
                    onRow = { _, _ -> },
                )
                Sheet.Bookmarks -> BookmarksSheet(
                    client = sheetClient,
                    items = bookmarks,
                    loaded = bookmarksLoaded,
                    saveFailed = bookmarkSaveFailed,
                    positionSeconds = positionMs / 1000.0,
                    showOnTimeline = device.showBookmarksOnTimeline,
                    coverUrl = null,
                    onAdd = { note -> addBookmark(note) },
                    onSeek = { seconds -> seekToSeconds(seconds) },
                    onEdit = { id, name, note -> renameBookmark(id, name, note) },
                    onDelete = { id -> deleteBookmark(id) },
                    onToggleTimeline = { on -> updateDevice { it.copy(showBookmarksOnTimeline = on) } },
                    onClose = { sheet = Sheet.None },
                )
            }
            findState?.let { state ->
                FindAnotherBottomSheet(
                    state = state,
                    itemName = title,
                    languageChoices = findLanguages,
                    onTapRow = { findModel?.tapRow(it) },
                    onUseThis = { findModel?.useSelected() },
                    onCancel = { findModel?.cancel() },
                    onChooseLanguage = { findModel?.chooseLanguage(it) },
                    onDismiss = { closeFind() },
                )
            }
            }
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

    /** The one line under the title: "S1 E1 · The First Wish · 22 min" for an episode, "22 min" for a film (the year is not on this screen's answer). */
    private fun detailLine(durationSeconds: Double?): String {
        val episode = TvEpisodes.find(seasons, assetId)
        if (episode == null && kind == TvMediaKind.Episode) {
            // The show's episode list has not been read (or is not reachable): the label the screen was opened with, then the length.
            return listOfNotNull(subtitle?.takeIf { it.isNotBlank() }, TvPlayerLogic.lengthText(durationSeconds).takeIf { it.isNotEmpty() })
                .joinToString(TvPlayerWords.SEP)
        }
        return PhonePlayerLogic.detailLine(kind, episode, year = null, durationSeconds = durationSeconds)
    }

    /**
     * The phone's video overlay: a top line (close, title and the one line, the "..." menu), the five transport buttons of the contract, one
     * row of tools and the timeline with a tick for each bookmark when the person wants them shown.
     */
    @Composable
    private fun Controls(
        positionMs: Long,
        durationMs: Long,
        dragging: Float?,
        ticks: List<Double>,
        transport: List<TvControl>,
        tools: List<PhoneTool>,
        menu: List<PhoneMenuRow>,
        onDrag: (Float) -> Unit,
        onDragEnd: () -> Unit,
        onControl: (String) -> Unit,
        onTool: (String) -> Unit,
        onMenu: (String) -> Unit,
    ) {
        // 70% black behind white text keeps at least 4.5:1 contrast over any picture.
        val scrim = Color(0xB3000000)
        var menuOpen by remember { mutableStateOf(false) }
        Box(Modifier.fillMaxSize().safeDrawingPadding()) {
            Row(
                Modifier.fillMaxWidth().background(scrim).align(Alignment.TopStart).padding(horizontal = 8.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                IconButton(
                    onClick = { finish() },
                    modifier = Modifier.size(48.dp).semantics { contentDescription = "Close video" },
                ) { Text("✕", color = Color.White, fontSize = 20.sp) }
                Column(Modifier.weight(1f).padding(start = 8.dp)) {
                    Text(
                        showTitle ?: title.orEmpty(),
                        color = Color.White,
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis,
                    )
                    val line = detailLine(if (durationMs > 0) durationMs / 1000.0 else manifestDurationSeconds)
                    if (line.isNotBlank()) {
                        Text(line, color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    }
                }
                if (menu.isNotEmpty()) {
                    Box {
                        IconButton(
                            onClick = { menuOpen = true },
                            modifier = Modifier.size(48.dp).semantics { contentDescription = "More options" },
                        ) { Text("⋯", color = Color.White, fontSize = 22.sp) }
                        DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                            menu.forEach { row ->
                                DropdownMenuItem(
                                    text = { Text(row.label) },
                                    onClick = {
                                        menuOpen = false
                                        onMenu(row.key)
                                    },
                                )
                            }
                        }
                    }
                }
            }
            Row(
                Modifier.align(Alignment.Center),
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                transport.forEach { control -> TransportButton(control, scrim, onControl) }
            }
            Column(Modifier.fillMaxWidth().background(scrim).align(Alignment.BottomStart).padding(horizontal = 16.dp, vertical = 8.dp)) {
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
                    tools.forEach { tool ->
                        TextButton(
                            onClick = { onTool(tool.key) },
                            modifier = Modifier.weight(1f).sizeMinimum().semantics { contentDescription = tool.label },
                        ) { Text(tool.label, color = Color.White, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis) }
                    }
                }
                val shown = dragging ?: positionMs.toFloat()
                Slider(
                    value = shown.coerceIn(0f, durationMs.coerceAtLeast(1L).toFloat()),
                    onValueChange = onDrag,
                    onValueChangeFinished = onDragEnd,
                    valueRange = 0f..durationMs.coerceAtLeast(1L).toFloat(),
                    enabled = durationMs > 0,
                    colors = SliderDefaults.colors(thumbColor = Color.White, activeTrackColor = Color.White),
                    modifier = Modifier.fillMaxWidth().semantics {
                        contentDescription = "Seek bar"
                        stateDescription = VideoAccessibility.positionAnnouncement(shown.toLong(), durationMs)
                        customActions = listOf(
                            CustomAccessibilityAction("Back ${skip.back} seconds") { onControl("skip-back"); true },
                            CustomAccessibilityAction("Forward ${skip.forward} seconds") { onControl("skip-forward"); true },
                        )
                    },
                )
                TickRow(ticks)
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(ClockText.format(shown.toLong()), color = Color.White, style = MaterialTheme.typography.bodySmall)
                    Text(ClockText.format(durationMs), color = Color.White, style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }

    /** A small tick under the timeline for each bookmark, where [ticks] are percentages of the length. */
    @Composable
    private fun TickRow(ticks: List<Double>) {
        if (ticks.isEmpty()) return
        BoxWithConstraints(Modifier.fillMaxWidth().height(6.dp)) {
            val total = maxWidth
            ticks.forEach { percent ->
                Box(Modifier.offset(x = total * (percent / 100.0).toFloat()).width(3.dp).height(6.dp).background(Color(0xFFA46FFF)))
            }
        }
    }

    /** One button of the five-button transport. A button that cannot act now is dimmed in place and reads as unavailable. */
    @Composable
    private fun TransportButton(control: TvControl, scrim: Color, onClick: (String) -> Unit) {
        val isPlay = control.key == "play-pause"
        val tint = Color.White.copy(alpha = if (control.dimmed) 0.35f else 1f)
        val glyph = when (control.key) {
            "play-pause" -> if (control.label == TvPlayerWords.PAUSE) "❚❚" else "▶"
            "previous" -> "⏮"
            "next" -> "⏭"
            "skip-back" -> "-${control.seconds ?: skip.back}"
            "skip-forward" -> "+${control.seconds ?: skip.forward}"
            else -> control.label
        }
        IconButton(
            onClick = { if (!control.dimmed) onClick(control.key) },
            modifier = Modifier.size(if (isPlay) 72.dp else 52.dp)
                .then(if (isPlay) Modifier.background(scrim, CircleShape) else Modifier)
                .semantics {
                    contentDescription = control.label
                    if (control.dimmed) disabled()
                },
        ) { Text(glyph, color = tint, fontSize = if (isPlay) 26.sp else 18.sp) }
    }

    /**
     * While the video is paused its details sit at the bottom left, above the controls, on a local gradient: the title, the one line, and a
     * short description for an episode. They hide while a panel is open (the caller) and come back when it closes.
     */
    @Composable
    private fun PausedDetails(durationSeconds: Double?, raised: Boolean) {
        val description = if (kind == TvMediaKind.Episode) TvEpisodes.find(seasons, assetId)?.description.orEmpty() else ""
        val line = detailLine(durationSeconds)
        Box(Modifier.fillMaxSize().safeDrawingPadding()) {
            Column(
                Modifier.align(Alignment.BottomStart).fillMaxWidth(0.6f)
                    .padding(start = 16.dp, bottom = if (raised) 150.dp else 24.dp)
                    .background(
                        Brush.horizontalGradient(listOf(Color(0xCC000000), Color.Transparent)),
                        androidx.compose.foundation.shape.RoundedCornerShape(12.dp),
                    )
                    .padding(12.dp),
                verticalArrangement = Arrangement.spacedBy(4.dp),
            ) {
                Text(showTitle ?: title.orEmpty(), color = Color.White, style = MaterialTheme.typography.titleMedium, maxLines = 2, overflow = TextOverflow.Ellipsis)
                if (line.isNotBlank()) Text(line, color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.bodyMedium, maxLines = 1)
                if (description.isNotBlank()) {
                    Text(description, color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.bodySmall, maxLines = 3, overflow = TextOverflow.Ellipsis)
                }
            }
        }
    }

    /**
     * The end card, in the contract's words: "Up next", with autoplay on "Starts in {0} seconds", the next item's still and title with
     * "Next episode" under it, and the buttons Cancel and Play now. With autoplay off, or after Cancel, the card stays and has no countdown.
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
        val view = PhonePlayerLogic.endCard(NextEpisodeLogic.cardLine(next), kind, if (countdown) NextEpisodeLogic.secondsLeft(elapsed) else null)
        val scrim = Color(0xCC000000)
        val imageClient = (application as TuvimaMobileApplication).client
        // Announced once and politely when the card appears; the countdown number changes are not read out second by second.
        val announcement = VideoAccessibility.nextEpisodeAnnouncement(NextEpisodeLogic.cardLine(next), if (countdown) NextEpisodeLogic.COUNTDOWN_SECONDS else null)
        Box(Modifier.fillMaxSize().safeDrawingPadding()) {
            Column(
                Modifier.align(Alignment.BottomEnd).padding(end = 16.dp, bottom = if (raised) 150.dp else 24.dp)
                    .widthIn(max = 320.dp)
                    .background(scrim, androidx.compose.foundation.shape.RoundedCornerShape(12.dp)).padding(16.dp)
                    .semantics { liveRegion = LiveRegionMode.Polite; contentDescription = announcement },
                verticalArrangement = Arrangement.spacedBy(8.dp),
            ) {
                Text(view.heading, color = Color.White, style = MaterialTheme.typography.labelMedium)
                if (view.countdown.isNotEmpty()) Text(view.countdown, color = Color.White.copy(alpha = 0.8f), style = MaterialTheme.typography.bodySmall)
                Row(horizontalArrangement = Arrangement.spacedBy(12.dp), verticalAlignment = Alignment.CenterVertically) {
                    if (imageClient != null) {
                        RemoteImage(imageClient, next.stillUrl, Modifier.width(96.dp).height(54.dp).clip(androidx.compose.foundation.shape.RoundedCornerShape(6.dp)))
                    }
                    Column {
                        Text(view.title, color = Color.White, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        Text(view.kind, color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.labelMedium)
                    }
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    if (countdown) OutlinedButton(onClick = onCancel, modifier = Modifier.sizeMinimum()) { Text(view.cancel) }
                    Button(onClick = onPlay, modifier = Modifier.sizeMinimum()) { Text(view.playNow) }
                }
            }
        }
    }

    companion object {
        private const val CONTROLS_HIDE_MS = 3_000L
        private const val HINT_MS = 800L
        private const val PREFS_WAIT_MS = 2_000L
        private const val ACTION_PIP_TOGGLE = "com.tuvima.library.PIP_TOGGLE"
        private const val NEXT_FETCH_AFTER_MS = 30_000L
        private const val NEXT_EPISODES_COUNT = 10
        private const val EXTRA_SHOW_ID = "show_id"
        private const val EXTRA_WORK_ID = "work_id"

        fun intent(context: Context, assetId: String, title: String?, subtitle: String?, showId: String? = null, workId: String? = null): Intent =
            Intent(context, VideoPlayerActivity::class.java)
                .putExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId)
                .putExtra(TuvimaMediaLibraryService.EXTRA_TITLE, title)
                .putExtra(TuvimaMediaLibraryService.EXTRA_SUBTITLE, subtitle)
                .putExtra(EXTRA_SHOW_ID, showId)
                .putExtra(EXTRA_WORK_ID, workId)
    }
}

/** Buttons on the video screen are at least 48dp tall and wide. */
private fun Modifier.sizeMinimum(): Modifier = defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
