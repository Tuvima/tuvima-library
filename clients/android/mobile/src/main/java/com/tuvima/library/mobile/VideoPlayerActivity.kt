package com.tuvima.library.mobile

import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color as AndroidColor
import android.graphics.drawable.ColorDrawable
import android.os.Bundle
import android.view.ViewGroup
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.darkColorScheme
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
import androidx.media3.common.C
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import androidx.media3.session.SessionToken
import androidx.media3.ui.PlayerView
import com.google.common.util.concurrent.ListenableFuture
import com.tuvima.library.core.ClockText
import com.tuvima.library.core.OrientationPolicy
import com.tuvima.library.core.OrientationRule
import com.tuvima.library.core.ScreenKind
import com.tuvima.library.core.VideoFailure
import com.tuvima.library.core.VideoMessages
import kotlinx.coroutines.delay

/** What the video screen is showing over the picture. */
private sealed interface VideoUiState {
    /** Waiting for the player, or for the Engine to prepare a stream. */
    data object Connecting : VideoUiState
    data object Ready : VideoUiState
    data class Failed(val message: String, val canRetry: Boolean) : VideoUiState
}

/**
 * Full-screen movie and episode player. It shows the same ExoPlayer the media service owns, through a media controller,
 * so the lock screen, headset buttons, progress code and offline replay stay in one place. Landscape comes from
 * [OrientationPolicy]; rotating never restarts playback because the activity handles those configuration changes itself.
 * The main screen stays locked to portrait, so closing this returns to portrait without any code here.
 */
class VideoPlayerActivity : ComponentActivity() {
    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller by mutableStateOf<MediaController?>(null)
    private var uiState by mutableStateOf<VideoUiState>(VideoUiState.Connecting)
    private var isPlaying by mutableStateOf(false)
    private var buffering by mutableStateOf(false)
    private var assetId = ""
    private var title: String? = null
    private var subtitle: String? = null

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            this@VideoPlayerActivity.isPlaying = isPlaying
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            buffering = playbackState == Player.STATE_BUFFERING
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
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                VideoScreen()
            }
        }
    }

    override fun onStart() {
        super.onStart()
        connect()
    }

    override fun onStop() {
        releaseController()
        super.onStop()
    }

    override fun onDestroy() {
        releaseController()
        super.onDestroy()
    }

    private fun connect() {
        if (controllerFuture != null || assetId.isBlank()) return
        uiState = VideoUiState.Connecting
        val token = SessionToken(this, ComponentName(this, TuvimaMediaLibraryService::class.java))
        val future = MediaController.Builder(this, token).setListener(disconnectListener).buildAsync()
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
            if (connected.currentMediaItem?.mediaId == assetId && connected.playbackState != Player.STATE_IDLE) {
                // Coming back to a video that is already loaded: show it paused where it was.
                uiState = VideoUiState.Ready
            } else {
                requestPlay()
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun requestPlay() {
        val active = controller
        if (active == null) {
            controllerFuture = null
            connect()
            return
        }
        uiState = VideoUiState.Connecting
        val args = Bundle().apply {
            putString(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId)
            putString(TuvimaMediaLibraryService.EXTRA_TITLE, title)
            putString(TuvimaMediaLibraryService.EXTRA_SUBTITLE, subtitle)
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
            } else if (!extras.getBoolean(TuvimaMediaLibraryService.RESULT_CANCELLED)) {
                val failure = runCatching {
                    VideoFailure.valueOf(extras.getString(TuvimaMediaLibraryService.RESULT_FAILURE).orEmpty())
                }.getOrDefault(VideoFailure.Other)
                uiState = VideoUiState.Failed(
                    extras.getString(TuvimaMediaLibraryService.RESULT_MESSAGE) ?: VideoMessages.forFailure(failure),
                    canRetry = failure != VideoFailure.Unavailable,
                )
            }
        }, ContextCompat.getMainExecutor(this))
    }

    private fun cancelAndClose() {
        controller?.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_CANCEL_VIDEO, Bundle.EMPTY), Bundle.EMPTY)
        finish()
    }

    /** Pauses (no background audio for video), stops any wait for the Engine, and lets go of the controller. */
    private fun releaseController() {
        val active = controller
        if (active != null) {
            if (uiState == VideoUiState.Connecting) {
                active.sendCustomCommand(SessionCommand(TuvimaMediaLibraryService.COMMAND_CANCEL_VIDEO, Bundle.EMPTY), Bundle.EMPTY)
            }
            active.removeListener(playerListener)
            active.pause()
        }
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controllerFuture = null
        controller = null
        isPlaying = false
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

        LaunchedEffect(active) {
            while (true) {
                if (active != null && dragging == null) {
                    positionMs = active.currentPosition.coerceAtLeast(0L)
                    durationMs = active.duration.takeIf { it > 0 && it != C.TIME_UNSET } ?: 0L
                }
                delay(500)
            }
        }
        LaunchedEffect(controlsVisible, isPlaying, touch) {
            if (controlsVisible && isPlaying) {
                delay(CONTROLS_HIDE_MS)
                controlsVisible = false
            }
        }

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
                update = { it.player = active },
            )
            // A tap anywhere on the picture shows or hides the controls.
            Box(
                Modifier.fillMaxSize().clickable(
                    interactionSource = remember { MutableInteractionSource() },
                    indication = null,
                ) { controlsVisible = !controlsVisible },
            )
            when (val state = uiState) {
                VideoUiState.Connecting -> ConnectingPanel()
                is VideoUiState.Failed -> FailedPanel(state)
                VideoUiState.Ready -> {
                    if (buffering) {
                        CircularProgressIndicator(Modifier.align(Alignment.Center).size(48.dp), color = Color.White)
                    }
                    if (controlsVisible) {
                        Controls(
                            positionMs = positionMs,
                            durationMs = durationMs,
                            dragging = dragging,
                            onDrag = { dragging = it; touch += 1 },
                            onDragEnd = {
                                dragging?.let { active?.seekTo(it.toLong()) }
                                dragging = null
                            },
                            onSeekBy = { delta ->
                                active?.let {
                                    val limit = if (durationMs > 0) durationMs else Long.MAX_VALUE
                                    it.seekTo((it.currentPosition + delta).coerceIn(0L, limit))
                                }
                                touch += 1
                            },
                            onTogglePlay = {
                                active?.let { if (it.isPlaying) it.pause() else it.play() }
                                touch += 1
                            },
                        )
                    }
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

    @Composable
    private fun Controls(
        positionMs: Long,
        durationMs: Long,
        dragging: Float?,
        onDrag: (Float) -> Unit,
        onDragEnd: () -> Unit,
        onSeekBy: (Long) -> Unit,
        onTogglePlay: () -> Unit,
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
                    onClick = { onSeekBy(-SKIP_MS) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Back 10 seconds" },
                ) { Text("-10", color = Color.White, fontSize = 18.sp) }
                IconButton(
                    onClick = onTogglePlay,
                    modifier = Modifier.size(72.dp).background(scrim, androidx.compose.foundation.shape.CircleShape)
                        .semantics { contentDescription = if (isPlaying) "Pause" else "Play" },
                ) { Text(if (isPlaying) "❚❚" else "▶", color = Color.White, fontSize = 26.sp) }
                IconButton(
                    onClick = { onSeekBy(SKIP_MS) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Forward 10 seconds" },
                ) { Text("+10", color = Color.White, fontSize = 18.sp) }
            }
            Column(Modifier.fillMaxWidth().background(scrim).align(Alignment.BottomStart).padding(horizontal = 16.dp, vertical = 8.dp)) {
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

    companion object {
        private const val CONTROLS_HIDE_MS = 3_000L
        private const val SKIP_MS = 10_000L

        fun intent(context: Context, assetId: String, title: String?, subtitle: String?): Intent =
            Intent(context, VideoPlayerActivity::class.java)
                .putExtra(TuvimaMediaLibraryService.EXTRA_ASSET_ID, assetId)
                .putExtra(TuvimaMediaLibraryService.EXTRA_TITLE, title)
                .putExtra(TuvimaMediaLibraryService.EXTRA_SUBTITLE, subtitle)
    }
}

/** Buttons on the video screen are at least 48dp tall and wide. */
private fun Modifier.sizeMinimum(): Modifier = defaultMinSize(minWidth = 48.dp, minHeight = 48.dp)
