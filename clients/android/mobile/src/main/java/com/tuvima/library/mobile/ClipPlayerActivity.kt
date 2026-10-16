package com.tuvima.library.mobile

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
import androidx.annotation.OptIn
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectTapGestures
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
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
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
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.tuvima.library.core.ClipFailure
import com.tuvima.library.core.ClipMessages
import com.tuvima.library.core.ClipPlayback
import com.tuvima.library.core.ClockText
import com.tuvima.library.core.OrientationPolicy
import com.tuvima.library.core.OrientationRule
import com.tuvima.library.core.ScreenKind
import com.tuvima.library.core.VideoGestures
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking

/** What the clip screen shows over the picture. */
private sealed interface ClipUiState {
    data object Connecting : ClipUiState
    data object Ready : ClipUiState
    data class Failed(val failure: ClipFailure) : ClipUiState
}

/**
 * Plays a personal video from Photos. It is deliberately separate from the movie and episode player: it owns its own
 * player (no media service, notification, lock-screen session, headset buttons or background audio), sends no
 * heartbeats and writes no progress or History, and has no next episode, chapters or skip buttons.
 *
 * The stream is the original of a View item, read through byte ranges. The bearer token is added to every request when
 * the request is made (a fresh token each time), never put in the URL. The screen follows the phone's rotation
 * ([OrientationPolicy] VideoClip), pauses when it leaves the screen, and releases the player when it is destroyed.
 */
@OptIn(UnstableApi::class)
class ClipPlayerActivity : ComponentActivity() {
    private var player: ExoPlayer? = null
    private var playerState by mutableStateOf<Player?>(null)
    private var uiState by mutableStateOf<ClipUiState>(ClipUiState.Connecting)
    private var isPlaying by mutableStateOf(false)
    private var buffering by mutableStateOf(false)
    private var speed by mutableStateOf(1f)
    private var itemId = ""
    private var title: String? = null

    private val playerListener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            this@ClipPlayerActivity.isPlaying = isPlaying
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            buffering = playbackState == Player.STATE_BUFFERING
            if (playbackState == Player.STATE_READY && uiState is ClipUiState.Connecting) uiState = ClipUiState.Ready
        }

        override fun onPlayerError(error: PlaybackException) {
            val offline = error.errorCode == PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_FAILED ||
                error.errorCode == PlaybackException.ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT
            val missing = error.errorCode == PlaybackException.ERROR_CODE_IO_FILE_NOT_FOUND ||
                error.errorCode == PlaybackException.ERROR_CODE_IO_BAD_HTTP_STATUS
            uiState = ClipUiState.Failed(
                when {
                    offline -> ClipFailure(ClipMessages.OFFLINE, canRetry = true)
                    missing -> ClipFailure(ClipMessages.MISSING, canRetry = false)
                    else -> ClipFailure(ClipMessages.OTHER, canRetry = true)
                },
            )
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        itemId = intent.getStringExtra(EXTRA_ITEM_ID).orEmpty()
        title = intent.getStringExtra(EXTRA_TITLE)
        if (itemId.isBlank()) {
            finish()
            return
        }
        requestedOrientation = when (OrientationPolicy.ruleFor(ScreenKind.VideoClip)) {
            OrientationRule.SensorLandscape -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            OrientationRule.Sensor -> ActivityInfo.SCREEN_ORIENTATION_SENSOR
            OrientationRule.Portrait -> ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        window.setBackgroundDrawable(ColorDrawable(AndroidColor.BLACK))
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        WindowInsetsControllerCompat(window, window.decorView).apply {
            systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            hide(WindowInsetsCompat.Type.systemBars())
        }
        setContent {
            MaterialTheme(colorScheme = darkColorScheme()) {
                ClipScreen()
            }
        }
        load()
    }

    override fun onStop() {
        // Never plays in the background: leaving the screen pauses it.
        player?.pause()
        super.onStop()
    }

    override fun onDestroy() {
        releasePlayer()
        super.onDestroy()
    }

    private fun releasePlayer() {
        player?.removeListener(playerListener)
        player?.release()
        player = null
        playerState = null
        isPlaying = false
    }

    /** Asks the Engine for the clip's manifest, then plays its direct stream. Every failure becomes a message, never a black screen. */
    private fun load() {
        releasePlayer()
        uiState = ClipUiState.Connecting
        lifecycleScope.launch {
            val client = (application as TuvimaMobileApplication).client
            if (client == null) {
                uiState = ClipUiState.Failed(ClipFailure(ClipMessages.OTHER, canRetry = false))
                return@launch
            }
            val url = try {
                ClipPlayback.streamUrl(client.viewItemPlayback(itemId))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (error: Exception) {
                uiState = ClipUiState.Failed(ClipPlayback.failureFor(error))
                return@launch
            }
            if (url == null) {
                uiState = ClipUiState.Failed(ClipFailure(ClipMessages.MISSING, canRetry = false))
                return@launch
            }
            // The token goes on each request as it is made, never into the URL, and is refreshed when it is about to expire.
            val http = DefaultHttpDataSource.Factory()
            val authorised = ResolvingDataSource.Factory(DefaultDataSource.Factory(this@ClipPlayerActivity, http)) { dataSpec ->
                val token = runBlocking { client.streamBearerToken() }
                dataSpec.withAdditionalHeaders(mapOf("Authorization" to "Bearer $token"))
            }
            val created = ExoPlayer.Builder(this@ClipPlayerActivity)
                .setMediaSourceFactory(DefaultMediaSourceFactory(this@ClipPlayerActivity).setDataSourceFactory(authorised))
                .build()
            created.addListener(playerListener)
            created.setMediaItem(MediaItem.fromUri(url))
            created.prepare()
            created.playWhenReady = true
            player = created
            playerState = created
        }
    }

    @Composable
    private fun ClipScreen() {
        val active = playerState
        var controlsVisible by remember { mutableStateOf(true) }
        var positionMs by remember { mutableLongStateOf(0L) }
        var durationMs by remember { mutableLongStateOf(0L) }
        var dragging by remember { mutableStateOf<Float?>(null) }
        var touch by remember { mutableLongStateOf(0L) }

        LaunchedEffect(active) {
            while (true) {
                if (active != null && dragging == null) {
                    positionMs = active.currentPosition.coerceAtLeast(0L)
                    durationMs = active.duration.takeIf { it > 0 && it != C.TIME_UNSET } ?: 0L
                }
                delay(500)
            }
        }
        // Controls hide a few seconds after the last touch while it plays.
        LaunchedEffect(touch, isPlaying) {
            if (isPlaying) {
                delay(3000)
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
                update = { view -> view.player = active },
            )
            Box(
                Modifier.fillMaxSize().pointerInput(active) {
                    detectTapGestures(
                        onTap = {
                            controlsVisible = !controlsVisible
                            touch += 1
                        },
                        onDoubleTap = { offset ->
                            val deltaMs = VideoGestures.doubleTapSeekMs(offset.x, size.width.toFloat(), SKIP_SECONDS, SKIP_SECONDS)
                            seekBy(deltaMs)
                        },
                    )
                },
            )
            when (val state = uiState) {
                ClipUiState.Connecting -> CircularProgressIndicator(Modifier.align(Alignment.Center), color = Color.White)
                is ClipUiState.Failed -> FailedPanel(state.failure)
                ClipUiState.Ready -> {
                    if (buffering) CircularProgressIndicator(Modifier.align(Alignment.Center), color = Color.White)
                    if (controlsVisible) {
                        Controls(
                            positionMs = positionMs,
                            durationMs = durationMs,
                            dragging = dragging,
                            onDrag = { dragging = it; touch += 1 },
                            onDragEnd = {
                                dragging?.let { player?.seekTo(it.toLong()) }
                                dragging = null
                                touch += 1
                            },
                            onSeekBy = { delta -> seekBy(delta); touch += 1 },
                            onTogglePlay = {
                                player?.let { if (it.isPlaying) it.pause() else it.play() }
                                touch += 1
                            },
                            onSpeed = {
                                val next = ClipPlayback.nextSpeed(speed)
                                player?.setPlaybackSpeed(next)
                                speed = next
                                touch += 1
                            },
                        )
                    }
                }
            }
            if (uiState !is ClipUiState.Ready || !controlsVisible) CloseButton(Modifier.align(Alignment.TopStart))
        }
    }

    private fun seekBy(deltaMs: Long) {
        val active = player ?: return
        val duration = active.duration.takeIf { it > 0 && it != C.TIME_UNSET } ?: Long.MAX_VALUE
        active.seekTo((active.currentPosition + deltaMs).coerceIn(0L, duration))
    }

    @Composable
    private fun CloseButton(modifier: Modifier) {
        IconButton(
            onClick = { finish() },
            modifier = modifier.safeDrawingPadding().size(48.dp).semantics { contentDescription = "Close video" },
        ) { Text("✕", color = Color.White, fontSize = 20.sp) }
    }

    @Composable
    private fun FailedPanel(failure: ClipFailure) {
        Box(Modifier.fillMaxSize().safeDrawingPadding(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(24.dp)) {
                Text(failure.message, color = Color.White, textAlign = TextAlign.Center, style = MaterialTheme.typography.bodyLarge)
                Row(Modifier.padding(top = 16.dp), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                    if (failure.canRetry) Button(onClick = { load() }, modifier = Modifier.defaultMinSize(minHeight = 48.dp)) { Text("Retry") }
                    OutlinedButton(onClick = { finish() }, modifier = Modifier.defaultMinSize(minHeight = 48.dp)) { Text("Close") }
                }
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
        onSpeed: () -> Unit,
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
                Text(title.orEmpty(), color = Color.White, style = MaterialTheme.typography.titleMedium, maxLines = 1, modifier = Modifier.padding(start = 8.dp))
            }
            Row(Modifier.align(Alignment.Center), horizontalArrangement = Arrangement.spacedBy(24.dp), verticalAlignment = Alignment.CenterVertically) {
                IconButton(
                    onClick = { onSeekBy(-SKIP_SECONDS * 1000L) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Back $SKIP_SECONDS seconds" },
                ) { Text("-$SKIP_SECONDS", color = Color.White, fontSize = 18.sp) }
                IconButton(
                    onClick = onTogglePlay,
                    modifier = Modifier.size(72.dp).background(scrim, CircleShape).semantics { contentDescription = if (isPlaying) "Pause" else "Play" },
                ) { Text(if (isPlaying) "❚❚" else "▶", color = Color.White, fontSize = 26.sp) }
                IconButton(
                    onClick = { onSeekBy(SKIP_SECONDS * 1000L) },
                    modifier = Modifier.size(56.dp).semantics { contentDescription = "Forward $SKIP_SECONDS seconds" },
                ) { Text("+$SKIP_SECONDS", color = Color.White, fontSize = 18.sp) }
            }
            Column(Modifier.fillMaxWidth().background(scrim).align(Alignment.BottomStart).padding(horizontal = 16.dp, vertical = 8.dp)) {
                TextButton(
                    onClick = onSpeed,
                    modifier = Modifier.defaultMinSize(minWidth = 48.dp, minHeight = 48.dp).semantics { contentDescription = "Playback speed" },
                ) { Text(ClipPlayback.speedLabel(speed), color = Color.White) }
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
        private const val EXTRA_ITEM_ID = "clip_item_id"
        private const val EXTRA_TITLE = "clip_title"
        private const val SKIP_SECONDS = 10

        /** Opens a Photos video by its View item id. */
        fun intent(context: Context, itemId: String, title: String?): Intent =
            Intent(context, ClipPlayerActivity::class.java)
                .putExtra(EXTRA_ITEM_ID, itemId)
                .putExtra(EXTRA_TITLE, title)
    }
}
