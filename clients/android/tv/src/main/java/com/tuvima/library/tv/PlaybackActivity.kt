package com.tuvima.library.tv

import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color as AndroidColor
import android.net.Uri
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import android.util.LruCache
import android.view.KeyEvent
import android.view.ViewGroup
import android.widget.FrameLayout
import androidx.activity.ComponentActivity
import androidx.activity.OnBackPressedCallback
import androidx.annotation.OptIn
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.ComposeView
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
import androidx.media3.ui.CaptionStyleCompat
import androidx.media3.ui.PlayerView
import com.tuvima.library.core.Chapter
import com.tuvima.library.core.CoroutineDwellScheduler
import com.tuvima.library.core.FindAnotherKey
import com.tuvima.library.core.FindAnotherLanguages
import com.tuvima.library.core.FindAnotherModel
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.FindAnotherWording
import com.tuvima.library.core.LyricsFollow
import com.tuvima.library.core.LyricsView
import com.tuvima.library.core.NextEpisodeLogic
import com.tuvima.library.core.NextEpisodeOffer
import com.tuvima.library.core.PlaybackManifest
import com.tuvima.library.core.PlaybackPreferences
import com.tuvima.library.core.PlaybackTrackPicker
import com.tuvima.library.core.TextTrackChoiceResult
import com.tuvima.library.core.TextTrackKind
import com.tuvima.library.core.TuvimaHttpException
import com.tuvima.library.core.TvAudioTrack
import com.tuvima.library.core.TvBookmark
import com.tuvima.library.core.TvBookmarkTab
import com.tuvima.library.core.TvBookmarkView
import com.tuvima.library.core.TvBookmarks
import com.tuvima.library.core.TvChapters
import com.tuvima.library.core.TvControlsFocus
import com.tuvima.library.core.TvDeviceChoices
import com.tuvima.library.core.TvEpisode
import com.tuvima.library.core.TvEpisodes
import com.tuvima.library.core.TvFocus
import com.tuvima.library.core.TvKeyAction
import com.tuvima.library.core.TvKeyEffect
import com.tuvima.library.core.TvKeyMap
import com.tuvima.library.core.TvKeyState
import com.tuvima.library.core.TvMediaKind
import com.tuvima.library.core.TvPanel
import com.tuvima.library.core.TvPanelEffect
import com.tuvima.library.core.TvPanelFocus
import com.tuvima.library.core.TvPanelId
import com.tuvima.library.core.TvPanelKeys
import com.tuvima.library.core.TvPanels
import com.tuvima.library.core.TvPlaybackInformation
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvPlayerWords
import com.tuvima.library.core.TvPlayingNow
import com.tuvima.library.core.TvRemoteKeys
import com.tuvima.library.core.TvRepeatMode
import com.tuvima.library.core.TvRow
import com.tuvima.library.core.TvSeason
import com.tuvima.library.core.TvSkip
import com.tuvima.library.core.TvSubtitleAppearance
import com.tuvima.library.core.TvSubtitleTrack
import com.tuvima.library.core.TvZone
import com.tuvima.library.core.findAnotherApi
import com.tuvima.library.core.TvPlayerLogic.PLAY_INDEX
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import org.json.JSONObject
import kotlin.coroutines.cancellation.CancellationException

/**
 * The television player, following the player contract (`clients/contracts/v1/player-controls.md`, Televisions) and the approved
 * mock-up. The stock player view only draws the picture; the controls, the side panels, the paused details and the end card are
 * the app's own ([TvPlayerOverlay]) and take their words, rows and key effects from the core classes (`TvPlayerLogic`,
 * `TvPanels`, `TvKeyMap`, `TvPanelKeys`), which are unit-tested. This class only connects them to ExoPlayer and the Engine.
 *
 * Music and audiobooks use the same screen as a full-screen Now playing, with Lyrics or Chapters as the side panel.
 */
class PlaybackActivity : ComponentActivity() {
    private lateinit var player: ExoPlayer
    private lateinit var playerView: PlayerView
    private lateinit var store: TvDeviceStore
    private var assetId: String = ""
    private var itemName: String? = null
    private var entityType: String = ""
    private var entityId: String = ""
    private val client get() = (application as TuvimaTvApplication).client
        ?: error("The television is not connected to a Library server.")

    /** Everything the screen draws; [rebuild] recomputes it from the fields below whenever one changes. */
    private var ui by mutableStateOf(TvPlayerScreenState(status = "Starting…"))

    /** The sentence shown instead of the player when a title will not start; kept so a redraw does not wipe it. */
    private var problem: String? = null

    // What is playing.
    private var kind = TvMediaKind.Film
    private var manifest: PlaybackManifest? = null
    private var mediaPath: String? = null
    private var preferences: PlaybackPreferences = PlaybackPreferences.Default
    private var device = TvDeviceChoices()
    private var title = ""
    private var line = ""
    private var description = ""
    private var rating = ""
    private var artworkUrl: String? = null
    private var showId: String? = null
    private var workId: String? = null
    private var seasons: List<TvSeason> = emptyList()
    private var chapters: List<Chapter> = emptyList()
    private var yearText: String? = null
    private var subtitleLine: String? = null
    private var contentRating: String? = null

    // The controls overlay and its focus.
    private var overlayVisible = false
    private var focus = TvFocus.initial(PLAY_INDEX)
    private var hideJob: Job? = null
    private var tickJob: Job? = null

    // The side panel (one at a time) and where focus is inside it.
    private var panelId: TvPanelId? = null
    private var panelFocus = TvPanelFocus()
    private var seasonIndex = 0

    // Bookmarks.
    private var bookmarks: List<TvBookmark> = emptyList()
    private var bmTab = TvBookmarkTab.Mine
    private var bmView = TvBookmarkView()

    // The Lyrics panel (music only).
    private var lyrics by mutableStateOf(LyricsView.loading())
    private var lyricsIndex = -1
    private var canFindLyrics = false
    private var lyricsGeneration = 0
    private var lyricsFollowJob: Job? = null

    // The end card.
    private var endShownAt = 0L
    private var endElapsedMs = 0L
    private var endFocus = 1
    private var endCancelled = false
    private var switching = false

    // Find another.
    private var findState by mutableStateOf<FindAnotherState?>(null)
    private var findLanguages by mutableStateOf<List<String>>(emptyList())
    private var findModel: FindAnotherModel? = null
    private var findKind = TextTrackKind.Subtitles

    /** The footer row "Find subtitles…" is drawn only when the sources check said a source is switched on (D4, D5). */
    private var canFindSubtitles = false

    /** Whether the video was playing when the window paused it, so closing the window puts it back as it was. */
    private var resumeAfterFind = false

    private val bitmaps = LruCache<String, Bitmap>(24)
    private val imageLoader: TvImageLoader = { url ->
        bitmaps.get(url) ?: loadBitmap(url)?.also { bitmaps.put(url, it) }
    }

    @OptIn(UnstableApi::class)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        assetId = intent.getStringExtra(ASSET_ID).orEmpty()
        itemName = intent.getStringExtra(TITLE)
        entityType = intent.getStringExtra(ENTITY_TYPE).orEmpty()
        entityId = intent.getStringExtra(ENTITY_ID).orEmpty()
        if (assetId.isBlank()) { finish(); return }
        store = TvDeviceStore(this)
        device = store.load()
        title = itemName.orEmpty()
        kind = guessKind()
        // The stock controller is off: the app draws its own controls from the contract.
        playerView = PlayerView(this).apply {
            layoutParams = ViewGroup.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT)
            useController = false
        }
        // The overlay is a second view above the player view, so it always draws over the video surface.
        val overlay = ComposeView(this).apply {
            setContent {
                MaterialTheme(colorScheme = darkColorScheme(primary = Color(0xFF8852FC))) {
                    TvPlayerOverlay(
                        ui.copy(lyrics = lyrics, find = findState, itemName = itemName, languageChoices = findLanguages),
                        imageLoader,
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
        // Back reaches here from the system (a key event is handled in dispatchKeyEvent first).
        onBackPressedDispatcher.addCallback(this, object : OnBackPressedCallback(true) {
            override fun handleOnBackPressed() = handleBack()
        })
        lifecycleScope.launch {
            try {
                startPlayer()
                openAsset(assetId, restart = false)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "Playback could not start", error)
                problem = failureSentence(error)
                ui = ui.copy(status = problem)
            }
        }
    }

    /** The sentences a person gets when a title will not start. */
    private fun failureSentence(error: Exception): String = when ((error as? TuvimaHttpException)?.statusCode) {
        403 -> "This profile cannot play this title."
        404 -> "This title is no longer available."
        else -> error.message?.takeIf { it.startsWith("Media preparation") } ?: "Playback could not start. Check the connection to the server."
    }

    private fun guessKind(): TvMediaKind {
        val type = entityType.lowercase()
        return TvPlayerLogic.kindOf(type, hasShow = type == "tvshow" || type == "tvepisode")
    }

    // ---- starting and switching what plays ------------------------------------------------------------------------------

    @OptIn(UnstableApi::class)
    private fun startPlayer() {
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
        // The panel lists what the player reports, so it redraws whenever the tracks, the state or the selection change.
        player.addListener(object : Player.Listener {
            override fun onTracksChanged(tracks: Tracks) = rebuild()
            override fun onPlaybackStateChanged(playbackState: Int) = onStateChanged()
            override fun onPlayWhenReadyChanged(playWhenReady: Boolean, reason: Int) = onStateChanged()
        })
        playerView.player = player
        tickJob = lifecycleScope.launch {
            while (isActive) {
                delay(TICK_MS)
                onTick()
            }
        }
        lifecycleScope.launch {
            while (isActive) {
                delay(15_000)
                sendHeartbeat()
            }
        }
    }

    private suspend fun awaitPlayableManifest(id: String): PlaybackManifest {
        repeat(60) {
            val next = client.playbackManifest(id, "local")
            if (next.playablePath() != null) return next
            if (next.hlsStatus != "preparing") error(next.warnings.joinToString().ifBlank { "Media preparation failed." })
            delay(2_000)
        }
        error("Media preparation timed out.")
    }

    /** Plays [id] from the start of the screen's life or after a switch of episode; the screen keeps its show, panels aside. */
    @OptIn(UnstableApi::class)
    private suspend fun openAsset(id: String, restart: Boolean) {
        val next = awaitPlayableManifest(id)
        assetId = id
        manifest = next
        mediaPath = next.playablePath()
        chapters = emptyList()
        endShownAt = 0L
        endElapsedMs = 0L
        endCancelled = false
        switching = false
        canFindSubtitles = false
        bookmarks = emptyList()
        bmView = TvBookmarkView()
        preferences = client.playbackPreferences()
        kind = TvPlayerLogic.kindOf(next.mediaType, hasShow = showId != null || guessKind() == TvMediaKind.Episode)
        if (LyricsFollow.isMusic(next.mediaType)) kind = TvMediaKind.Music
        if (kind == TvMediaKind.Audiobook) chapters = next.chapters
        applySubtitleAppearance()
        // The profile's language choices pick the starting subtitles; the Engine's subtitle files come with the media item.
        val choice = PlaybackTrackPicker.pick(
            next.audioTracks, next.subtitleTracks, preferences.audioLanguage, preferences.subtitleLanguage, preferences.subtitleMode,
        )
        player.setMediaItem(mediaItemFor(next, choice.subtitleIndex))
        player.prepare()
        if (!restart) next.resumeSeconds?.takeIf { it > 0 }?.let { player.seekTo((it * 1000).toLong()) }
        if (kind != TvMediaKind.Music) player.setPlaybackSpeed((device.speed ?: preferences.defaultPlaybackSpeed).toFloat().coerceIn(0.5f, 3f))
        player.playWhenReady = true
        problem = null
        ui = ui.copy(status = null)
        showOverlay()
        rebuild()
        // Everything below is read beside the start; none of it holds playback up.
        lifecycleScope.launch { loadDetails() }
        // Asked once, from settings alone: with no source switched on (or a profile that may not search) the footer row is not shown.
        // A song has no Audio & subtitles panel, so nothing is asked for it.
        if (kind != TvMediaKind.Music && kind != TvMediaKind.Audiobook) {
            lifecycleScope.launch {
                canFindSubtitles = client.textTrackSources(id, TextTrackKind.Subtitles).isReady
                rebuild()
            }
        }
    }

    /** The detail page of what plays (the line under the title, the rating card, the cover) and, for a show, its episodes. */
    private suspend fun loadDetails() {
        val forAsset = assetId
        if (entityType.isNotBlank() && entityId.isNotBlank()) {
            try {
                val detail = client.detailPage(entityType, entityId)
                if (forAsset != assetId) return
                contentRating = detail.contentRating
                yearText = detail.year
                subtitleLine = detail.subtitle
                artworkUrl = detail.artworkUrl
                if (showId == null) showId = if (entityType.equals("tvShow", ignoreCase = true)) entityId else detail.showId
                if (title.isBlank()) title = detail.title
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The details of what is playing could not be read", error)
            }
        }
        val show = showId
        if (show != null && seasons.isEmpty()) {
            try {
                val json = client.details("tvShow", show)
                if (forAsset != assetId) return
                seasons = TvEpisodes.seasons(json) { it }
                json.optString("title").takeIf { it.isNotBlank() }?.let { title = it }
                if (kind != TvMediaKind.Episode && seasons.isNotEmpty()) kind = TvMediaKind.Episode
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // No episode list: Previous, Next and the Episodes panel stay absent or dimmed, and the video plays on.
                Log.w(TAG, "The episodes of the show could not be read", error)
            }
        }
        workId = when {
            seasons.isNotEmpty() -> TvEpisodes.find(seasons, forAsset)?.workId?.takeIf { it.isNotBlank() }
            entityType.equals("tvShow", true) || entityType.equals("collection", true) -> null
            else -> entityId.takeIf { it.isNotBlank() }
        }
        val episode = TvEpisodes.find(seasons, forAsset)
        seasonIndex = TvEpisodes.seasonIndexOf(seasons, forAsset)
        line = when (kind) {
            TvMediaKind.Episode -> TvPlayerLogic.detailLine(
                TvMediaKind.Episode, season = episode?.season, episode = episode?.number, title = episode?.title,
                durationSeconds = durationSeconds(),
            )
            TvMediaKind.Music, TvMediaKind.Audiobook -> subtitleLine.orEmpty()
            else -> TvPlayerLogic.detailLine(kind, year = yearText?.toIntOrNull(), durationSeconds = durationSeconds())
        }
        description = if (kind == TvMediaKind.Episode || kind == TvMediaKind.Lesson) episode?.description.orEmpty() else ""
        rating = listOfNotNull(contentRating?.takeIf { it.isNotBlank() }, episode?.title?.takeIf { it.isNotBlank() && contentRating?.isNotBlank() == true })
            .joinToString(TvPlayerWords.SEP)
        rebuild()
        if (workId != null && kind != TvMediaKind.Music) loadBookmarks()
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

    private suspend fun loadBitmap(url: String): Bitmap? = try {
        val bytes = client.authenticatedBytes(url)
        BitmapFactory.decodeByteArray(bytes, 0, bytes.size)
    } catch (error: CancellationException) {
        throw error
    } catch (error: Exception) {
        // A picture that cannot be read leaves the plain placeholder; nothing else depends on it.
        Log.w(TAG, "A picture could not be read", error)
        null
    }

    private fun sendHeartbeat() {
        if (!::player.isInitialized || manifest == null) return
        val payload = JSONObject()
            .put("assetId", assetId)
            .put("isPlaying", player.isPlaying)
            .put("positionSeconds", player.currentPosition / 1000.0)
            .put("durationSeconds", player.duration.takeIf { it > 0 }?.div(1000.0))
            .put("playbackRate", player.playbackParameters.speed.toDouble())
        lifecycleScope.launch {
            try {
                client.heartbeat(payload)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The playback position could not be saved", error)
            }
        }
    }

    // ---- what the screen shows ------------------------------------------------------------------------------------------

    private fun positionSeconds(): Double = if (::player.isInitialized) player.currentPosition / 1000.0 else 0.0

    private fun durationSeconds(): Double? =
        (if (::player.isInitialized) player.duration.takeIf { it > 0 }?.div(1000.0) else null) ?: manifest?.durationSeconds

    private fun isAudioKind() = kind == TvMediaKind.Music || kind == TvMediaKind.Audiobook

    private fun skip(): TvSkip = TvPlayerLogic.skipSeconds(preferences, device)

    private fun autoplay(): Boolean = TvPlayerLogic.autoplayOn(preferences, device)

    private fun speed(): Double = TvPlayerLogic.clampSpeed(if (::player.isInitialized) player.playbackParameters.speed.toDouble() else 1.0)

    private fun nextEpisode(): TvEpisode? = TvEpisodes.neighbours(seasons, assetId).next

    private fun repeatMode(): TvRepeatMode = when (if (::player.isInitialized) player.repeatMode else Player.REPEAT_MODE_OFF) {
        Player.REPEAT_MODE_ALL -> TvRepeatMode.All
        Player.REPEAT_MODE_ONE -> TvRepeatMode.One
        else -> TvRepeatMode.Off
    }

    /** Recomputes everything the screen draws from the player, the Engine's answers and the choices made. */
    private fun rebuild() {
        if (!::player.isInitialized || manifest == null) return
        val position = positionSeconds()
        val duration = durationSeconds()
        val playing = player.playWhenReady
        val neighbours = TvEpisodes.neighbours(seasons, assetId)
        val canPrevious: Boolean
        val canNext: Boolean
        when (kind) {
            TvMediaKind.Episode -> { canPrevious = neighbours.previous != null; canNext = neighbours.next != null }
            TvMediaKind.Audiobook -> { canPrevious = TvChapters.hasPrevious(chapters, position); canNext = TvChapters.hasNext(chapters, position) }
            TvMediaKind.Music -> { canPrevious = TvPlayerLogic.musicCanPrevious(position, false); canNext = false }
            else -> { canPrevious = false; canNext = false }
        }
        val transport = TvPlayerLogic.transport(
            kind, skip(), playing, canPrevious, canNext, shuffleOn = player.shuffleModeEnabled, repeat = repeatMode(),
        )
        val tools = TvPlayerLogic.tools(
            kind,
            hasEpisodes = TvEpisodes.flatten(seasons).isNotEmpty(),
            canBookmark = workId != null && kind != TvMediaKind.Music,
            hasChapters = chapters.isNotEmpty(),
        )
        val panel = panelId?.let { currentPanel(it, position, duration) }
        val next = nextEpisode()
        val card = if (endShownAt > 0L && next != null) {
            val offer = endOffer(position, duration)
            val remaining = if (offer == NextEpisodeOffer.Countdown) NextEpisodeLogic.secondsLeft((endElapsedMs / 1000).toInt()) else null
            TvPlayerLogic.endCard(next.title, kind, remaining)
        } else {
            null
        }
        // Focus only ever rests on something that is there: tools can disappear (a show's list not loaded yet).
        val toolCount = tools.size
        focus = focus.copy(
            transportIndex = focus.transportIndex.coerceIn(0, (transport.size - 1).coerceAtLeast(0)),
            toolIndex = focus.toolIndex.coerceIn(0, (toolCount - 1).coerceAtLeast(0)),
            zone = if (focus.zone == TvZone.Tools && toolCount == 0) TvZone.Transport else focus.zone,
            panelOpen = panel != null,
        )
        ui = ui.copy(
            status = problem,
            kind = kind,
            title = title,
            line = line,
            description = description,
            rating = rating,
            artworkUrl = artworkUrl,
            overlayVisible = overlayVisible || isAudioKind(),
            focus = focus,
            transport = transport,
            repeat = repeatMode(),
            tools = tools,
            selectedTool = panelId?.let { TvPlayerLogic.toolOf(it) },
            playing = playing,
            timeline = TvChapters.timeline(chapters, position, duration, device.timelineWholeBook),
            ticks = if (device.showBookmarksOnTimeline && duration != null) TvPanels.bookmarkTicks(bookmarks, duration) else emptyList(),
            panel = panel,
            panelFocus = panelFocus,
            lyricsIndex = lyricsIndex,
            endCard = card,
            endFocus = endFocus,
            endCardImage = next?.stillUrl,
        )
    }

    private fun currentPanel(id: TvPanelId, position: Double, duration: Double?): TvPanel = when (id) {
        TvPanelId.Episodes -> TvPanels.episodes(
            seasons, seasonIndex, kind == TvMediaKind.Lesson,
            TvPlayingNow(assetId, player.playWhenReady, position, duration ?: 0.0, nextEpisode()?.assetId), autoplay(),
        )
        TvPanelId.AudioSubtitles -> TvPanels.audioSubtitles(audioTracks(), subtitleTracks(), canFindSubtitles)
        TvPanelId.SubtitleAppearance -> TvSubtitleAppearance.of(preferences, device).let { TvPanels.subtitleAppearance(it.style, it.background) }
        TvPanelId.Bookmarks -> TvPanels.bookmarks(bookmarks, bmTab, position, bmView, device.showBookmarksOnTimeline)
        TvPanelId.PlaybackSettings -> TvPanels.playbackSettings(speed(), skip())
        TvPanelId.Speed -> TvPanels.speed(speed())
        TvPanelId.SkipBack -> TvPanels.skipChoices(false, skip().back)
        TvPanelId.SkipForward -> TvPanels.skipChoices(true, skip().forward)
        TvPanelId.PlaybackInformation -> TvPanels.playbackInformation(playbackLines())
        TvPanelId.Lyrics -> TvPanel(
            id = TvPanelId.Lyrics,
            title = TvPlayerWords.LYRICS,
            footer = if (canFindLyrics) listOf(TvRow("find", FindAnotherWording.entryLabel(TextTrackKind.Lyrics), trailing = "›")) else emptyList(),
            focusKey = "find",
        )
        TvPanelId.Chapters -> TvPanels.chapters(chapters, position, duration, device.timelineWholeBook)
    }

    private fun playbackLines(): List<Pair<String, String>> {
        var height = 0
        var videoMime: String? = null
        var audioMime: String? = null
        var channels = 0
        player.currentTracks.groups.forEach { group ->
            for (index in 0 until group.length) {
                if (!group.isTrackSelected(index)) continue
                val format = group.getTrackFormat(index)
                if (group.type == C.TRACK_TYPE_VIDEO) { height = format.height; videoMime = format.sampleMimeType }
                if (group.type == C.TRACK_TYPE_AUDIO) { audioMime = format.sampleMimeType; channels = format.channelCount }
            }
        }
        return TvPlaybackInformation.lines(manifest, height, videoMime, audioMime, channels)
    }

    // ---- the overlay -----------------------------------------------------------------------------------------------------

    private fun showOverlay() {
        overlayVisible = true
        scheduleHide()
        rebuild()
    }

    /** The overlay hides after 5 seconds with no input, never while a panel, the Find window or the end card is open, nor while paused. */
    private fun scheduleHide() {
        hideJob?.cancel()
        if (isAudioKind()) return
        hideJob = lifecycleScope.launch {
            delay(TvPlayerLogic.OVERLAY_HIDE_MS)
            val paused = ::player.isInitialized && !player.playWhenReady
            if (TvPlayerLogic.overlayShouldHide(paused, panelId != null, findModel != null, endShownAt > 0L)) {
                overlayVisible = false
                rebuild()
            } else {
                scheduleHide()
            }
        }
    }

    private fun onStateChanged() {
        if (::player.isInitialized && player.playbackState == Player.STATE_ENDED) {
            // The end of a title: an episode with a next one keeps its end card; anything else closes the player.
            if (!(kind == TvMediaKind.Episode && nextEpisode() != null && !switching)) { finish(); return }
        }
        rebuild()
    }

    // ---- ticking: position, the timeline and the end card ---------------------------------------------------------------

    private fun endOffer(position: Double, duration: Double?): NextEpisodeOffer =
        NextEpisodeLogic.nextEpisodeOffer(
            position, duration, manifest?.segments.orEmpty(), autoplay(),
            ended = player.playbackState == Player.STATE_ENDED, cancelled = endCancelled,
        )

    private fun onTick() {
        if (!::player.isInitialized || manifest == null || switching) return
        if (kind == TvMediaKind.Episode && nextEpisode() != null) {
            val position = positionSeconds()
            val duration = durationSeconds()
            val ended = player.playbackState == Player.STATE_ENDED
            when (endOffer(position, duration)) {
                NextEpisodeOffer.None -> { endShownAt = 0L; endElapsedMs = 0L }
                NextEpisodeOffer.Manual -> if (endShownAt == 0L) { endShownAt = SystemClock.elapsedRealtime(); endFocus = 1 }
                NextEpisodeOffer.Countdown -> {
                    if (endShownAt == 0L) { endShownAt = SystemClock.elapsedRealtime(); endFocus = 1 }
                    // The countdown runs only while the video plays (or has ended), never while the person has paused it.
                    if (NextEpisodeLogic.shouldTick(started = true, playing = player.isPlaying, ended = ended)) endElapsedMs += TICK_MS
                    if (NextEpisodeLogic.secondsLeft((endElapsedMs / 1000).toInt()) == 0) { playNext(); return }
                }
            }
        }
        rebuild()
    }

    private fun cancelEndCard() {
        endCancelled = true
        rebuild()
    }

    private fun playNext() {
        val next = nextEpisode() ?: return
        switchEpisode(next)
    }

    /** Plays another episode of the show in this same screen: the saved position of the old one goes to the Engine first. */
    private fun switchEpisode(episode: TvEpisode) {
        if (episode.assetId.equals(assetId, ignoreCase = true)) { player.play(); return }
        switching = true
        sendHeartbeat()
        endShownAt = 0L
        endElapsedMs = 0L
        lifecycleScope.launch {
            try {
                workId = episode.workId.takeIf { it.isNotBlank() }
                openAsset(episode.assetId, restart = false)
                loadDetails()
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The next episode could not start", error)
                switching = false
                problem = failureSentence(error)
                ui = ui.copy(status = problem)
            }
        }
    }

    // ---- the remote -------------------------------------------------------------------------------------------------------

    private fun findKeyOf(action: TvKeyAction): FindAnotherKey? = when (action) {
        TvKeyAction.Up -> FindAnotherKey.Up
        TvKeyAction.Down -> FindAnotherKey.Down
        TvKeyAction.Left -> FindAnotherKey.Left
        TvKeyAction.Right -> FindAnotherKey.Right
        TvKeyAction.Select -> FindAnotherKey.Select
        TvKeyAction.Back -> FindAnotherKey.Back
        else -> null
    }

    /**
     * While the Find another window is open the arrows, Select and Back drive it (and nothing else sees those keys). Otherwise
     * the contract's key map decides: see [TvKeyMap] and [TvPanelKeys]. A key the player does not use goes on to the system.
     */
    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        val action = TvRemoteKeys.actionFor(event.keyCode) ?: return super.dispatchKeyEvent(event)
        val down = event.action == KeyEvent.ACTION_DOWN
        val model = findModel
        if (model != null) {
            val key = findKeyOf(action) ?: return super.dispatchKeyEvent(event)
            // Select acts once per press, never on auto-repeat; the arrows repeat so a held key keeps moving.
            if (down && (key != FindAnotherKey.Select || event.repeatCount == 0)) model.press(key)
            return true
        }
        if (!::player.isInitialized || ui.status != null) {
            // Nothing is playing yet (or it could not start): Back leaves, everything else is ignored.
            if (action == TvKeyAction.Back) { if (down && event.repeatCount == 0) finish(); return true }
            return super.dispatchKeyEvent(event)
        }
        if (down) {
            val repeats = event.repeatCount > 0
            val holdable = action == TvKeyAction.Up || action == TvKeyAction.Down || action == TvKeyAction.Left || action == TvKeyAction.Right ||
                action == TvKeyAction.Rewind || action == TvKeyAction.FastForward
            if (!repeats || holdable) onKey(action)
        }
        return true
    }

    private fun onKey(action: TvKeyAction) {
        // Any key shows the overlay and restarts its 5 seconds.
        val wasVisible = overlayVisible || isAudioKind()
        if (action != TvKeyAction.Back || wasVisible) { overlayVisible = true; scheduleHide() }
        val nested = panelId?.let { TvPlayerLogic.parentOf(it) != null } ?: false
        val state = TvKeyState(
            overlayVisible = wasVisible, panelOpen = panelId != null, panelNested = nested,
            endCard = endShownAt > 0L && nextEpisode() != null, zone = focus.zone,
        )
        val result = TvKeyMap.effect(state, action)
        when (result.effect) {
            TvKeyEffect.Move -> moveOrPress(action)
            TvKeyEffect.ShowOverlay -> Unit
            TvKeyEffect.HideOverlay -> if (isAudioKind()) finish() else { overlayVisible = false; hideJob?.cancel() }
            TvKeyEffect.ClosePanel -> closePanel()
            TvKeyEffect.PanelBack -> panelBack()
            TvKeyEffect.SkipBack -> seekBy(-skip().back)
            TvKeyEffect.SkipForward -> seekBy(skip().forward)
            TvKeyEffect.TogglePlay -> player.playWhenReady = !player.playWhenReady
            TvKeyEffect.Play -> player.playWhenReady = true
            TvKeyEffect.Pause -> player.playWhenReady = false
            TvKeyEffect.CancelEndCard -> cancelEndCard()
            TvKeyEffect.PlayNext -> playNext()
            TvKeyEffect.ClosePlayer -> finish()
            TvKeyEffect.OpenAudioSubtitles -> if (kind != TvMediaKind.Music && kind != TvMediaKind.Audiobook) openPanel(TvPanelId.AudioSubtitles)
        }
        if (!isFinishing) rebuild()
    }

    /** Back from the system (a gesture or a key the activity did not see): the same order as the Back key. */
    private fun handleBack() {
        if (findModel != null) { findModel?.press(FindAnotherKey.Back); return }
        if (!::player.isInitialized || ui.status != null) { finish(); return }
        onKey(TvKeyAction.Back)
    }

    /** The arrows and Select when the key map leaves them to the screen: moving focus and pressing the focused control. */
    private fun moveOrPress(action: TvKeyAction) {
        if (endShownAt > 0L && nextEpisode() != null) {
            if (action == TvKeyAction.Select) {
                when (TvPlayerLogic.endCardEffect(endFocus)) {
                    TvKeyEffect.CancelEndCard -> cancelEndCard()
                    else -> playNext()
                }
            } else {
                endFocus = TvPlayerLogic.endCardFocus(endFocus, action)
            }
            return
        }
        if (focus.zone == TvZone.Panel && panelId != null) { onPanelKey(action); return }
        if (action == TvKeyAction.Select) { pressFocused(); return }
        focus = TvControlsFocus.move(focus.copy(panelOpen = panelId != null), action, ui.transport.size, ui.tools.size)
        if (focus.zone == TvZone.Panel) {
            panelFocus = TvPanelFocus(currentPanelNow()?.initialIndex() ?: 0)
        }
    }

    private fun currentPanelNow(): TvPanel? = panelId?.let { currentPanel(it, positionSeconds(), durationSeconds()) }

    private fun seekBy(deltaSeconds: Int) {
        val duration = durationSeconds()
        val target = player.currentPosition + deltaSeconds * 1000L
        player.seekTo(if (duration != null) target.coerceIn(0L, (duration * 1000).toLong()) else target.coerceAtLeast(0L))
    }

    private fun pressFocused() {
        when (focus.zone) {
            TvZone.Timeline -> player.playWhenReady = !player.playWhenReady
            TvZone.Tools -> ui.tools.getOrNull(focus.toolIndex)?.let { tool -> TvPlayerLogic.panelOf(tool.key)?.let { openPanel(it) } }
            else -> ui.transport.getOrNull(focus.transportIndex)?.let { pressControl(it.key, it.dimmed) }
        }
    }

    private fun pressControl(key: String, dimmed: Boolean) {
        // A dimmed control is dimmed in place and does nothing when pressed.
        if (dimmed) return
        when (key) {
            "play-pause" -> player.playWhenReady = !player.playWhenReady
            "skip-back" -> seekBy(-skip().back)
            "skip-forward" -> seekBy(skip().forward)
            "previous" -> previous()
            "next" -> next()
            "shuffle" -> player.shuffleModeEnabled = !player.shuffleModeEnabled
            "repeat" -> player.repeatMode = when (player.repeatMode) {
                Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
                Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
                else -> Player.REPEAT_MODE_OFF
            }
        }
    }

    private fun previous() {
        when (kind) {
            TvMediaKind.Episode -> TvEpisodes.neighbours(seasons, assetId).previous?.let { switchEpisode(it) }
            TvMediaKind.Audiobook -> TvChapters.previousStart(chapters, positionSeconds())?.let { player.seekTo((it * 1000).toLong()) }
            // Previous in music restarts a song after 3 seconds of play; there is no queue on a television to go back through.
            TvMediaKind.Music -> if (TvPlayerLogic.musicPreviousRestarts(positionSeconds())) player.seekTo(0L)
            else -> Unit
        }
    }

    private fun next() {
        when (kind) {
            TvMediaKind.Episode -> playNext()
            TvMediaKind.Audiobook -> TvChapters.nextStart(chapters, positionSeconds())?.let { player.seekTo((it * 1000).toLong()) }
            else -> Unit
        }
    }

    // ---- the side panels --------------------------------------------------------------------------------------------------

    private fun openPanel(id: TvPanelId) {
        if (panelId == id) { focus = focus.copy(zone = TvZone.Panel); return }
        val previous = panelId
        if (previous == TvPanelId.Lyrics) closeLyrics()
        panelId = id
        when (id) {
            TvPanelId.Episodes -> seasonIndex = TvEpisodes.seasonIndexOf(seasons, assetId)
            TvPanelId.Bookmarks -> { bmTab = TvBookmarkTab.Mine; bmView = bmView.copy(error = false, confirmDeleteId = null); loadBookmarks() }
            TvPanelId.Lyrics -> openLyrics()
            else -> Unit
        }
        panelFocus = TvPanelFocus(currentPanelNow()?.initialIndex() ?: 0)
        focus = focus.copy(zone = TvZone.Panel, panelOpen = true)
        overlayVisible = true
        hideJob?.cancel()
    }

    /** Back closes the panel (or one level of it) first; closing a panel never changes playback. */
    private fun closePanel() {
        if (panelId == TvPanelId.Lyrics) closeLyrics()
        panelId = null
        focus = focus.copy(zone = TvZone.Tools, panelOpen = false)
        scheduleHide()
    }

    private fun panelBack() {
        val parent = panelId?.let { TvPlayerLogic.parentOf(it) } ?: return closePanel()
        panelId = parent
        panelFocus = TvPanelFocus(currentPanelNow()?.initialIndex() ?: 0)
    }

    private fun onPanelKey(action: TvKeyAction) {
        val panel = currentPanelNow() ?: return
        val (next, effect) = TvPanelKeys.reduce(panel, panelFocus, action)
        panelFocus = next
        when (effect) {
            is TvPanelEffect.Activate -> activate(effect.key, effect.action)
            is TvPanelEffect.Choose -> choose(effect.segmentKey, effect.optionKey)
            is TvPanelEffect.Adjust -> adjust(effect.key, effect.delta)
            TvPanelEffect.Leave -> focus = TvControlsFocus.leavePanel(focus)
            TvPanelEffect.None -> Unit
        }
    }

    private fun saveDevice(next: TvDeviceChoices) {
        device = next
        store.save(next)
    }

    private fun activate(key: String, action: String?) {
        when (key) {
            TvPanelKeys.HEADER_CLOSE -> { closePanel(); return }
            TvPanelKeys.HEADER_BACK -> { panelBack(); return }
        }
        when (panelId) {
            TvPanelId.Episodes -> when {
                key == "season" -> if (seasons.isNotEmpty()) seasonIndex = (seasonIndex + 1) % seasons.size
                key == "autoplay" -> saveDevice(device.copy(autoplay = !autoplay()))
                key.startsWith("ep:") -> TvEpisodes.find(seasons, key.removePrefix("ep:"))?.let { switchEpisode(it) }
            }
            TvPanelId.AudioSubtitles -> when {
                key == "sub:off" -> player.trackSelectionParameters = player.trackSelectionParameters.buildUpon()
                    .setTrackTypeDisabled(C.TRACK_TYPE_TEXT, true)
                    .clearOverridesOfType(C.TRACK_TYPE_TEXT)
                    .build()
                key.startsWith("sub:") -> selectTrack(key.removePrefix("sub:"), C.TRACK_TYPE_TEXT)
                key.startsWith("audio:") -> selectTrack(key.removePrefix("audio:"), C.TRACK_TYPE_AUDIO)
                key == "appearance" -> { panelId = TvPanelId.SubtitleAppearance; panelFocus = TvPanelFocus(currentPanelNow()?.initialIndex() ?: 0) }
                key == "find" -> openFind(TextTrackKind.Subtitles)
            }
            TvPanelId.SubtitleAppearance -> {
                when {
                    key.startsWith("style:") -> saveDevice(device.copy(subtitleStyle = key.removePrefix("style:")))
                    key.startsWith("background:") -> saveDevice(device.copy(subtitleBackground = key.removePrefix("background:")))
                    key == "reset-appearance" -> saveDevice(device.copy(subtitleStyle = null, subtitleBackground = null))
                }
                applySubtitleAppearance()
            }
            TvPanelId.Bookmarks -> when {
                key == "add" -> addBookmark()
                key == "ticks" -> saveDevice(device.copy(showBookmarksOnTimeline = !device.showBookmarksOnTimeline))
                key.startsWith("bm:") -> {
                    val id = key.removePrefix("bm:")
                    when (action) {
                        null -> bookmarks.firstOrNull { it.id == id }?.let { player.seekTo((it.positionSeconds * 1000).toLong()) }
                        "delete" -> bmView = bmView.copy(confirmDeleteId = id)
                        "delete-keep" -> bmView = bmView.copy(confirmDeleteId = null)
                        "delete-yes" -> deleteBookmark(id)
                    }
                }
            }
            TvPanelId.PlaybackSettings -> when (key) {
                "speed" -> panelId = TvPanelId.Speed
                "skip-back" -> panelId = TvPanelId.SkipBack
                "skip-forward" -> panelId = TvPanelId.SkipForward
                "info" -> panelId = TvPanelId.PlaybackInformation
                "reset-preferences" -> {
                    saveDevice(TvDeviceChoices(showBookmarksOnTimeline = device.showBookmarksOnTimeline))
                    player.setPlaybackSpeed(preferences.defaultPlaybackSpeed.toFloat().coerceIn(0.5f, 3f))
                    applySubtitleAppearance()
                }
            }
            TvPanelId.Speed -> when {
                key.startsWith("preset:") -> key.removePrefix("preset:").toDoubleOrNull()?.let { setSpeed(it) }
                key == "reset-speed" -> setSpeed(1.0)
            }
            TvPanelId.SkipBack, TvPanelId.SkipForward -> key.removePrefix("seconds:").toIntOrNull()?.let { seconds ->
                saveDevice(if (panelId == TvPanelId.SkipBack) device.copy(skipBack = seconds) else device.copy(skipForward = seconds))
                panelBack()
            }
            TvPanelId.Chapters -> if (key.startsWith("ch:")) {
                chapters.getOrNull(key.removePrefix("ch:").toIntOrNull() ?: -1)?.let { player.seekTo((it.startSeconds * 1000).toLong()) }
            }
            TvPanelId.Lyrics -> if (key == "find") { if (canFindLyrics) openFind(TextTrackKind.Lyrics) }
            TvPanelId.PlaybackInformation, null -> Unit
        }
        // A nested view that changed panel starts on its own first row.
        if (panelId != null && currentPanelNow()?.id != null) panelFocus = panelFocus.copy(index = panelFocus.index.coerceAtLeast(-1))
    }

    private fun choose(segmentKey: String, optionKey: String) {
        when (segmentKey) {
            "tab" -> bmTab = TvBookmarkTab.fromKey(optionKey)
            "timeline" -> saveDevice(device.copy(timelineWholeBook = optionKey == "book"))
        }
    }

    private fun adjust(key: String, delta: Int) {
        when (key) {
            "speed-fine" -> setSpeed(TvPlayerLogic.stepSpeed(speed(), delta))
            "season" -> if (seasons.isNotEmpty()) seasonIndex = (seasonIndex + delta).coerceIn(0, seasons.size - 1)
        }
    }

    private fun setSpeed(value: Double) {
        val speed = TvPlayerLogic.clampSpeed(value)
        player.setPlaybackSpeed(speed.toFloat())
        saveDevice(device.copy(speed = speed))
    }

    // ---- tracks and subtitle appearance -----------------------------------------------------------------------------------

    private fun languageName(format: Format, number: Int): String =
        format.language?.takeIf { it.isNotBlank() && !it.equals("und", ignoreCase = true) }?.let { FindAnotherLanguages.nameFor(it) }
            ?: format.label?.takeIf { it.isNotBlank() }
            ?: "Track $number"

    private fun audioTracks(): List<TvAudioTrack> = buildList {
        player.currentTracks.groups.forEachIndexed { groupIndex, group ->
            if (group.type != C.TRACK_TYPE_AUDIO) return@forEachIndexed
            for (trackIndex in 0 until group.length) {
                if (!group.isTrackSupported(trackIndex)) continue
                val format = group.getTrackFormat(trackIndex)
                add(
                    TvAudioTrack(
                        id = "$groupIndex:$trackIndex",
                        language = languageName(format, size + 1),
                        mimeType = format.sampleMimeType,
                        channels = format.channelCount.takeIf { it > 0 } ?: 0,
                        selected = group.isTrackSelected(trackIndex),
                        commentary = format.roleFlags and C.ROLE_FLAG_COMMENTARY != 0,
                        description = format.roleFlags and C.ROLE_FLAG_DESCRIBES_VIDEO != 0,
                    ),
                )
            }
        }
    }

    private fun subtitleTracks(): List<TvSubtitleTrack> = buildList {
        player.currentTracks.groups.forEachIndexed { groupIndex, group ->
            if (group.type != C.TRACK_TYPE_TEXT) return@forEachIndexed
            for (trackIndex in 0 until group.length) {
                if (!group.isTrackSupported(trackIndex)) continue
                val format = group.getTrackFormat(trackIndex)
                add(
                    TvSubtitleTrack(
                        id = "$groupIndex:$trackIndex",
                        language = languageName(format, size + 1),
                        selected = group.isTrackSelected(trackIndex),
                        hearingImpaired = format.roleFlags and (C.ROLE_FLAG_CAPTION or C.ROLE_FLAG_DESCRIBES_MUSIC_AND_SOUND) != 0,
                        forced = format.selectionFlags and C.SELECTION_FLAG_FORCED != 0,
                    ),
                )
            }
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

    /** Subtitle appearance (Style and Background) is the device's own choice; Large text uses the large size step. */
    @OptIn(UnstableApi::class)
    private fun applySubtitleAppearance() {
        val view = playerView.subtitleView ?: return
        val appearance = TvSubtitleAppearance.of(preferences, device)
        val contrast = appearance.style == TvPlayerWords.STYLE_CONTRAST
        val background = when {
            contrast || appearance.background == TvPlayerWords.BACKGROUND_BOX -> if (contrast) AndroidColor.BLACK else 0x99000000.toInt()
            else -> AndroidColor.TRANSPARENT
        }
        val edge = when {
            contrast && appearance.background != TvPlayerWords.BACKGROUND_BOX -> CaptionStyleCompat.EDGE_TYPE_OUTLINE
            appearance.background == TvPlayerWords.BACKGROUND_SHADOW -> CaptionStyleCompat.EDGE_TYPE_DROP_SHADOW
            else -> CaptionStyleCompat.EDGE_TYPE_NONE
        }
        view.setStyle(CaptionStyleCompat(AndroidColor.WHITE, background, AndroidColor.TRANSPARENT, edge, AndroidColor.BLACK, null))
        view.setFractionalTextSize(TvSubtitleAppearance.sizeFraction(appearance, preferences.subtitleSize))
    }

    // ---- bookmarks --------------------------------------------------------------------------------------------------------

    private fun loadBookmarks() {
        val work = workId ?: return
        lifecycleScope.launch {
            try {
                bookmarks = client.bookmarks(work)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                // A list that cannot be read shows as empty; the Engine decides what this profile may see.
                Log.w(TAG, "The bookmarks could not be read", error)
                bookmarks = emptyList()
            }
            bmView = bmView.copy(loaded = true)
            rebuild()
        }
    }

    /** "Bookmark at {0}": saves at the moment it was pressed, at once, with no note. */
    private fun addBookmark() {
        val work = workId ?: return
        val body = TvBookmarks.createBody(assetId, positionSeconds(), durationSeconds())
        bmView = bmView.copy(error = false)
        lifecycleScope.launch {
            try {
                bookmarks = bookmarks + client.createBookmark(work, body)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The bookmark could not be saved", error)
                bmView = bmView.copy(error = true)
            }
            rebuild()
        }
    }

    private fun deleteBookmark(id: String) {
        bmView = bmView.copy(confirmDeleteId = null, error = false)
        lifecycleScope.launch {
            try {
                val status = client.deleteBookmark(id)
                if (status in 200..299) bookmarks = bookmarks.filter { it.id != id } else bmView = bmView.copy(error = true)
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The bookmark could not be deleted", error)
                bmView = bmView.copy(error = true)
            }
            rebuild()
        }
    }

    // ---- the Lyrics panel -------------------------------------------------------------------------------------------------

    private fun openLyrics() {
        canFindLyrics = false
        loadLyrics()
        startLyricsFollow()
        // Asked from settings alone: with no lyrics source switched on (or a profile that may not search) the footer row stays hidden.
        lifecycleScope.launch {
            val ready = client.textTrackSources(assetId, TextTrackKind.Lyrics).isReady
            if (panelId == TvPanelId.Lyrics) { canFindLyrics = ready; rebuild() }
        }
    }

    private fun closeLyrics() {
        canFindLyrics = false
        // A read still on its way is dropped; the follow loop stops.
        lyricsGeneration++
        lyricsFollowJob?.cancel()
        lyricsFollowJob = null
    }

    /** Reads the song's lyrics file (the Engine's preferred lyrics track) into lines. A newer read or closing the panel drops an older one. */
    private fun loadLyrics(note: String? = null) {
        val mine = ++lyricsGeneration
        lyrics = LyricsView.loading(note)
        lyricsIndex = -1
        lifecycleScope.launch {
            val next = try {
                val pick = LyricsFollow.pickTrack(client.lyricsTracks(assetId))
                if (pick == null) {
                    LyricsView.None
                } else {
                    val parsed = LyricsFollow.parse(client.textTrackText(assetId, pick.id))
                    if (parsed.lines.isEmpty()) LyricsView.None else LyricsView.ready(parsed)
                }
            } catch (error: CancellationException) {
                throw error
            } catch (error: Exception) {
                Log.w(TAG, "The lyrics could not be read", error)
                LyricsView.Failed
            }
            if (mine != lyricsGeneration) return@launch
            lyrics = next
        }
    }

    /** While the panel is open the current line follows the song's position. */
    private fun startLyricsFollow() {
        lyricsFollowJob?.cancel()
        lyricsFollowJob = lifecycleScope.launch {
            while (isActive) {
                val parsed = lyrics.parsed
                val index = if (parsed == null) -1 else LyricsFollow.currentIndex(parsed.lines, positionSeconds())
                if (index != lyricsIndex) { lyricsIndex = index; rebuild() }
                delay(250)
            }
        }
    }

    // ---- the Find another window ------------------------------------------------------------------------------------------

    private fun openFind(kind: TextTrackKind) {
        val current = manifest ?: return
        findKind = kind
        // The video stays paused behind the window; closing the window puts it back as it was.
        resumeAfterFind = player.playWhenReady
        player.pause()
        // Lyrics are searched in the song's own language: no Language button and no language sent.
        val lyricsKind = kind == TextTrackKind.Lyrics
        val languages = if (lyricsKind) emptyList<String>() else FindAnotherLanguages.choices(
            preferred = preferences.subtitleLanguage,
            others = listOf(preferences.audioLanguage) + current.subtitleTracks.map { it.language },
        )
        findLanguages = languages
        val model = FindAnotherModel(
            api = client.findAnotherApi(assetId, kind),
            scope = lifecycleScope,
            scheduler = CoroutineDwellScheduler(lifecycleScope),
            kind = kind,
            initialLanguage = if (lyricsKind) "" else FindAnotherLanguages.nameFor(preferences.subtitleLanguage),
            languageChoices = languages,
            itemNoun = if (lyricsKind) "song" else "title",
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
        scheduleHide()
        rebuild()
    }

    /** Use this worked: close the window and the panel, then play on from the same place with the new subtitles. */
    private fun onFindApplied(result: TextTrackChoiceResult) {
        findModel?.close()
        findModel = null
        findState = null
        if (findKind == TextTrackKind.Lyrics) {
            // The Lyrics panel stays open and reads the new lyrics ("Updating lyrics…"); the song carries on at the same place.
            player.playWhenReady = resumeAfterFind
            loadLyrics(LyricsFollow.UPDATING)
            return
        }
        panelId = null
        focus = focus.copy(zone = TvZone.Tools, panelOpen = false)
        lifecycleScope.launch { reloadWithNewSubtitles() }
    }

    @OptIn(UnstableApi::class)
    private suspend fun reloadWithNewSubtitles() {
        val position = player.currentPosition
        val fresh = try {
            client.playbackManifest(assetId, "local")
        } catch (error: CancellationException) {
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
        rebuild()
    }

    override fun onStop() {
        if (::player.isInitialized) {
            player.pause()
            sendHeartbeat()
        }
        super.onStop()
    }

    override fun onDestroy() {
        findModel?.close()
        tickJob?.cancel()
        if (::player.isInitialized) player.release()
        super.onDestroy()
    }

    companion object {
        const val ASSET_ID = "asset_id"

        /** The item's name for the player's title and the Find another window's header (optional). */
        const val TITLE = "title"

        /** The detail page the player was started from (its type, such as `tvShow` or `movie`, and id); optional. */
        const val ENTITY_TYPE = "entity_type"
        const val ENTITY_ID = "entity_id"

        private const val TICK_MS = 500L
        private const val TAG = "PlaybackActivity"
    }
}
