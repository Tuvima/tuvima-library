package com.tuvima.library.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.gestures.animateScrollBy
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.key
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.AudioSubtitlesPanel
import com.tuvima.library.core.CheckTone
import com.tuvima.library.core.FindAnotherPane
import com.tuvima.library.core.FindAnotherPhase
import com.tuvima.library.core.FindAnotherRow
import com.tuvima.library.core.FindAnotherRows
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.FindAnotherWording
import com.tuvima.library.core.FindAnotherZone
import com.tuvima.library.core.PanelRow
import com.tuvima.library.core.PanelRowKind

// The Audio & subtitles side panel and the Find another window of the television player (spec "Find another on TV").
// Dark only, controls at least 56dp, a white focus ring, 5 percent safe margins, no toasts and nothing to type. These
// composables only draw: the remote's keys go through the core key reducers (the activity forwards them), so where focus
// is comes from state rather than from the framework's focus search.

private val Scrim = Color(0xF209080D)
private val PanelBackground = Color(0xF2141018)
private val SurfaceColor = Color(0xFF241D32)
private val SurfaceFocusedRow = Color(0xFF30284A)
private val ButtonSurface = Color(0xFF2F2742)
private val TextPrimary = Color.White
private val TextMuted = Color(0xFFB8B0C8)
private val Accent = Color(0xFF9B7BFF)
private val Ok = Color(0xFF6FD08C)
private val Warn = Color(0xFFF2B84B)
private val Bad = Color(0xFFFF6B6B)
private val Ring = Color.White

private val RowShape = RoundedCornerShape(12.dp)

/** The white ring the focused control wears (D2). */
private fun Modifier.focusRing(focused: Boolean, shape: Shape = RowShape): Modifier =
    border(width = if (focused) 4.dp else 0.dp, color = if (focused) Ring else Color.Transparent, shape = shape)

private fun toneColor(tone: CheckTone): Color = when (tone) {
    CheckTone.Ok -> Ok
    CheckTone.Warn -> Warn
    CheckTone.Bad -> Bad
    CheckTone.Neutral -> TextMuted
}

/** The tool that opens the panel, shown with the player's controls: Up from the controls reaches it. For a song it reads "Lyrics". */
@Composable
internal fun AudioSubtitlesToolButton(focused: Boolean, modifier: Modifier = Modifier, label: String = "Audio & subtitles") {
    Box(
        modifier
            .heightIn(min = 56.dp)
            .focusRing(focused, RoundedCornerShape(28.dp))
            .background(ButtonSurface, RoundedCornerShape(28.dp))
            .padding(horizontal = 28.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = TextPrimary, fontSize = 20.sp)
    }
}

/** The small Audio & subtitles side panel: Audio rows (when several), Subtitles rows (Off, then each track) and the footer row. */
@Composable
internal fun AudioSubtitlesPanelView(rows: List<PanelRow>, focusIndex: Int, modifier: Modifier = Modifier) {
    val focusable = AudioSubtitlesPanel.focusable(rows)
    val focusedRow = focusable.getOrNull(focusIndex)
    val listState = rememberLazyListState()
    LaunchedEffect(focusIndex, rows.size) {
        val position = rows.indexOf(focusedRow)
        if (position >= 0) listState.animateScrollToItem((position - 2).coerceAtLeast(0))
    }
    Column(
        modifier
            .width(400.dp)
            .fillMaxHeight()
            .background(PanelBackground, RoundedCornerShape(16.dp))
            .padding(horizontal = 24.dp, vertical = 28.dp),
    ) {
        Text("Audio & subtitles", color = TextPrimary, fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Spacer(Modifier.height(12.dp))
        LazyColumn(state = listState, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            itemsIndexed(rows) { _, row ->
                if (!row.isFocusable) {
                    Text(
                        row.label.uppercase(),
                        color = TextMuted,
                        fontSize = 16.sp,
                        fontWeight = FontWeight.Bold,
                        modifier = Modifier.padding(top = 16.dp, bottom = 4.dp),
                    )
                } else {
                    PanelRowView(row, focused = row === focusedRow)
                }
            }
        }
    }
}

@Composable
private fun PanelRowView(row: PanelRow, focused: Boolean) {
    val isFooter = row.kind == PanelRowKind.Find
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .focusRing(focused)
            .background(if (focused) SurfaceFocusedRow else SurfaceColor, RowShape)
            .padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        // A tick marks the track in use.
        Text(if (row.selected) "✓" else "", color = Accent, fontSize = 22.sp, modifier = Modifier.width(32.dp))
        Text(
            row.label,
            color = if (isFooter) Accent else TextPrimary,
            fontSize = 22.sp,
            fontWeight = if (isFooter) FontWeight.Bold else FontWeight.Normal,
        )
    }
}

/**
 * The Find another window: full screen over the paused, dimmed video. A header (the item's name and "Find subtitles", one
 * Language button), the list on the left, the focused row's words on the right, and Use this and Cancel at the foot.
 */
@Composable
internal fun FindAnotherWindow(state: FindAnotherState, itemName: String?, languageChoices: List<String>, modifier: Modifier = Modifier) {
    val keys = state.keys
    val rows = FindAnotherRows.rows(state.items, state.kind)
    BoxWithConstraints(modifier.fillMaxSize().background(Scrim)) {
        // 5 percent safe margins on every side (D2).
        val horizontal = maxWidth * 0.05f
        val vertical = maxHeight * 0.05f
        Column(Modifier.fillMaxSize().padding(horizontal = horizontal, vertical = vertical)) {
            Header(state, itemName)
            Row(
                Modifier.weight(1f).fillMaxWidth().padding(vertical = 20.dp),
                horizontalArrangement = Arrangement.spacedBy(32.dp),
            ) {
                Box(Modifier.weight(0.46f).fillMaxHeight()) { ListArea(state, rows) }
                Box(Modifier.weight(0.54f).fillMaxHeight()) { Pane(state, rows.getOrNull(keys.row)) }
            }
            Footer(state, hasRows = rows.isNotEmpty())
        }
        if (keys.languageListOpen) {
            LanguageList(
                languageChoices,
                keys.languageIndex,
                Modifier.align(Alignment.TopEnd).padding(end = horizontal, top = vertical + 84.dp),
            )
        }
    }
}

@Composable
private fun Header(state: FindAnotherState, itemName: String?) {
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            itemName?.takeIf { it.isNotBlank() }?.let { Text(it, color = TextMuted, fontSize = 22.sp, maxLines = 1) }
            Text(FindAnotherWording.windowTitle(state.kind), color = TextPrimary, fontSize = 34.sp, fontWeight = FontWeight.Bold)
        }
        if (state.keys.languageAvailable) {
            val shape = RoundedCornerShape(28.dp)
            Box(
                Modifier
                    .heightIn(min = 56.dp)
                    .focusRing(state.keys.zone == FindAnotherZone.Language, shape)
                    .background(ButtonSurface, shape)
                    .padding(horizontal = 28.dp),
                contentAlignment = Alignment.Center,
            ) {
                Text("Language: ${state.language}", color = TextPrimary, fontSize = 20.sp)
            }
        }
    }
}

@Composable
private fun LanguageList(choices: List<String>, index: Int, modifier: Modifier) {
    Column(
        modifier.width(320.dp).background(PanelBackground, RoundedCornerShape(16.dp)).padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp),
    ) {
        choices.forEachIndexed { position, name ->
            val focused = position == index
            Box(
                Modifier
                    .fillMaxWidth()
                    .heightIn(min = 56.dp)
                    .focusRing(focused)
                    .background(if (focused) SurfaceFocusedRow else SurfaceColor, RowShape)
                    .padding(horizontal = 16.dp),
                contentAlignment = Alignment.CenterStart,
            ) {
                Text(name, color = TextPrimary, fontSize = 22.sp)
            }
        }
    }
}

@Composable
private fun ListArea(state: FindAnotherState, rows: List<FindAnotherRow>) {
    when (state.phase) {
        FindAnotherPhase.Loading -> CenteredText(FindAnotherWording.loadingText(state.kind))
        FindAnotherPhase.Problem -> CenteredText(state.problem.orEmpty())
        FindAnotherPhase.Rows -> {
            val listState = rememberLazyListState()
            LaunchedEffect(state.keys.row) { listState.animateScrollToItem((state.keys.row - 1).coerceAtLeast(0)) }
            LazyColumn(state = listState, verticalArrangement = Arrangement.spacedBy(8.dp), modifier = Modifier.fillMaxSize()) {
                itemsIndexed(rows, key = { _, row -> row.key }) { index, row ->
                    ResultRow(
                        row,
                        isFocusedRow = index == state.keys.row,
                        ringed = index == state.keys.row && state.keys.zone == FindAnotherZone.List,
                    )
                }
            }
        }
    }
}

@Composable
private fun CenteredText(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = TextPrimary, fontSize = 26.sp, textAlign = TextAlign.Center, modifier = Modifier.padding(32.dp))
    }
}

@Composable
private fun ResultRow(row: FindAnotherRow, isFocusedRow: Boolean, ringed: Boolean) {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .focusRing(ringed)
            .background(if (isFocusedRow) SurfaceFocusedRow else SurfaceColor, RowShape)
            .padding(16.dp),
        verticalAlignment = Alignment.Top,
    ) {
        Column(Modifier.weight(1f)) {
            Text(row.title, color = TextPrimary, fontSize = 22.sp, fontWeight = FontWeight.Bold, maxLines = 2)
            row.meta?.let { Text(it, color = TextMuted, fontSize = 17.sp) }
            row.checks.forEach { check -> Text(check.text, color = toneColor(check.tone), fontSize = 17.sp) }
        }
        // "Best match" and "In use" sit at the right of their rows.
        Column(horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(6.dp)) {
            row.bestMatchChip?.let { Chip(it) }
            row.inUseChip?.let { Chip(it) }
        }
    }
}

@Composable
private fun Chip(text: String) {
    Text(
        text,
        color = TextPrimary,
        fontSize = 15.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier.background(Accent, RoundedCornerShape(50)).padding(horizontal = 12.dp, vertical = 4.dp),
    )
}

/** The pane titled with the focused row's name: its first lines and the time each appears; "Reading…" while that row is read. */
@Composable
private fun Pane(state: FindAnotherState, row: FindAnotherRow?) {
    val shape = RoundedCornerShape(16.dp)
    val focused = state.keys.zone == FindAnotherZone.Pane
    Column(
        Modifier
            .fillMaxSize()
            .focusRing(focused, shape)
            .background(SurfaceColor, shape)
            .padding(24.dp),
    ) {
        if (row == null || state.phase != FindAnotherPhase.Rows) return@Column
        Text(row.title, color = TextPrimary, fontSize = 26.sp, fontWeight = FontWeight.Bold, maxLines = 2)
        Spacer(Modifier.height(12.dp))
        val pane = FindAnotherRows.pane(
            row.item,
            reading = row.key in state.reading,
            failed = row.key in state.failed,
            full = state.keys.fullWords,
        )
        when (pane) {
            is FindAnotherPane.Prompt -> Text(pane.text, color = TextMuted, fontSize = 22.sp)
            FindAnotherPane.Reading -> Text(FindAnotherWording.READING, color = TextMuted, fontSize = 22.sp)
            FindAnotherPane.CouldNotRead -> Text(FindAnotherWording.COULD_NOT_READ, color = TextMuted, fontSize = 22.sp)
            is FindAnotherPane.Lines -> {
                // A new row or the full words start at the top.
                key(row.key, pane.full) {
                    val scroll = rememberScrollState()
                    LaunchedEffect(state.paneScrollTick) {
                        if (state.paneScrollTick > 0) scroll.animateScrollBy(state.paneScrollLines * 96f)
                    }
                    Column(Modifier.fillMaxSize().verticalScroll(scroll), verticalArrangement = Arrangement.spacedBy(14.dp)) {
                        pane.lines.forEach { line ->
                            Row {
                                line.atSeconds?.let {
                                    Text(FindAnotherWording.clock(it), color = Accent, fontSize = 20.sp, modifier = Modifier.width(88.dp))
                                }
                                Text(line.text, color = TextPrimary, fontSize = if (pane.full) 22.sp else 24.sp)
                            }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun Footer(state: FindAnotherState, hasRows: Boolean) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(20.dp)) {
        if (hasRows) {
            FooterButton(FindAnotherWording.USE_THIS, state.keys.zone == FindAnotherZone.UseThis, enabled = !state.applying)
        }
        FooterButton(FindAnotherWording.CANCEL, state.keys.zone == FindAnotherZone.Cancel, enabled = true)
        // The app's normal Updating state while the choice is applied; the sentence under the list when it did not work.
        val note = if (state.applying) "Updating…" else state.applyProblem
        note?.let { Text(it, color = if (state.applying) TextMuted else Warn, fontSize = 20.sp) }
    }
}

@Composable
private fun FooterButton(label: String, focused: Boolean, enabled: Boolean) {
    val shape = RoundedCornerShape(28.dp)
    Box(
        Modifier
            .heightIn(min = 56.dp)
            .widthIn(min = 180.dp)
            .focusRing(focused, shape)
            .background(ButtonSurface, shape)
            .padding(horizontal = 32.dp),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = if (enabled) TextPrimary else TextMuted, fontSize = 22.sp, fontWeight = FontWeight.Bold)
    }
}
