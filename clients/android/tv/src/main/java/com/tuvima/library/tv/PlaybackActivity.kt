package com.tuvima.library.tv

import android.net.Uri
import android.os.Bundle
import android.util.Log
import android.view.KeyEvent
import android.view.View
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.annotation.OptIn
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.unit.dp
import androidx.lifecycle.lifecycleScope
import androidx.media3.common.C
import androidx.media3.common.Format
import androidx.media3.common.MediaItem
import androidx.media3.common.MimeTypes
import androidx.media3.common.Player
import androidx.media3.common.TrackSelectionOverride
import androidx.media3.common.Tracks
import androidx.media3.common.util.UnstableApi
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.ui.PlayerView
import com.tuvima.library.core.AudioSubtitlesPanel
import com.tuvima.library.core.AudioSubtitlesPanelKeys
import com.tuvima.library.core.CoroutineDwellScheduler
import com.tuvima.library.core.FindAnotherKey
import com.tuvima.library.core.FindAnotherLanguages
import com.tuvima.library.core.FindAnotherModel
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.PanelChoice
import com.tuvima.library.core.PanelEffect
import com.tuvima.library.core.PanelRow
import com.tuvima.library.core.PanelRowKind
import com.tuvima.library.core.PlaybackManifest
import com.tuvima.library.core.PlaybackPreferences
import com.tuvima.library.core.PlaybackTrackPicker
import com.tuvima.library.core.TextTrackChoiceResult
import com.tuvima.library.core.TextTrackKind
import com.tuvima.library.core.findAnotherApi
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.json.JSONObject

/**
 * The television player. The stock player view draws the picture and the transport controls; the subtitle button is gone
 * and the app's own Audio & subtitles panel replaces it (spec "Find another on TV", D4 and D8). While the panel or the
 * Find another window is open the remote's keys go to the core key reducers (see [dispatchKeyEvent]); otherwise they
 * reach the player view as before.
 */
class PlaybackActivity : ComponentActivity() {
    private lateinit var player: ExoPlayer
    private lateinit var playerView: PlayerView
    private var assetId: String = ""
    private var itemName: String? = null
    private val client get() = (application as TuvimaTvApplication).client
        ?: error("The television is not connected to a Library server.")

    // What the overlay draws. Everything here is read by the compose overlay, so a change redraws it.
    private var controllerVisible by mutableStateOf(false)
    private var toolFocused by mutableStateOf(false)
    private var panelRows by mutableStateOf<List<PanelRow>?>(null)
    private var panelIndex by mutableIntStateOf(0)
    private var findState by mutableStateOf<FindAnotherState?>(null)
    private var findLanguages by mutableStateOf<List<String>>(emptyList())

    private var findModel: FindAnotherModel? = null
    private var manifest: PlaybackManifest? = null
    private var mediaPath: String? = null
    private var preferences: PlaybackPreferences = PlaybackPreferences.Default

    /** The footer row "Find subtitles…" is drawn only when the sources check said a source is switched on (D4, D5). */
    private var canFindSubtitles by mutableStateOf(false)

    /** Whether the video was playing when the window paused it, so closing the window puts it back as it was. */
    private var resumeAfterFind = false

    @OptIn(UnstableApi::class)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        assetId = intent.getStringExtra(ASSET_ID).orEmpty()
        itemName = intent.getStringExtra(TITLE)
        if (assetId.isBlank()) { finish(); return }
        playerView = PlayerView(this).apply {
            layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            useController = true
            // The app's own Audio & subtitles panel replaces the stock subtitle button.
            setShowSubtitleButton(false)
            setControllerVisibilityListener(
                PlayerView.ControllerVisibilityListener { visibility ->
                    controllerVisible = visibility == View.VISIBLE
                    if (!controllerVisible) toolFocused = false
                },
            )
        }
        // The overlay is a second view above the player view, so it always draws over the video surface.
        val overlay = ComposeView(this).apply {
            setContent {
                MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF9B7BFF))) {
                    PlaybackOverlay(
                        toolVisible = controllerVisible && playerReady(),
                        toolFocused = toolFocused,
                        panelRows = panelRows,
                        panelIndex = panelIndex,
                        find = findState,
                        itemName = itemName,
                        languageChoices = findLanguages,
                        onBack = { onBackKey() },
                    )
                }
            }
        }
        setContentView(
            FrameLayout(this).apply {
                addView(playerView)
                addView(overlay, ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))
            },
        )
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
        this.manifest = manifest
        mediaPath = manifest.playablePath()
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
        // The panel lists what the player reports, so it redraws whenever the tracks or the selection change.
        player.addListener(object : Player.Listener {
            override fun onTracksChanged(tracks: Tracks) = refreshPanel()
        })
        playerView.player = player
        // The profile's language choices pick the starting subtitles; the Engine's subtitle files come with the media item.
        preferences = client.playbackPreferences()
        val choice = PlaybackTrackPicker.pick(
            manifest.audioTracks, manifest.subtitleTracks, preferences.audioLanguage, preferences.subtitleLanguage, preferences.subtitleMode,
        )
        player.setMediaItem(mediaItemFor(manifest, choice.subtitleIndex))
        player.prepare()
        manifest.resumeSeconds?.takeIf { it > 0 }?.let { player.seekTo((it * 1000).toLong()) }
        player.playWhenReady = true
        // Asked once, from settings alone: with no source switched on (or a profile that may not search) the footer row is not shown.
        canFindSubtitles = client.textTrackSources(assetId, TextTrackKind.Subtitles).isReady
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

    /** The media item with the Engine's text subtitle files attached; [defaultSubtitleIndex] is the one shown from the start. */
    private fun mediaItemFor(manifest: PlaybackManifest, defaultSubtitleIndex: Int?): MediaItem {
        val subtitles = manifest.subtitleTracks
            .mapNotNull { track ->
                val url = track.deliveryUrl?.takeIf { it.isNotBlank() } ?: return@mapNotNull null
                MediaItem.SubtitleConfiguration.Builder(Uri.parse(url))
                    .setMimeType(MimeTypes.TEXT_VTT)
                    .setLanguage(track.language)
                    .setLabel(track.displayName ?: FindAnotherLanguages.nameFor(track.language))
                    .setSelectionFlags(if (track.index == defaultSubtitleIndex) C.SELECTION_FLAG_DEFAULT else 0)
                    .build()
            }
        return MediaItem.Builder()
            .setUri(mediaPath ?: manifest.playablePath())
            .setSubtitleConfigurations(subtitles)
            .build()
    }

    // ---- The remote -------------------------------------------------------------------------------------------------

    private fun playerReady(): Boolean = ::player.isInitialized

    private fun overlayOpen(): Boolean = toolFocused || panelRows != null || findModel != null

    private fun keyOf(keyCode: Int): FindAnotherKey? = when (keyCode) {
        KeyEvent.KEYCODE_DPAD_UP -> FindAnotherKey.Up
        KeyEvent.KEYCODE_DPAD_DOWN -> FindAnotherKey.Down
        KeyEvent.KEYCODE_DPAD_LEFT -> FindAnotherKey.Left
        KeyEvent.KEYCODE_DPAD_RIGHT -> FindAnotherKey.Right
        KeyEvent.KEYCODE_DPAD_CENTER, KeyEvent.KEYCODE_ENTER, KeyEvent.KEYCODE_NUMPAD_ENTER -> FindAnotherKey.Select
        else -> null
    }

    /**
     * While the tool, the panel or the window is open the arrows and Select drive them (and nothing else sees those keys);
     * Back goes through [onBackKey]. Otherwise the keys go to the player view as before, except that Up while the controls
     * show moves to the Audio & subtitles tool, and the remote's captions or menu key opens the panel directly.
     */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val key = keyOf(event.keyCode)
        val down = event.action == KeyEvent.ACTION_DOWN
        val model = findModel
        when {
            model != null -> {
                if (key == null) return super.dispatchKeyEvent(event)
                // Select acts once per press, never on auto-repeat; the arrows repeat so a held key keeps moving.
                if (down && (key != FindAnotherKey.Select || event.repeatCount == 0)) model.press(key)
                return true
            }
            panelRows != null -> {
                if (key == null) return super.dispatchKeyEvent(event)
                if (down && (key != FindAnotherKey.Select || event.repeatCount == 0)) onPanelKey(key)
                return true
            }
            toolFocused -> {
                if (key == null) return super.dispatchKeyEvent(event)
                if (down && event.repeatCount == 0) {
                    when (key) {
                        FindAnotherKey.Select -> openPanel()
                        FindAnotherKey.Down -> toolFocused = false
                        else -> Unit
                    }
                }
                return true
            }
        }
        if (::player.isInitialized) {
            if (event.keyCode == KeyEvent.KEYCODE_CAPTIONS || event.keyCode == KeyEvent.KEYCODE_MENU) {
                if (down && event.repeatCount == 0) openPanel()
                return true
            }
            if (key == FindAnotherKey.Up && controllerVisible) {
                if (down) toolFocused = true
                return true
            }
        }
        if (::playerView.isInitialized && playerView.dispatchKeyEvent(event)) return true
        return super.dispatchKeyEvent(event)
    }

    /** Back closes the language list, then the full words, then the window (the model does that); then the panel; then the tool. */
    private fun onBackKey() {
        val model = findModel
        when {
            model != null -> model.press(FindAnotherKey.Back)
            panelRows != null -> closePanel()
            toolFocused -> toolFocused = false
        }
    }

    // ---- The Audio & subtitles panel --------------------------------------------------------------------------------

    private fun openPanel() {
        if (!::player.isInitialized) return
        val rows = panelRowsNow()
        panelRows = rows
        panelIndex = AudioSubtitlesPanel.initialIndex(rows)
        toolFocused = false
        playerView.hideController()
    }

    private fun closePanel() {
        panelRows = null
    }

    private fun panelRowsNow(): List<PanelRow> = AudioSubtitlesPanel.rows(
        audio = trackChoices(C.TRACK_TYPE_AUDIO),
        subtitles = trackChoices(C.TRACK_TYPE_TEXT),
        canFind = canFindSubtitles,
    )

    private fun refreshPanel() {
        if (panelRows == null) return
        val rows = panelRowsNow()
        panelRows = rows
        panelIndex = panelIndex.coerceIn(0, (AudioSubtitlesPanel.focusable(rows).size - 1).coerceAtLeast(0))
    }

    /** The tracks of one type the player reports, as panel choices keyed "group:track". Unplayable tracks are left out. */
    private fun trackChoices(type: Int): List<PanelChoice> = buildList {
        player.currentTracks.groups.forEachIndexed { groupIndex, group ->
            if (group.type != type) return@forEachIndexed
            for (trackIndex in 0 until group.length) {
                if (!group.isTrackSupported(trackIndex)) continue
                add(PanelChoice("$groupIndex:$trackIndex", trackLabel(group.getTrackFormat(trackIndex), size + 1), group.isTrackSelected(trackIndex)))
            }
        }
    }

    private fun trackLabel(format: Format, number: Int): String =
        format.label?.takeIf { it.isNotBlank() }
            ?: format.language?.takeIf { it.isNotBlank() && !it.equals("und", ignoreCase = true) }?.let { FindAnotherLanguages.nameFor(it) }
            ?: "Track $number"

    private fun onPanelKey(key: FindAnotherKey) {
        val rows = panelRows ?: return
        val count = AudioSubtitlesPanel.focusable(rows).size
        val result = AudioSubtitlesPanelKeys.reduce(panelIndex, count, key)
        panelIndex = result.index
        when (val effect = result.effect) {
            is PanelEffect.Activate -> activatePanelRow(AudioSubtitlesPanel.focusable(rows)[effect.index])
            PanelEffect.Close -> closePanel()
            PanelEffect.None -> Unit
        }
    }

    private fun activatePanelRow(row: PanelRow) {
        when (row.kind) {
            PanelRowKind.Audio -> selectTrack(row.id, C.TRACK_TYPE_AUDIO)
            PanelRowKind.Subtitle -> selectTrack(row.id, C.TRACK_TYPE_TEXT)
            PanelRowKind.SubtitlesOff -> player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
                .clearOverridesOfType(C.TRACK_TYPE_TEXT)
                .build()
            PanelRowKind.Find -> openFind()
            PanelRowKind.AudioHeader, PanelRowKind.SubtitlesHeader -> Unit
        }
    }

    private fun selectTrack(id: String, type: Int) {
        val parts = id.split(':')
        val groupIndex = parts.getOrNull(0)?.toIntOrNull() ?: return
        val trackIndex = parts.getOrNull(1)?.toIntOrNull() ?: return
        val group = player.currentTracks.groups.getOrNull(groupIndex) ?: return
        player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
            .setTrackTypeDisabled(type, false)
            .setOverrideForType(TrackSelectionOverride(group.mediaTrackGroup, trackIndex))
            .build()
    }

    // ---- The Find another window ------------------------------------------------------------------------------------

    private fun openFind() {
        val current = manifest ?: return
        // The video stays paused behind the window; closing the window puts it back as it was.
        resumeAfterFind = player.playWhenReady
        player.pause()
        val languages = FindAnotherLanguages.choices(
            preferred = preferences.subtitleLanguage,
            others = listOf(preferences.audioLanguage) + current.subtitleTracks.map { it.language },
        )
        findLanguages = languages
        val model = FindAnotherModel(
            api = client.findAnotherApi(assetId, TextTrackKind.Subtitles),
            scope = lifecycleScope,
            scheduler = CoroutineDwellScheduler(lifecycleScope),
            kind = TextTrackKind.Subtitles,
            initialLanguage = FindAnotherLanguages.nameFor(preferences.subtitleLanguage),
            languageChoices = languages,
            itemNoun = "title",
            onChange = { findState = it },
            onApplied = { result -> onFindApplied(result) },
            onClosed = { closeFind() },
        )
        findModel = model
        findState = model.state
        model.start()
    }

    /** Back or Cancel: the window goes, the panel stays, the video goes on as it was. */
    private fun closeFind() {
        findModel?.close()
        findModel = null
        findState = null
        player.playWhenReady = resumeAfterFind
    }

    /** Use this worked: close the window and the panel, then play on from the same place with the new subtitles. */
    private fun onFindApplied(result: TextTrackChoiceResult) {
        findModel?.close()
        findModel = null
        findState = null
        panelRows = null
        toolFocused = false
        lifecycleScope.launch { reloadWithNewSubtitles() }
    }

    @OptIn(UnstableApi::class)
    private suspend fun reloadWithNewSubtitles() {
        val position = player.currentPosition
        val fresh = try {
            client.playbackManifest(assetId, "local")
        } catch (error: kotlin.coroutines.cancellation.CancellationException) {
            throw error
        } catch (error: Exception) {
            // The choice is already saved on the Engine; without the refreshed list the video simply carries on as it was.
            Log.w(TAG, "The playback manifest could not be refreshed after choosing subtitles", error)
            null
        }
        if (fresh != null) {
            manifest = fresh
            // The chosen track is the profile's preferred one now; text is switched back on in case the person had turned it off.
            val choice = PlaybackTrackPicker.pick(
                fresh.audioTracks, fresh.subtitleTracks, preferences.audioLanguage, preferences.subtitleLanguage, preferences.subtitleMode,
            )
            val chosen = fresh.subtitleTracks.firstOrNull { it.isDefault }?.index ?: choice.subtitleIndex
            player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
                .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, false)
                .clearOverridesOfType(C.TRACK_TYPE_TEXT)
                .build()
            player.setMediaItem(mediaItemFor(fresh, chosen), position)
            player.prepare()
        }
        player.playWhenReady = resumeAfterFind
    }

    override fun onStop() {
        if (::player.isInitialized) player.pause()
        super.onStop()
    }

    override fun onDestroy() {
        findModel?.close()
        if (::player.isInitialized) player.release()
        super.onDestroy()
    }

    companion object {
        const val ASSET_ID = "asset_id"

        /** The item's name for the Find another window's header (optional). */
        const val TITLE = "title"

        private const val TAG = "PlaybackActivity"
    }
}

/** What draws over the video: the tool with the controls, the side panel, and the Find another window. */
@Composable
private fun PlaybackOverlay(
    toolVisible: Boolean,
    toolFocused: Boolean,
    panelRows: List<PanelRow>?,
    panelIndex: Int,
    find: FindAnotherState?,
    itemName: String?,
    languageChoices: List<String>,
    onBack: () -> Unit,
) {
    // Back is taken only while something of ours is open; otherwise it leaves the player as before.
    BackHandler(enabled = toolFocused || panelRows != null || find != null, onBack = onBack)
    Box(Modifier.fillMaxSize()) {
        BoxWithConstraints(Modifier.fillMaxSize()) {
            // 5 percent safe margins (D2).
            val horizontal = maxWidth * 0.05f
            val vertical = maxHeight * 0.05f
            if (toolVisible && panelRows == null && find == null) {
                AudioSubtitlesToolButton(toolFocused, Modifier.align(Alignment.TopEnd).padding(end = horizontal, top = vertical))
            }
            if (panelRows != null) {
                Box(Modifier.align(Alignment.CenterEnd).padding(end = horizontal, top = vertical, bottom = vertical)) {
                    AudioSubtitlesPanelView(panelRows, panelIndex)
                }
            }
        }
        if (find != null) FindAnotherWindow(find, itemName, languageChoices)
    }
}
