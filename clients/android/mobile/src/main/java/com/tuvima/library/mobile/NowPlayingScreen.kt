package com.tuvima.library.mobile

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.disabled
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.AudioNow
import com.tuvima.library.core.NowPlayingLogic
import com.tuvima.library.core.PhoneBookmarks
import com.tuvima.library.core.PhonePlayerLogic
import com.tuvima.library.core.PhoneTool
import com.tuvima.library.core.RepeatCycle
import com.tuvima.library.core.SavedKey
import com.tuvima.library.core.SavedKind
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TvControl
import com.tuvima.library.core.TvMediaKind
import com.tuvima.library.core.TvPanels
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvPlayerWords
import com.tuvima.library.core.TvRepeatMode
import com.tuvima.library.core.TvRow
import kotlinx.coroutines.delay

// The phone's music and audiobook players (spec native-apps WP10c, the contract's Phone section): the collapsed 56 dp bar above the dock, and the
// full-screen Now playing view with a top line (collapse, "Playing from ...", the "..." menu), the cover, the title line (with the Favorite heart for a
// song), the timeline, the transport with a large Play, and one row of at most four tools. There is no volume control: the phone's own buttons set the
// volume. All rules come from `core` (`PhonePlayerLogic`, `NowPlayingLogic`); these composables draw them. Dark only, controls at least 44 dp,
// icon-only buttons are bare, no toasts.

private val MutedText = TuvimaTokens.textMuted
private val Accent = TuvimaTokens.accentPrimaryHover

private enum class AudioSheet { None, Queue, Lyrics, Chapters, Speed, SleepTimer, Bookmarks, Settings, SkipBack, SkipForward }

// ---- the collapsed bar ----

/**
 * The 56 dp bar: the cover, the title and the second line, then the contract's buttons (Play and Next track for music, Play and Skip forward for an
 * audiobook). A press on the bar opens Now playing. It also carries the words of a song or book that could not be played, as a line, never a toast.
 */
@Composable
internal fun MiniPlayer(host: AudioPlayerHost, client: TuvimaClient, onOpen: () -> Unit) {
    val now by AudioBridge.now.collectAsState()
    val problem by AudioBridge.problem.collectAsState()
    val skip by AudioBridge.skip.collectAsState()
    LaunchedEffect(now.assetId) {
        if (now.isNone) return@LaunchedEffect
        while (true) {
            host.tick()
            delay(MINI_TICK_MS)
        }
    }
    LaunchedEffect(problem) {
        if (problem != null) {
            delay(PROBLEM_SHOWN_MS)
            AudioBridge.problem.value = null
        }
    }
    Column(Modifier.fillMaxWidth()) {
        problem?.let { words ->
            Text(
                words,
                color = TuvimaTokens.textPrimary,
                style = MaterialTheme.typography.bodySmall,
                modifier = Modifier.fillMaxWidth().background(TuvimaTokens.bgSurfaceElevated).padding(horizontal = 16.dp, vertical = 8.dp),
            )
        }
        if (NowPlayingLogic.showsMiniPlayer(now)) {
            val canNext = NowPlayingLogic.hasNextSong(host.currentIndex, host.queue.size, host.repeat)
            val buttons = PhonePlayerLogic.bar(now.kind, host.isPlaying, skip, canNext)
            val fraction = if (host.durationMs > 0) (host.positionMs.toFloat() / host.durationMs).coerceIn(0f, 1f) else 0f
            Column(Modifier.fillMaxWidth().background(TuvimaTokens.bgSurfaceElevated)) {
                Row(
                    Modifier
                        .fillMaxWidth()
                        .height(PhonePlayerLogic.BAR_HEIGHT_DP.dp)
                        .clickable(onClickLabel = NowPlayingLogic.OPEN_NOW_PLAYING, onClick = onOpen),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RemoteImage(client, now.artworkUrl, Modifier.size(PhonePlayerLogic.BAR_HEIGHT_DP.dp))
                    Column(Modifier.weight(1f).padding(horizontal = 12.dp)) {
                        Text(now.title, color = TuvimaTokens.textPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        NowPlayingLogic.barLine(now, host.positionSeconds())?.let {
                            Text(it, color = MutedText, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                    buttons.forEach { control ->
                        TransportGlyphButton(control, skip.back, skip.forward, size = 44) {
                            when (control.key) {
                                "play-pause" -> host.togglePlay()
                                "next" -> host.next(now)
                                "skip-forward" -> host.skipBy(skip.forward)
                            }
                        }
                    }
                }
                Box(Modifier.fillMaxWidth(fraction).height(2.dp).background(Accent))
            }
        }
    }
}

/** One transport button drawn as a glyph in a bare 44 dp target (more for Play). A dimmed button stays in place and does nothing. */
@Composable
private fun TransportGlyphButton(control: TvControl, skipBack: Int, skipForward: Int, size: Int, onClick: () -> Unit) {
    val isPlay = control.key == "play-pause"
    val tint = if (control.dimmed) MutedText.copy(alpha = 0.5f) else TuvimaTokens.textPrimary
    val glyph = when (control.key) {
        "play-pause" -> if (control.label == TvPlayerWords.PAUSE) "❚❚" else "▶"
        "previous" -> "⏮"
        "next" -> "⏭"
        "skip-back" -> "-${control.seconds ?: skipBack}"
        "skip-forward" -> "+${control.seconds ?: skipForward}"
        else -> control.label
    }
    Box(
        Modifier
            .size(size.dp)
            .clip(CircleShape)
            .then(if (isPlay && size > 48) Modifier.background(TuvimaTokens.accentPrimary, CircleShape) else Modifier)
            .clickable(enabled = !control.dimmed, onClickLabel = control.label, onClick = onClick)
            .semantics {
                contentDescription = control.label
                if (control.dimmed) disabled()
            },
        contentAlignment = Alignment.Center,
    ) {
        Text(glyph, color = if (isPlay && size > 48) TuvimaTokens.textOnAccent else tint, fontSize = if (isPlay) 24.sp else 17.sp)
    }
}

// ---- the full-screen Now playing ----

/**
 * Now playing for a song or an audiobook, over the whole app. Collapse (or Back) returns to the page underneath and playback carries on; nothing
 * here stops the player.
 */
@Composable
internal fun NowPlayingScreen(host: AudioPlayerHost, client: TuvimaClient, onCollapse: () -> Unit) {
    BackHandler(onBack = onCollapse)
    val now by AudioBridge.now.collectAsState()
    val timer by AudioBridge.sleepTimer.collectAsState()
    val shuffle by AudioBridge.shuffle.collectAsState()
    val skip by AudioBridge.skip.collectAsState()
    var sheet by remember { mutableStateOf(AudioSheet.None) }
    var menuOpen by remember { mutableStateOf(false) }
    var dragging by remember { mutableStateOf<Float?>(null) }

    if (now.isNone) {
        // Whatever was playing is gone (the media service stopped): there is nothing to show.
        LaunchedEffect(Unit) { onCollapse() }
        return
    }

    LaunchedEffect(now.assetId) {
        while (true) {
            host.tick()
            delay(NOW_TICK_MS)
        }
    }
    LaunchedEffect(now.assetId, now.workId) { host.bookmarks.load(now) }

    val positionSeconds = host.positionSeconds()
    val durationSeconds = host.durationSeconds()
    val device = host.device
    val canBookmark = host.bookmarks.workOf(now) != null
    val canPrevious = NowPlayingLogic.canPrevious(
        now.kind,
        positionSeconds,
        NowPlayingLogic.hasPreviousSong(host.currentIndex, host.queue.size, host.repeat),
        now.chapters,
    )
    val canNext = NowPlayingLogic.canNext(
        now.kind,
        positionSeconds,
        NowPlayingLogic.hasNextSong(host.currentIndex, host.queue.size, host.repeat),
        now.chapters,
    )
    val transport = PhonePlayerLogic.transport(now.kind, skip, host.isPlaying, canPrevious, canNext)
    val tools = PhonePlayerLogic.tools(
        kind = now.kind,
        hasChapters = now.chapters.isNotEmpty(),
        canBookmark = canBookmark,
        shuffleOn = shuffle,
        repeat = host.repeat,
        speed = host.speed.toDouble(),
    )
    val menu = PhonePlayerLogic.menu(now.kind)
    val timeline = NowPlayingLogic.timeline(now, positionSeconds, durationSeconds, device.timelineWholeBook)
    val wholeTimeline = !now.isAudiobook || now.chapters.isEmpty() || device.timelineWholeBook
    val ticks = if (wholeTimeline) PhonePlayerLogic.timelineTicks(host.bookmarks.items, durationSeconds, device.showBookmarksOnTimeline) else emptyList()
    val saved = LocalSavedItems.current

    Column(
        Modifier
            .fillMaxSize()
            .background(TuvimaTokens.bgRoot)
            .statusBarsPadding()
            .navigationBarsPadding(),
    ) {
        // The top line: collapse, "Playing from ...", and the "..." menu (only where it has a row: Playback settings for an audiobook).
        Row(Modifier.fillMaxWidth().height(56.dp).padding(horizontal = 4.dp), verticalAlignment = Alignment.CenterVertically) {
            BareIconButton("Collapse", onCollapse) { Text("⌄", color = TuvimaTokens.textPrimary, fontSize = 26.sp) }
            Text(
                NowPlayingLogic.playingFrom(now.sourceName) ?: "Now playing",
                color = MutedText,
                style = MaterialTheme.typography.labelLarge,
                textAlign = TextAlign.Center,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.weight(1f),
            )
            if (menu.isNotEmpty()) {
                Box {
                    BareIconButton("More actions", { menuOpen = true }) { Text("⋯", color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleLarge) }
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        menu.forEach { row ->
                            DropdownMenuItem(
                                text = { Text(row.label) },
                                onClick = {
                                    menuOpen = false
                                    if (row.key == "playback-settings") sheet = AudioSheet.Settings
                                },
                            )
                        }
                    }
                }
            } else {
                Spacer(Modifier.width(TuvimaTokens.tapTarget))
            }
        }

        BoxWithConstraints(Modifier.weight(1f).fillMaxWidth().padding(horizontal = 24.dp, vertical = 8.dp), contentAlignment = Alignment.Center) {
            val side = minOf(maxWidth, maxHeight)
            RemoteImage(client, now.artworkUrl, Modifier.size(side).clip(RoundedCornerShape(12.dp)))
        }

        // The title line: the song or book and its second line, with the Favorite heart for a song.
        Row(Modifier.fillMaxWidth().padding(start = 24.dp, end = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(now.title, color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleLarge, maxLines = 2, overflow = TextOverflow.Ellipsis)
                NowPlayingLogic.secondLine(now, positionSeconds)?.let {
                    Text(it, color = MutedText, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
            val heartKey = if (now.kind == TvMediaKind.Music) now.workId?.takeIf(String::isNotBlank)?.let { SavedKey.of(SavedKind.Song, it) } else null
            val titles = saved?.titles
            if (heartKey != null && saved != null && titles != null) {
                val hearted = heartKey in titles
                BareIconButton(if (hearted) "Remove from Favorites" else "Add to Favorites", { saved.toggle(heartKey) }) {
                    Text("♥".takeIf { hearted } ?: "♡", color = if (hearted) Accent else TuvimaTokens.textPrimary, fontSize = 24.sp)
                }
            }
        }

        // The timeline, with a tick for each bookmark when the person wants them shown, then the time played and the time left.
        Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp)) {
            val shown = dragging ?: timeline.fraction.toFloat()
            Slider(
                value = shown.coerceIn(0f, 1f),
                onValueChange = { dragging = it },
                onValueChangeFinished = {
                    dragging?.let { value ->
                        host.seekToSeconds(NowPlayingLogic.seekFromTimeline(now, value.toDouble(), positionSeconds, durationSeconds, device.timelineWholeBook))
                    }
                    dragging = null
                },
                enabled = durationSeconds != null,
                colors = SliderDefaults.colors(thumbColor = TuvimaTokens.textPrimary, activeTrackColor = TuvimaTokens.textPrimary),
                modifier = Modifier.fillMaxWidth().semantics {
                    contentDescription = "Seek bar"
                    stateDescription = "${timeline.elapsed} played"
                },
            )
            TickRow(ticks)
            Row(Modifier.fillMaxWidth().padding(horizontal = 4.dp), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(timeline.elapsed, color = MutedText, style = MaterialTheme.typography.bodySmall)
                Text(timeline.left, color = MutedText, style = MaterialTheme.typography.bodySmall)
            }
        }

        // The transport with a large Play: three buttons for a song, five for an audiobook.
        Row(
            Modifier.fillMaxWidth().padding(vertical = 8.dp),
            horizontalArrangement = Arrangement.SpaceEvenly,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            transport.forEach { control ->
                TransportGlyphButton(control, skip.back, skip.forward, size = if (control.key == "play-pause") 64 else 48) {
                    when (control.key) {
                        "play-pause" -> host.togglePlay()
                        "previous" -> host.previous(now)
                        "next" -> host.next(now)
                        "skip-back" -> host.skipBy(-skip.back)
                        "skip-forward" -> host.skipBy(skip.forward)
                    }
                }
            }
        }

        // One row of at most four tools. Shuffle and Repeat are lit while on; Playback speed shows the speed ("1.25×").
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp, vertical = 4.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
            tools.forEach { tool ->
                ToolButton(tool, host.repeat, Modifier.weight(1f)) {
                    when (tool.key) {
                        "shuffle" -> host.setShuffle(!shuffle)
                        "repeat" -> host.cycleRepeat()
                        "lyrics" -> sheet = AudioSheet.Lyrics
                        "queue" -> sheet = AudioSheet.Queue
                        "chapters" -> sheet = AudioSheet.Chapters
                        "speed" -> sheet = AudioSheet.Speed
                        "sleep-timer" -> sheet = AudioSheet.SleepTimer
                        "bookmarks" -> sheet = AudioSheet.Bookmarks
                    }
                }
            }
        }
        Spacer(Modifier.height(8.dp))
    }

    AudioSheets(host, client, now, sheet, timer, skip.back, skip.forward) { sheet = it }
}

@Composable
private fun ToolButton(tool: PhoneTool, repeat: TvRepeatMode, modifier: Modifier, onClick: () -> Unit) {
    val label = when {
        tool.key == "repeat" && repeat == TvRepeatMode.One -> "Repeat 1"
        tool.badge != null -> "${tool.label} ${tool.badge}"
        else -> tool.label
    }
    val description = if (tool.key == "repeat") RepeatCycle.label(repeat) else label
    TextButton(
        onClick = onClick,
        modifier = modifier.defaultMinSize(minHeight = 44.dp).semantics {
            contentDescription = description
            if (tool.key == "shuffle" || tool.key == "repeat") stateDescription = if (tool.selected) "On" else "Off"
        },
    ) {
        Text(
            label,
            color = if (tool.selected) Accent else TuvimaTokens.textPrimary,
            fontWeight = if (tool.selected) FontWeight.Bold else FontWeight.Normal,
            fontSize = 13.sp,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

/** A small tick under the timeline for each bookmark, where [ticks] are percentages of the length. */
@Composable
private fun TickRow(ticks: List<Double>) {
    if (ticks.isEmpty()) return
    BoxWithConstraints(Modifier.fillMaxWidth().padding(horizontal = 16.dp).height(6.dp)) {
        val total = maxWidth
        ticks.forEach { percent ->
            Box(Modifier.offset(x = total * (percent / 100.0).toFloat()).width(3.dp).height(6.dp).background(Accent))
        }
    }
}

// ---- the sheets one tool opens ----

/** The one sheet that is open (only one at a time; closing one never changes playback). */
@Composable
private fun AudioSheets(
    host: AudioPlayerHost,
    client: TuvimaClient,
    now: AudioNow,
    sheet: AudioSheet,
    timer: com.tuvima.library.core.SleepTimer,
    skipBack: Int,
    skipForward: Int,
    setSheet: (AudioSheet) -> Unit,
) {
    val close = { setSheet(AudioSheet.None) }
    val device = host.device
    val skip = com.tuvima.library.core.TvSkip(skipBack, skipForward)
    when (sheet) {
        AudioSheet.None -> Unit
        AudioSheet.Queue -> QueueSheet(
            client = client,
            tracks = host.queue,
            currentIndex = host.currentIndex,
            sourceName = now.sourceName,
            onPlay = { host.playIndex(it) },
            onAction = { action, index -> host.queueAction(action, index) },
            onClear = { host.clearUpcoming() },
            onClose = close,
        )
        AudioSheet.Lyrics -> LyricsSheet(
            client = client,
            assetId = now.assetId,
            isDownload = now.isDownload,
            positionMs = host.positionMs,
            onSeek = { host.seekToSeconds(it) },
            onClose = close,
        )
        AudioSheet.Chapters -> PanelSheet(
            client = client,
            panel = TvPanels.chapters(now.chapters, host.positionSeconds(), host.durationSeconds(), device.timelineWholeBook),
            onClose = close,
            onBack = close,
            onRow = { key, _ ->
                // A press on a chapter starts it; the sheet stays open so the person can see where they are.
                now.chapters.getOrNull(key.removePrefix("ch:").toIntOrNull() ?: -1)?.let { host.seekToSeconds(it.startSeconds) }
            },
            onChoose = { segment, option ->
                if (segment == "timeline") host.updateDevice { it.copy(timelineWholeBook = option == "book") }
            },
        )
        AudioSheet.Speed -> SpeedSheet(
            speed = host.speed.toDouble(),
            onSet = { host.setSpeed(it) },
            onClose = close,
            onBack = null,
        )
        AudioSheet.SleepTimer -> SleepTimerSheet(
            timer = timer,
            chapters = now.chapters,
            positionSeconds = { host.positionSeconds() },
            durationSeconds = { host.durationSeconds() },
            onSet = { AudioBridge.sleepTimer.value = it },
            onClose = close,
        )
        AudioSheet.Bookmarks -> BookmarksSheet(
            client = client,
            items = host.bookmarks.items,
            loaded = host.bookmarks.loaded,
            saveFailed = host.bookmarks.saveFailed,
            positionSeconds = host.positionSeconds(),
            showOnTimeline = device.showBookmarksOnTimeline,
            coverUrl = now.artworkUrl,
            onAdd = { note -> host.bookmarks.add(now, note, host.positionSeconds(), host.durationSeconds()) },
            onSeek = { host.seekToSeconds(it) },
            onEdit = { id, name, note -> host.bookmarks.rename(id, name, note) },
            onDelete = { id -> host.bookmarks.delete(id) },
            onToggleTimeline = { on -> host.updateDevice { it.copy(showBookmarksOnTimeline = on) } },
            onClose = close,
        )
        AudioSheet.Settings -> PanelSheet(
            client = client,
            // Playback information is a video panel; an audiobook's settings are its speed and skips.
            panel = TvPanels.playbackSettings(host.speed.toDouble(), skip).let { panel ->
                panel.copy(body = panel.body.filterNot { (it as? TvRow)?.key == "info" })
            },
            onClose = close,
            onBack = close,
            onRow = { key, _ ->
                when (key) {
                    "speed" -> setSheet(AudioSheet.Speed)
                    "skip-back" -> setSheet(AudioSheet.SkipBack)
                    "skip-forward" -> setSheet(AudioSheet.SkipForward)
                    "reset-preferences" -> {
                        host.setSpeed(1.0)
                        host.updateDevice {
                            it.copy(skipBack = null, skipForward = null, audiobookSpeed = null, timelineWholeBook = false, showBookmarksOnTimeline = true)
                        }
                    }
                }
            },
        )
        AudioSheet.SkipBack, AudioSheet.SkipForward -> {
            val forward = sheet == AudioSheet.SkipForward
            val choices = if (forward) PhonePlayerLogic.AUDIO_SKIP_FORWARD_CHOICES else PhonePlayerLogic.AUDIO_SKIP_BACK_CHOICES
            PanelSheet(
                client = client,
                // Audio offers fewer choices than video (10, 15, 30 back and 15, 30, 60 forward).
                panel = TvPanels.skipChoices(forward, if (forward) skipForward else skipBack).let { panel ->
                    panel.copy(body = panel.body.filter { (it as? TvRow)?.key?.removePrefix("seconds:")?.toIntOrNull() in choices })
                },
                onClose = close,
                onBack = { setSheet(AudioSheet.Settings) },
                onRow = { key, _ ->
                    key.removePrefix("seconds:").toIntOrNull()?.let { seconds ->
                        host.updateDevice { if (forward) it.copy(skipForward = seconds) else it.copy(skipBack = seconds) }
                    }
                },
            )
        }
    }
}

private const val MINI_TICK_MS = 1_000L
private const val NOW_TICK_MS = 500L
private const val PROBLEM_SHOWN_MS = 6_000L
