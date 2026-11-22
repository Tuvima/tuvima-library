package com.tuvima.library.tv

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.FindAnotherWording
import com.tuvima.library.core.LyricsFollow
import com.tuvima.library.core.LyricsPhase
import com.tuvima.library.core.LyricsView
import com.tuvima.library.core.TextTrackKind

// The Lyrics side panel of the television player, for songs (spec "Find another on TV", D6 and WP5). A heading with the
// Synced or Unsynced chip, the lyric lines (the current synced line is highlighted and the list follows the song) and, only
// when a lyrics source is ready, the footer row "Find lyrics…". Dark only, controls at least 56dp, a white focus ring. It
// only draws: the remote's keys go through LyricsFollow.panelKey (the activity forwards them).

private val PanelBackground = Color(0xF2141018)
private val SurfaceFocusedRow = Color(0xFF30284A)
private val TextPrimary = Color.White
private val TextMuted = Color(0xFFB8B0C8)
private val Accent = Color(0xFF9B7BFF)
private val RowShape = RoundedCornerShape(12.dp)

/** The Lyrics panel. [currentIndex] is the line to highlight (-1 for none); [canFind] shows the footer row, which is then the one focus. */
@Composable
internal fun LyricsPanelView(view: LyricsView, currentIndex: Int, canFind: Boolean, modifier: Modifier = Modifier) {
    Column(
        modifier
            .width(460.dp)
            .fillMaxHeight()
            .background(PanelBackground, RoundedCornerShape(16.dp))
            .padding(horizontal = 24.dp, vertical = 28.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(16.dp)) {
            Text(LyricsFollow.HEADING, color = TextPrimary, fontSize = 28.sp, fontWeight = FontWeight.Bold)
            if (view.phase == LyricsPhase.Ready) LyricsChip(LyricsFollow.chip(view.parsed))
        }
        Spacer(Modifier.height(12.dp))
        Box(Modifier.weight(1f).fillMaxWidth()) {
            when (view.phase) {
                LyricsPhase.Ready -> LyricsLines(view, currentIndex)
                LyricsPhase.Loading -> LyricsNote(view.note ?: LyricsFollow.LOOKING)
                LyricsPhase.None -> LyricsNote(LyricsFollow.NO_LYRICS)
                LyricsPhase.Failed -> LyricsNote(LyricsFollow.NOT_LOADED)
            }
        }
        if (canFind) {
            Spacer(Modifier.height(12.dp))
            FindLyricsRow()
        }
    }
}

@Composable
private fun LyricsChip(text: String) {
    Text(
        text,
        color = TextPrimary,
        fontSize = 16.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier.background(Accent, RoundedCornerShape(50)).padding(horizontal = 12.dp, vertical = 4.dp),
    )
}

@Composable
private fun LyricsNote(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = TextMuted, fontSize = 22.sp, textAlign = TextAlign.Center, modifier = Modifier.padding(16.dp))
    }
}

/** The lines, the current one bright and larger; the list scrolls so the current line sits a little below the top. */
@Composable
private fun LyricsLines(view: LyricsView, currentIndex: Int) {
    val lines = view.parsed?.lines.orEmpty()
    val listState = rememberLazyListState()
    LaunchedEffect(currentIndex, lines.size) {
        if (lines.isNotEmpty()) listState.animateScrollToItem((currentIndex - 2).coerceIn(0, lines.size - 1))
    }
    LazyColumn(state = listState, verticalArrangement = Arrangement.spacedBy(14.dp), modifier = Modifier.fillMaxSize()) {
        itemsIndexed(lines) { index, line ->
            val current = index == currentIndex
            Text(
                LyricsFollow.displayText(line),
                color = if (current) TextPrimary else TextMuted,
                fontSize = if (current) 28.sp else 24.sp,
                fontWeight = if (current) FontWeight.Bold else FontWeight.Normal,
            )
        }
    }
}

/** The footer row, always the focused one while it is shown: Select opens the Find lyrics window. */
@Composable
private fun FindLyricsRow() {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 56.dp)
            .border(width = 4.dp, color = Color.White, shape = RowShape)
            .background(SurfaceFocusedRow, RowShape)
            .padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(FindAnotherWording.entryLabel(TextTrackKind.Lyrics), color = Accent, fontSize = 22.sp, fontWeight = FontWeight.Bold)
    }
}
