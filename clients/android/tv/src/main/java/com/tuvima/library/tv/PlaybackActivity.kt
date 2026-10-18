package com.tuvima.library.tv

import android.net.Uri
import android.os.Bundle
import android.view.ViewGroup
import androidx.activity.ComponentActivity
import androidx.annotation.OptIn
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.MediaItem
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.tuvima.library.core.PlaybackManifest
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.json.JSONObject

class PlaybackActivity : ComponentActivity() {
    private lateinit var player: ExoPlayer
    private lateinit var playerView: PlayerView
    private var assetId: String = ""
    private val client get() = (application as TuvimaTvApplication).client
        ?: error("The television is not connected to a Tuvima server.")

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        assetId = intent.getStringExtra(ASSET_ID).orEmpty()
        if (assetId.isBlank()) { finish(); return }
        playerView = PlayerView(this).apply {
            layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            useController = true
            setShowSubtitleButton(true)
        }
        setContentView(playerView)
        lifecycleScope.launch { startPlayback(awaitPlayableManifest()) }
    }

    private suspend fun awaitPlayableManifest(): PlaybackManifest {
        repeat(60) {
            val manifest = client.playbackManifest(assetId, "local")
            if (manifest.playablePath() != null) return manifest
            if (manifest.hlsStatus != "preparing") error(manifest.warnings.joinToString().ifBlank { "Media preparation failed." })
            delay(2_000)
        }
        error("Media preparation timed out.")
    }

    @OptIn(UnstableApi::class)
    private suspend fun startPlayback(manifest: PlaybackManifest) {
        // The token and the server address (home or outside) are read when each request is made, never once at start-up:
        // the token lives about ten minutes and the address can change when the television changes network.
        val dataSource = ResolvingDataSource.Factory(DefaultHttpDataSource.Factory()) { dataSpec ->
            val located = dataSpec.withUri(Uri.parse(client.resolveUrl(dataSpec.uri.toString())))
            // Only the server's own addresses get the token; a link to any other host is fetched without it.
            val token = runBlocking { client.streamBearerTokenFor(located.uri.toString()) }
            if (token == null) located else located.withAdditionalHeaders(mapOf("Authorization" to "Bearer $token"))
        }
        player = ExoPlayer.Builder(this)
            .setMediaSourceFactory(DefaultMediaSourceFactory(this).setDataSourceFactory(dataSource))
            .build()
        playerView.player = player
        player.setMediaItem(MediaItem.fromUri(manifest.playablePath()!!))
        player.prepare()
        manifest.resumeSeconds?.takeIf { it > 0 }?.let { player.seekTo((it * 1000).toLong()) }
        player.playWhenReady = true
        lifecycleScope.launch {
            while (isActive) {
                delay(15_000)
                client.heartbeat(
                    JSONObject()
                        .put("assetId", assetId)
                        .put("isPlaying", player.isPlaying)
                        .put("positionSeconds", player.currentPosition / 1000.0)
                        .put("durationSeconds", player.duration.takeIf { it > 0 }?.div(1000.0))
                        .put("playbackRate", player.playbackParameters.speed.toDouble()),
                )
            }
        }
    }

    override fun onStop() {
        if (::player.isInitialized) player.pause()
        super.onStop()
    }

    override fun onDestroy() {
        if (::player.isInitialized) player.release()
        super.onDestroy()
    }

    companion object { const val ASSET_ID = "asset_id" }
}
