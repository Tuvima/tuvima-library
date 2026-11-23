package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.Chapter
import com.tuvima.library.core.CoroutineDwellScheduler
import com.tuvima.library.core.FindAnotherModel
import com.tuvima.library.core.FindAnotherSheet
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.FindAnotherWording
import com.tuvima.library.core.LyricsFollow
import com.tuvima.library.core.LyricsPhase
import com.tuvima.library.core.LyricsTrackInfo
import com.tuvima.library.core.LyricsView
import com.tuvima.library.core.NowPlayingLogic
import com.tuvima.library.core.QueueLogic
import com.tuvima.library.core.QueueRow
import com.tuvima.library.core.QueueRowAction
import com.tuvima.library.core.QueueTab
import com.tuvima.library.core.QueueTrack
import com.tuvima.library.core.SleepTimer
import com.tuvima.library.core.SleepTimerLogic
import com.tuvima.library.core.TextTrackKind
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.findAnotherApi
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

// The music and audiobook players' own panels (spec native-apps WP10c, the contract's Panels table): Queue, Lyrics and Sleep timer, as bottom
// sheets in the frame the video panels use (`PlayerSheet`). Every word and row comes from `core` (`QueueLogic`, `LyricsFollow`, `SleepTimerLogic`);
// these composables only draw them and report taps. Dark only, controls at least 44 dp, icon-only buttons are bare, no toasts.

private val MutedText = TuvimaTokens.textMuted
private val Accent = TuvimaTokens.accentPrimaryHover

// ---- Queue ----

/**
 * The Queue panel: "Up next · N" and "History" tabs, the Now playing row, the upcoming rows with a "..." menu each (Play next, Move up, Move down,
 * Remove), Clear (which never stops the song), and the footer "Playing from · {source}". A press on a row plays it.
 */
@Composable
internal fun QueueSheet(
    client: TuvimaClient?,
    tracks: List<QueueTrack>,
    currentIndex: Int,
    sourceName: String?,
    onPlay: (Int) -> Unit,
    onAction: (QueueRowAction, Int) -> Unit,
    onClear: () -> Unit,
    onClose: () -> Unit,
) {
    var tab by remember { mutableStateOf(QueueTab.UpNext) }
    val view = QueueLogic.view(tracks, currentIndex, tab, sourceName)
    PlayerSheet(
        title = "Queue",
        onClose = onClose,
        footer = {
            view.footer?.let { Text(it, color = MutedText, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) }
        },
    ) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            listOf(QueueTab.UpNext to view.upNextLabel, QueueTab.History to view.historyLabel).forEach { (value, label) ->
                if (tab == value) {
                    Button(onClick = { tab = value }, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(label) }
                } else {
                    OutlinedButton(onClick = { tab = value }, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(label) }
                }
            }
        }
        if (tab == QueueTab.UpNext) {
            view.nowPlaying?.let { now ->
                Text(QueueLogic.NOW_PLAYING, color = Accent, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 16.dp))
                QueueRowView(client, now, onPlay, onAction)
            }
            if (view.canClear) {
                TextButton(onClick = onClear, modifier = Modifier.padding(start = 8.dp).defaultMinSize(minHeight = 44.dp)) {
                    Text(QueueLogic.CLEAR, color = Accent)
                }
            }
        }
        view.rows.forEach { row -> QueueRowView(client, row, onPlay, onAction) }
    }
}

@Composable
private fun QueueRowView(client: TuvimaClient?, row: QueueRow, onPlay: (Int) -> Unit, onAction: (QueueRowAction, Int) -> Unit) {
    var menuOpen by remember(row.index) { mutableStateOf(false) }
    Row(
        Modifier
            .fillMaxWidth()
            .background(if (row.isCurrent) TuvimaTokens.bgSurfaceHover else androidx.compose.ui.graphics.Color.Transparent)
            .defaultMinSize(minHeight = 56.dp)
            .clickable(onClickLabel = "Play ${row.title}") { onPlay(row.index) }
            .padding(start = 16.dp, top = 6.dp, bottom = 6.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (client != null) {
            RemoteImage(client, row.artworkUrl, Modifier.width(44.dp).height(44.dp).clip(RoundedCornerShape(6.dp)))
            Spacer(Modifier.width(12.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(row.title, color = TuvimaTokens.textPrimary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            row.line?.let { Text(it, color = MutedText, style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis) }
        }
        if (row.actions.isNotEmpty()) {
            Box {
                BareIconButton("More actions for ${row.title}", { menuOpen = true }) {
                    Text("⋯", color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleLarge)
                }
                DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                    row.actions.forEach { action ->
                        DropdownMenuItem(
                            text = { Text(action.label) },
                            onClick = {
                                menuOpen = false
                                onAction(action, row.index)
                            },
                        )
                    }
                }
            }
        } else {
            Spacer(Modifier.width(TuvimaTokens.tapTarget))
        }
    }
}

// ---- Lyrics ----

/**
 * The Lyrics panel for a song. Synced lyrics follow playback and a line selected jumps to its moment; a hand scroll stops the following and shows
 * "Resume following"; unsynced lyrics are plain words with no jumping. "Find lyrics…" (when a lyrics source is switched on and the song is not a
 * download) opens the same Find another sheet the subtitles use.
 */
@Composable
internal fun LyricsSheet(
    client: TuvimaClient?,
    assetId: String,
    isDownload: Boolean,
    positionMs: Long,
    onSeek: (Double) -> Unit,
    onClose: () -> Unit,
) {
    var reload by remember { mutableIntStateOf(0) }
    var view by remember { mutableStateOf(LyricsView.loading()) }
    var canFind by remember { mutableStateOf(false) }
    var findState by remember { mutableStateOf<FindAnotherState?>(null) }
    var findModel by remember { mutableStateOf<FindAnotherModel?>(null) }
    val scope = rememberCoroutineScope()

    LaunchedEffect(assetId, reload) {
        view = if (reload > 0) LyricsView.loading(LyricsFollow.UPDATING) else LyricsView.loading()
        view = try {
            val api = client ?: throw java.io.IOException("No connection")
            val tracks: List<LyricsTrackInfo> = api.lyricsTracks(assetId)
            val track = LyricsFollow.pickTrack(tracks)
            if (track == null) LyricsView.None else LyricsView.ready(LyricsFollow.parse(api.textTrackText(assetId, track.id)))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            LyricsView.Failed
        }
    }
    // "Find lyrics…" is asked from settings alone: with no lyrics source switched on (or a downloaded copy) the row stays hidden.
    LaunchedEffect(assetId) {
        canFind = false
        val api = client ?: return@LaunchedEffect
        canFind = try {
            FindAnotherSheet.entryVisible(api.textTrackSources(assetId, TextTrackKind.Lyrics), isDownload)
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            // Any failed answer keeps the row hidden.
            false
        }
    }

    fun closeFind() {
        findModel?.close()
        findModel = null
        findState = null
    }

    fun openFind() {
        val api = client ?: return
        if (findModel != null) return
        // Lyrics are searched in the song's own language: no Language button and no language sent.
        val model = FindAnotherModel(
            api = api.findAnotherApi(assetId, TextTrackKind.Lyrics),
            scope = scope,
            scheduler = CoroutineDwellScheduler(scope),
            kind = TextTrackKind.Lyrics,
            initialLanguage = "",
            languageChoices = emptyList(),
            itemNoun = "song",
            onChange = { findState = it },
            onApplied = {
                closeFind()
                reload += 1
            },
            onClosed = { closeFind() },
        )
        findModel = model
        findState = model.state
        model.start()
    }

    PlayerSheet(
        title = LyricsFollow.HEADING,
        onClose = onClose,
        scroll = false,
        footer = {
            if (canFind) {
                TextButton(onClick = { openFind() }, modifier = Modifier.padding(start = 8.dp).defaultMinSize(minHeight = 44.dp)) {
                    Text(FindAnotherWording.entryLabel(TextTrackKind.Lyrics), color = Accent)
                }
            }
        },
    ) {
        when (view.phase) {
            LyricsPhase.Loading -> Text(view.note ?: LyricsFollow.LOOKING, color = MutedText, modifier = Modifier.padding(16.dp))
            LyricsPhase.None -> Text(LyricsFollow.NO_LYRICS, color = MutedText, modifier = Modifier.padding(16.dp))
            LyricsPhase.Failed -> Text(LyricsFollow.NOT_LOADED, color = MutedText, modifier = Modifier.padding(16.dp))
            LyricsPhase.Ready -> view.parsed?.let { parsed -> LyricsBody(parsed, positionMs, onSeek) }
        }
    }

    findState?.let { state ->
        FindAnotherBottomSheet(
            state = state,
            itemName = null,
            languageChoices = emptyList(),
            onTapRow = { findModel?.tapRow(it) },
            onUseThis = { findModel?.useSelected() },
            onCancel = { findModel?.cancel() },
            onChooseLanguage = { findModel?.chooseLanguage(it) },
            onDismiss = { closeFind() },
        )
    }
}

@Composable
private fun androidx.compose.foundation.layout.ColumnScope.LyricsBody(
    parsed: com.tuvima.library.core.ParsedLyrics,
    positionMs: Long,
    onSeek: (Double) -> Unit,
) {
    val listState = rememberLazyListState()
    var following by remember { mutableStateOf(true) }
    var programmatic by remember { mutableStateOf(false) }
    val index = LyricsFollow.currentIndex(parsed.lines, positionMs / 1000.0)

    LaunchedEffect(index, following) {
        if (following && parsed.synced && index >= 0) {
            programmatic = true
            try {
                listState.animateScrollToItem(index)
            } finally {
                programmatic = false
            }
        }
    }
    // A hand scroll stops the lyrics following the song; "Resume following" starts it again.
    LaunchedEffect(listState) {
        snapshotFlow { listState.isScrollInProgress }.collect { scrolling -> if (scrolling && !programmatic) following = false }
    }

    Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(LyricsFollow.chip(parsed), color = Accent, style = MaterialTheme.typography.labelMedium, modifier = Modifier.weight(1f))
        if (NowPlayingLogic.showResumeFollowing(parsed, userScrolled = !following)) {
            OutlinedButton(onClick = { following = true }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) {
                Text(NowPlayingLogic.RESUME_FOLLOWING)
            }
        }
    }
    LazyColumn(state = listState, modifier = Modifier.weight(1f).fillMaxWidth()) {
        itemsIndexed(parsed.lines) { i, line ->
            val seek = NowPlayingLogic.lyricSeek(line)
            Text(
                LyricsFollow.displayText(line),
                color = if (i == index) TuvimaTokens.textPrimary else MutedText,
                fontWeight = if (i == index) FontWeight.Bold else FontWeight.Normal,
                fontSize = 20.sp,
                modifier = Modifier
                    .fillMaxWidth()
                    .then(if (seek != null) Modifier.clickable(onClickLabel = "Jump to this line") { onSeek(seek) } else Modifier)
                    .padding(horizontal = 16.dp, vertical = 10.dp),
            )
        }
        NowPlayingLogic.lyricsHint(parsed)?.let { hint ->
            item { Text(hint, color = MutedText, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp)) }
        }
    }
}

// ---- Sleep timer ----

/**
 * The Sleep timer panel: four lengths, End of chapter (with chapters) and Custom duration…. While a timer runs it leads with "Stopping in 14:32" (or
 * "Stopping at the end of chapter 8") and "Playback will pause automatically.", and the footer offers "+5 minutes" and "Cancel timer". The timer
 * itself runs in the media service; this only sets it.
 */
@Composable
internal fun SleepTimerSheet(
    timer: SleepTimer,
    chapters: List<Chapter>,
    positionSeconds: () -> Double,
    durationSeconds: () -> Double?,
    onSet: (SleepTimer) -> Unit,
    onClose: () -> Unit,
) {
    var nowMs by remember { mutableLongStateOf(System.currentTimeMillis()) }
    var customOpen by remember { mutableStateOf(false) }
    var custom by remember { mutableStateOf("") }
    // The countdown on the lead line moves every second while the panel is open.
    LaunchedEffect(timer) {
        while (timer != SleepTimer.Off) {
            nowMs = System.currentTimeMillis()
            delay(1_000L)
        }
    }
    val running = timer != SleepTimer.Off
    PlayerSheet(
        title = "Sleep timer",
        onClose = onClose,
        footer = {
            if (running) {
                Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(
                        onClick = { onSet(SleepTimerLogic.plusFive(timer, System.currentTimeMillis())) },
                        modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp),
                    ) { Text(SleepTimerLogic.PLUS_FIVE_LABEL) }
                    OutlinedButton(onClick = { onSet(SleepTimer.Off) }, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) {
                        Text(SleepTimerLogic.CANCEL_LABEL)
                    }
                }
            }
        },
    ) {
        if (running) {
            Text(
                SleepTimerLogic.lead(timer, nowMs),
                color = TuvimaTokens.textPrimary,
                style = MaterialTheme.typography.headlineSmall,
                modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
            )
            Text(SleepTimerLogic.RUNNING_NOTE, color = MutedText, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        }
        SleepTimerLogic.choices(hasChapters = chapters.isNotEmpty()).forEach { choice ->
            Row(
                Modifier
                    .fillMaxWidth()
                    .defaultMinSize(minHeight = 52.dp)
                    .clickable {
                        if (choice.key == SleepTimerLogic.CUSTOM_KEY) {
                            customOpen = true
                        } else {
                            NowPlayingLogic.sleepTimerFor(choice.key, System.currentTimeMillis(), chapters, positionSeconds(), durationSeconds())
                                ?.let(onSet)
                        }
                    }
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(choice.label, color = TuvimaTokens.textPrimary, modifier = Modifier.weight(1f))
                if (NowPlayingLogic.sleepChoiceChecked(choice, timer)) Text("✓", color = TuvimaTokens.textPrimary)
            }
        }
        if (customOpen) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                OutlinedTextField(
                    value = custom,
                    onValueChange = { custom = it.filter(Char::isDigit).take(3) },
                    placeholder = { Text("Minutes") },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number),
                    modifier = Modifier.weight(1f),
                )
                Spacer(Modifier.width(8.dp))
                Button(
                    onClick = {
                        SleepTimerLogic.parseCustomMinutes(custom)?.let { minutes ->
                            onSet(SleepTimerLogic.after(minutes, System.currentTimeMillis()))
                            customOpen = false
                            custom = ""
                        }
                    },
                    enabled = SleepTimerLogic.parseCustomMinutes(custom) != null,
                    modifier = Modifier.defaultMinSize(minHeight = 48.dp),
                ) { Text("Start") }
            }
            Text(
                "Up to ${SleepTimerLogic.MAX_CUSTOM_MINUTES} minutes",
                color = MutedText,
                style = MaterialTheme.typography.bodySmall,
                textAlign = TextAlign.Start,
                modifier = Modifier.padding(horizontal = 16.dp),
            )
        }
    }
}
