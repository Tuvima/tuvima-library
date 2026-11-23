package com.tuvima.library.tv

import android.graphics.Bitmap
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.compositionLocalOf
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.TextUnit
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.material3.Text
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.LyricsFollow
import com.tuvima.library.core.LyricsPhase
import com.tuvima.library.core.LyricsView
import com.tuvima.library.core.TvControl
import com.tuvima.library.core.TvEndCardView
import com.tuvima.library.core.TvFocus
import com.tuvima.library.core.TvInfoLine
import com.tuvima.library.core.TvItem
import com.tuvima.library.core.TvLabel
import com.tuvima.library.core.TvMediaKind
import com.tuvima.library.core.TvNote
import com.tuvima.library.core.TvPanel
import com.tuvima.library.core.TvPanelFocus
import com.tuvima.library.core.TvPanelId
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvRepeatMode
import com.tuvima.library.core.TvRow
import com.tuvima.library.core.TvSegment
import com.tuvima.library.core.TvSwitch
import com.tuvima.library.core.TvTimeline
import com.tuvima.library.core.TvTool
import com.tuvima.library.core.TvZone
import com.tuvima.library.core.focusKey
import kotlin.coroutines.cancellation.CancellationException

// The television player's screen, drawn 1:1 from the approved mock-up (engineering/design/mockups/tv-player/tv-player-panels.html).
// The mock is a 1920 x 1080 picture, so every size below is written in the mock's own pixels and scaled by the real screen
// width ("mock pixels"): the 700 pixel panel is 36 percent of the screen on any television. Dark only, a white focus ring,
// rows at least 56 px high. These composables only draw: where focus is and what each key does come from the core reducers
// (the activity forwards the keys), so a dimmed or hidden control never moves anything.

/** Everything the screen draws. The activity owns one and replaces it as things change. */
data class TvPlayerScreenState(
    /** A sentence that replaces the player while there is nothing to play (starting, or a problem). */
    val status: String? = null,
    val kind: TvMediaKind = TvMediaKind.Film,
    val title: String = "",
    val line: String = "",
    val description: String = "",
    val rating: String = "",
    val artworkUrl: String? = null,
    val overlayVisible: Boolean = false,
    val focus: TvFocus = TvFocus.initial(TvPlayerLogic.PLAY_INDEX),
    val transport: List<TvControl> = emptyList(),
    val repeat: TvRepeatMode = TvRepeatMode.Off,
    val tools: List<TvTool> = emptyList(),
    val selectedTool: String? = null,
    val playing: Boolean = false,
    val timeline: TvTimeline = TvTimeline("0:00", "", 0.0),
    val ticks: List<Double> = emptyList(),
    val panel: TvPanel? = null,
    val panelFocus: TvPanelFocus = TvPanelFocus(),
    val lyrics: LyricsView = LyricsView.loading(),
    val lyricsIndex: Int = -1,
    val endCard: TvEndCardView? = null,
    val endFocus: Int = 1,
    val endCardImage: String? = null,
    val find: FindAnotherState? = null,
    val itemName: String? = null,
    val languageChoices: List<String> = emptyList(),
)

typealias TvImageLoader = suspend (String) -> Bitmap?

// ---- the mock's colours ----

private val Shell = Color(0xFF14151A)
private val Raised = Color(0xFF1F2027)
private val Hover = Color(0xFF272830)
private val TextColor = Color(0xFFF5F7FB)
private val Secondary = Color(0xFFB6C2D6)
private val Muted = Color(0xFF94A3B8)
private val Accent = Color(0xFF8852FC)
private val Line = Color(0xFF94A3B8).copy(alpha = 0.16f)
private val ButtonFill = Color(0xFF2F3039).copy(alpha = 0.85f)
private val Gold = Color(0xFFD6A84F)
private val Ring = Color.White
private val Glow = Color.White.copy(alpha = 0.28f)
private val ChipFill = Accent.copy(alpha = 0.3f)
private val ChipText = Color(0xFFD9C9FF)

private val LocalUnit = compositionLocalOf { 1f }

/** Mock pixels to dp. */
@Composable
private fun px(value: Int): Dp = (value * LocalUnit.current).dp

@Composable
private fun px(value: Float): Dp = (value * LocalUnit.current).dp

/** Mock font pixels to sp. */
@Composable
private fun fs(value: Int): TextUnit = (value * LocalUnit.current).sp

/** The one pixel lines of the panel's header (below) and footer (above). */
private fun Modifier.lineBottom(color: Color): Modifier = drawBehind {
    drawLine(color, Offset(0f, size.height), Offset(size.width, size.height), strokeWidth = 1f)
}

private fun Modifier.lineTop(color: Color): Modifier = drawBehind {
    drawLine(color, Offset(0f, 0f), Offset(size.width, 0f), strokeWidth = 1f)
}

@Composable
fun TvPlayerOverlay(state: TvPlayerScreenState, loader: TvImageLoader) {
    BoxWithConstraints(Modifier.fillMaxSize()) {
        CompositionLocalProvider(LocalUnit provides maxWidth.value / 1920f) {
            Box(Modifier.fillMaxSize()) {
                when {
                    state.status != null -> StatusText(state.status)
                    state.kind == TvMediaKind.Music || state.kind == TvMediaKind.Audiobook -> NowPlaying(state, loader)
                    else -> VideoControls(state, loader)
                }
                state.panel?.let { panel ->
                    SidePanel(
                        panel, state.panelFocus, state.lyrics, state.lyricsIndex, loader,
                        Modifier.align(Alignment.CenterEnd),
                    )
                }
                state.endCard?.let { EndCard(it, state.endFocus, state.endCardImage, loader, Modifier.align(Alignment.BottomEnd)) }
                state.find?.let { FindAnotherWindow(it, state.itemName, state.languageChoices) }
            }
        }
    }
}

@Composable
private fun StatusText(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = TextColor, fontSize = fs(34), textAlign = TextAlign.Center, modifier = Modifier.padding(px(96)))
    }
}

// ---- the video overlay ----

@Composable
private fun VideoControls(state: TvPlayerScreenState, loader: TvImageLoader) {
    val panelOpen = state.panel != null
    val paused = !state.playing
    val pausedDetails = TvPlayerLogic.pausedDetailsVisible(paused, panelOpen, state.endCard != null)
    val showControls = state.overlayVisible || paused || state.endCard != null
    Box(Modifier.fillMaxSize()) {
        // While a panel is open the picture is dimmed a little so the panel reads.
        if (panelOpen) Box(Modifier.fillMaxSize().background(Color.Black.copy(alpha = 0.35f)))
        if (showControls && !panelOpen) {
            Box(
                Modifier
                    .align(Alignment.BottomCenter)
                    .fillMaxWidth()
                    .height(px(520))
                    .background(Brush.verticalGradient(listOf(Color.Transparent, Color.Black.copy(alpha = 0.88f)))),
            )
        }
        // The rating card stays at the top left.
        if (showControls && state.rating.isNotBlank() && !panelOpen) RatingCard(state.rating, Modifier.align(Alignment.TopStart))
        if (showControls && !panelOpen && state.endCard == null) {
            TitleBlock(state, pausedDetails, Modifier.align(Alignment.BottomStart))
        }
        if (showControls && !panelOpen) {
            TimelineRow(state.timeline, state.ticks, state.focus.zone == TvZone.Timeline && state.overlayVisible, Modifier.align(Alignment.BottomCenter))
        }
        if (showControls) {
            TransportRow(state.transport, state.repeat, state.focus, panelOpen, Modifier.align(Alignment.BottomCenter))
            if (state.endCard == null) ToolRow(state.tools, state.selectedTool, state.focus, panelOpen, Modifier.align(Alignment.BottomCenter))
        }
    }
}

@Composable
private fun RatingCard(text: String, modifier: Modifier) {
    Text(
        text,
        color = Secondary,
        fontSize = fs(24),
        modifier = modifier
            .padding(start = px(96), top = px(72))
            .background(Color(0xFF14151A).copy(alpha = 0.8f), RoundedCornerShape(px(12)))
            .border(px(1), Line, RoundedCornerShape(px(12)))
            .padding(horizontal = px(20), vertical = px(10)),
    )
}

@Composable
private fun TitleBlock(state: TvPlayerScreenState, pausedDetails: Boolean, modifier: Modifier) {
    Column(modifier.padding(start = px(96), bottom = px(if (pausedDetails) 380 else 360)).widthIn(max = px(900))) {
        Text(state.title, color = TextColor, fontSize = fs(44), fontWeight = FontWeight.Bold, maxLines = 2, overflow = TextOverflow.Ellipsis)
        if (state.line.isNotBlank()) {
            Text(state.line, color = Secondary, fontSize = fs(30), modifier = Modifier.padding(top = px(4)), maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        // The short description shows only while paused, for episodes and lessons; a film has none here.
        if (pausedDetails && state.description.isNotBlank()) {
            Text(
                state.description, color = Muted, fontSize = fs(26), maxLines = 3, overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(top = px(10)).widthIn(max = px(820)),
            )
        }
    }
}

@Composable
private fun TimelineRow(timeline: TvTimeline, ticks: List<Double>, focused: Boolean, modifier: Modifier) {
    Row(
        modifier.fillMaxWidth().padding(start = px(96), end = px(96), bottom = px(300)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(px(24)),
    ) {
        Text(timeline.elapsed, color = Secondary, fontSize = fs(26))
        BoxWithConstraints(Modifier.weight(1f).height(px(40)), contentAlignment = Alignment.CenterStart) {
            val barWidth = maxWidth
            Box(Modifier.fillMaxWidth().height(px(10)).background(Color.White.copy(alpha = 0.2f), RoundedCornerShape(px(5))))
            Box(Modifier.fillMaxWidth(timeline.fraction.toFloat().coerceIn(0f, 1f)).height(px(10)).background(Accent, RoundedCornerShape(px(5))))
            // A tick for each bookmark when "Show on timeline" is on.
            ticks.forEach { percent ->
                Box(Modifier.offset(x = barWidth * (percent / 100.0).toFloat() - px(2)).size(width = px(4), height = px(22)).background(Gold))
            }
            val knob = if (focused) 34 else 26
            Box(
                Modifier
                    .offset(x = barWidth * timeline.fraction.toFloat().coerceIn(0f, 1f) - px(knob / 2f))
                    .size(px(knob))
                    .background(Color.White, CircleShape)
                    .then(if (focused) Modifier.border(px(6), Glow, CircleShape) else Modifier),
            )
        }
        Text(timeline.left, color = Secondary, fontSize = fs(26))
    }
}

@Composable
private fun TransportRow(controls: List<TvControl>, repeat: TvRepeatMode, focus: TvFocus, panelOpen: Boolean, modifier: Modifier) {
    Row(
        modifier.fillMaxWidth().padding(bottom = px(148), end = if (panelOpen) px(700) else 0.dp),
        horizontalArrangement = Arrangement.spacedBy(px(if (panelOpen) 4 else 16), Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        controls.forEachIndexed { index, control ->
            TransportButton(control, repeat, focused = focus.zone == TvZone.Transport && focus.transportIndex == index)
        }
    }
}

@Composable
private fun TransportButton(control: TvControl, repeat: TvRepeatMode, focused: Boolean) {
    val big = control.key == "play-pause"
    val size = if (big) 136 else 112
    val fill = when {
        big -> Accent
        control.selected -> Accent.copy(alpha = 0.55f)
        else -> ButtonFill
    }
    // The glow of a focused button sits in a 6 px ring around it, so nothing moves when focus does.
    Box(
        Modifier
            .size(px(size + 12))
            .alpha(if (control.dimmed) 0.35f else 1f)
            .border(px(6), if (focused) Glow else Color.Transparent, CircleShape)
            .padding(px(6)),
    ) {
        Box(
            Modifier
                .fillMaxSize()
                .background(fill, CircleShape)
                .border(px(3), if (focused) Ring else Color.Transparent, CircleShape),
            contentAlignment = Alignment.Center,
        ) {
            when (control.key) {
                "skip-back", "skip-forward" -> {
                    Text(if (control.key == "skip-back") "↺" else "↻", color = TextColor.copy(alpha = 0.55f), fontSize = fs(84))
                    Text((control.seconds ?: 0).toString(), color = TextColor, fontSize = fs(22), fontWeight = FontWeight.Bold)
                }
                "shuffle" -> Text("⇄", color = TextColor, fontSize = fs(56))
                "repeat" -> {
                    Text("↻", color = TextColor, fontSize = fs(56))
                    if (repeat == TvRepeatMode.One) Text("1", color = TextColor, fontSize = fs(22), fontWeight = FontWeight.Bold)
                }
                else -> TransportGlyph(control.key, control.label == "Pause", Modifier.size(px(if (big) 56 else 44)))
            }
        }
    }
}

/** Play, Pause, Previous and Next are drawn rather than typed, so they look the same on every television's fonts. */
@Composable
private fun TransportGlyph(key: String, pause: Boolean, modifier: Modifier) {
    Canvas(modifier) {
        val w = size.width
        val h = size.height
        val color = Color(0xFFF5F7FB)
        fun triangle(left: Float, right: Float, pointRight: Boolean) {
            val path = Path().apply {
                if (pointRight) {
                    moveTo(left, 0f); lineTo(right, h / 2f); lineTo(left, h)
                } else {
                    moveTo(right, 0f); lineTo(left, h / 2f); lineTo(right, h)
                }
                close()
            }
            drawPath(path, color)
        }
        when (key) {
            "play-pause" ->
                if (pause) {
                    drawRect(color, Offset(w * 0.12f, 0f), Size(w * 0.28f, h))
                    drawRect(color, Offset(w * 0.60f, 0f), Size(w * 0.28f, h))
                } else {
                    triangle(w * 0.1f, w * 0.95f, pointRight = true)
                }
            "previous" -> {
                drawRect(color, Offset(0f, 0f), Size(w * 0.16f, h))
                triangle(w * 0.2f, w, pointRight = false)
            }
            "next" -> {
                triangle(0f, w * 0.8f, pointRight = true)
                drawRect(color, Offset(w * 0.84f, 0f), Size(w * 0.16f, h))
            }
        }
    }
}

private fun toolIcon(key: String): String = when (key) {
    "episodes" -> "☰"
    "audio-subtitles" -> "💬"
    "bookmarks" -> "🔖"
    "playback-settings" -> "⚙"
    "lyrics" -> "♪"
    "chapters" -> "☰"
    else -> ""
}

@Composable
private fun ToolRow(tools: List<TvTool>, selected: String?, focus: TvFocus, panelOpen: Boolean, modifier: Modifier) {
    if (tools.isEmpty()) return
    Row(
        modifier.fillMaxWidth().padding(bottom = px(56), end = if (panelOpen) px(700) else 0.dp),
        horizontalArrangement = Arrangement.spacedBy(px(if (panelOpen) 16 else 24), Alignment.CenterHorizontally),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        tools.forEachIndexed { index, tool ->
            val focused = focus.zone == TvZone.Tools && focus.toolIndex == index
            val shape = RoundedCornerShape(px(38))
            Row(
                Modifier
                    .height(px(76))
                    .background(if (tool.key == selected) Accent.copy(alpha = 0.35f) else ButtonFill, shape)
                    .border(px(3), if (focused) Ring else if (tool.key == selected) Accent else Color.Transparent, shape)
                    .padding(horizontal = px(if (panelOpen) 20 else 30)),
                verticalAlignment = Alignment.CenterVertically,
                horizontalArrangement = Arrangement.spacedBy(px(12)),
            ) {
                Text(toolIcon(tool.key), color = TextColor, fontSize = fs(if (panelOpen) 24 else 26))
                Text(tool.label, color = TextColor, fontSize = fs(if (panelOpen) 24 else 26), maxLines = 1)
            }
        }
    }
}

// ---- Now playing (music and audiobooks) ----

@Composable
private fun NowPlaying(state: TvPlayerScreenState, loader: TvImageLoader) {
    val panelOpen = state.panel != null
    Box(
        Modifier
            .fillMaxSize()
            .background(Brush.linearGradient(listOf(Color(0xFF2B3A5C), Color(0xFF141A2B), Color(0xFF07090F)))),
    ) {
        Row(
            Modifier.fillMaxSize().padding(start = px(160), end = px(if (panelOpen) 760 else 160), top = px(120), bottom = px(300)),
            horizontalArrangement = Arrangement.spacedBy(px(72)),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Box(
                Modifier
                    .size(px(560))
                    .background(Brush.linearGradient(listOf(Color(0xFF3A4A70), Color(0xFF1C2338))), RoundedCornerShape(px(16))),
            ) {
                TvImage(state.artworkUrl, loader, Modifier.fillMaxSize(), RoundedCornerShape(px(16)))
            }
            Column(Modifier.weight(1f)) {
                Text(state.title, color = TextColor, fontSize = fs(56), fontWeight = FontWeight.Bold, maxLines = 3, overflow = TextOverflow.Ellipsis)
                if (state.line.isNotBlank()) {
                    Text(state.line, color = Secondary, fontSize = fs(34), modifier = Modifier.padding(top = px(8)), maxLines = 2, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        TimelineRow(state.timeline, state.ticks, state.focus.zone == TvZone.Timeline, Modifier.align(Alignment.BottomCenter).padding(end = if (panelOpen) px(700) else 0.dp))
        TransportRow(state.transport, state.repeat, state.focus, panelOpen, Modifier.align(Alignment.BottomCenter))
        ToolRow(state.tools, state.selectedTool, state.focus, panelOpen, Modifier.align(Alignment.BottomCenter))
    }
}

// ---- the side panel ----

@Composable
private fun SidePanel(
    panel: TvPanel,
    focus: TvPanelFocus,
    lyrics: LyricsView,
    lyricsIndex: Int,
    loader: TvImageLoader,
    modifier: Modifier,
) {
    val focusedKey = if (focus.index >= 0) panel.focusable().getOrNull(focus.index)?.focusKey() else null
    Row(modifier.width(px(700)).fillMaxHeight()) {
        Box(Modifier.width(px(1)).fillMaxHeight().background(Line))
        Column(Modifier.weight(1f).fillMaxHeight().background(Shell)) {
            PanelHeader(panel, headerFocus = focus.index < 0, focus.sub)
            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (panel.id == TvPanelId.Lyrics) {
                    LyricsBody(lyrics, lyricsIndex)
                } else {
                    PanelBody(panel.body, focusedKey, focus.sub, loader)
                }
            }
            if (panel.footer.isNotEmpty()) {
                Column(
                    Modifier
                        .fillMaxWidth()
                        .heightIn(min = px(104))
                        .lineTop(Line)
                        .padding(horizontal = px(36), vertical = px(18)),
                    verticalArrangement = Arrangement.Center,
                ) {
                    panel.footer.forEach { PanelItemView(it, focusedKey, focus.sub, loader) }
                }
            }
        }
    }
}

@Composable
private fun PanelHeader(panel: TvPanel, headerFocus: Boolean, sub: Int) {
    Row(
        Modifier.fillMaxWidth().height(px(104)).lineBottom(Line).padding(horizontal = px(36)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(px(20)), modifier = Modifier.weight(1f)) {
            if (panel.back) {
                HeaderPill("‹", ringed = headerFocus && sub == 0)
            }
            Text(panel.title, color = TextColor, fontSize = fs(34), fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        HeaderPill("Close", ringed = headerFocus && sub == (if (panel.back) 1 else 0))
    }
}

@Composable
private fun HeaderPill(text: String, ringed: Boolean) {
    val shape = RoundedCornerShape(px(30))
    Text(
        text,
        color = Secondary,
        fontSize = fs(26),
        modifier = Modifier
            .heightIn(min = px(56))
            .border(px(if (ringed) 3 else 2), if (ringed) Ring else Line, shape)
            .padding(horizontal = px(22), vertical = px(10)),
    )
}

@Composable
private fun PanelBody(items: List<TvItem>, focusedKey: String?, sub: Int, loader: TvImageLoader) {
    val listState = rememberLazyListState()
    val focusedAt = items.indexOfFirst { it.focusKey() != null && it.focusKey() == focusedKey }
    LaunchedEffect(focusedAt) {
        if (focusedAt >= 0) listState.animateScrollToItem((focusedAt - 2).coerceAtLeast(0))
    }
    LazyColumn(state = listState, modifier = Modifier.fillMaxSize().padding(horizontal = px(28), vertical = px(20))) {
        itemsIndexed(items) { _, item -> PanelItemView(item, focusedKey, sub, loader) }
    }
}

@Composable
private fun PanelItemView(item: TvItem, focusedKey: String?, sub: Int, loader: TvImageLoader) {
    val focused = item.focusKey() != null && item.focusKey() == focusedKey
    when (item) {
        is TvLabel -> Text(item.text, color = Muted, fontSize = fs(23), modifier = Modifier.padding(start = px(8), top = px(14), bottom = px(6)))
        is TvNote -> Text(item.text, color = Muted, fontSize = fs(23), modifier = Modifier.padding(horizontal = px(8), vertical = px(10)))
        is TvInfoLine -> Row(Modifier.fillMaxWidth().padding(horizontal = px(16), vertical = px(14)), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(item.label, color = Muted, fontSize = fs(26))
            Text(item.value, color = TextColor, fontSize = fs(26))
        }
        is TvSwitch -> SwitchRow(item, focused)
        is TvSegment -> SegmentView(item, focused)
        is TvRow -> RowView(item, focused, sub, loader)
    }
}

@Composable
private fun RowView(row: TvRow, focused: Boolean, sub: Int, loader: TvImageLoader) {
    val shape = RoundedCornerShape(px(14))
    if (row.primary) {
        Box(
            Modifier
                .fillMaxWidth()
                .padding(bottom = px(6))
                .heightIn(min = px(72))
                .background(Accent, shape)
                .border(px(3), if (focused) Ring else Color.Transparent, shape)
                .padding(px(16)),
            contentAlignment = Alignment.Center,
        ) {
            Text("＋ " + row.title, color = Color.White, fontSize = fs(28), fontWeight = FontWeight.SemiBold)
        }
        return
    }
    val rowRing = focused && sub == 0
    Row(
        Modifier
            .fillMaxWidth()
            .padding(bottom = px(6))
            .heightIn(min = px(66))
            .background(if (focused) Hover else Color.Transparent, shape)
            .border(px(3), if (rowRing) Ring else Color.Transparent, shape)
            .padding(horizontal = px(16), vertical = px(14)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(px(20)),
    ) {
        if (row.showImage) {
            Box(
                Modifier
                    .size(width = px(150), height = px(84))
                    .background(Brush.linearGradient(listOf(Color(0xFF3A4A70), Color(0xFF1C2338))), RoundedCornerShape(px(8))),
            ) {
                TvImage(row.imageUrl, loader, Modifier.fillMaxSize(), RoundedCornerShape(px(8)))
            }
        }
        Column(Modifier.weight(1f)) {
            Text(
                row.title, color = TextColor, fontSize = fs(28), fontWeight = FontWeight.SemiBold,
                maxLines = 1, overflow = TextOverflow.Ellipsis,
            )
            row.sub?.let { Text(it, color = Muted, fontSize = fs(23), maxLines = 2, overflow = TextOverflow.Ellipsis) }
        }
        row.chip?.let {
            Text(
                it, color = ChipText, fontSize = fs(22), maxLines = 1,
                modifier = Modifier.background(ChipFill, RoundedCornerShape(px(20))).padding(horizontal = px(16), vertical = px(2)),
            )
        }
        if (row.checked) Text("✓", color = Accent, fontSize = fs(32), fontWeight = FontWeight.Bold)
        row.trailing?.let { Text(it, color = Secondary, fontSize = fs(26), maxLines = 1) }
        row.actions.forEachIndexed { index, action ->
            val ringed = focused && sub == index + 1
            val pill = RoundedCornerShape(px(30))
            Text(
                action.label, color = TextColor, fontSize = fs(24),
                modifier = Modifier
                    .background(ButtonFill, pill)
                    .border(px(3), if (ringed) Ring else Color.Transparent, pill)
                    .padding(horizontal = px(20), vertical = px(8)),
            )
        }
    }
}

@Composable
private fun SegmentView(segment: TvSegment, focused: Boolean) {
    val shape = RoundedCornerShape(px(30))
    Row(
        Modifier
            .fillMaxWidth()
            .padding(top = px(6), bottom = px(14))
            .border(px(if (focused) 3 else 2), if (focused) Ring else Line, shape)
            .background(Color.Transparent, shape),
    ) {
        segment.options.forEach { option ->
            val on = option.key == segment.selectedKey
            Text(
                option.label,
                color = if (on) Color.White else Secondary,
                fontSize = fs(26),
                textAlign = TextAlign.Center,
                maxLines = 1,
                modifier = Modifier.weight(1f).background(if (on) Accent else Color.Transparent, shape).padding(vertical = px(10)),
            )
        }
    }
}

@Composable
private fun SwitchRow(item: TvSwitch, focused: Boolean) {
    val shape = RoundedCornerShape(px(14))
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = px(66))
            .background(if (focused) Hover else Color.Transparent, shape)
            .border(px(3), if (focused) Ring else Color.Transparent, shape)
            .padding(horizontal = px(16), vertical = px(10)),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Text(item.label, color = Secondary, fontSize = fs(26))
        Box(
            Modifier.size(width = px(84), height = px(46)).background(if (item.on) Accent else Color(0xFF3A3B45), RoundedCornerShape(px(23))),
            contentAlignment = if (item.on) Alignment.CenterEnd else Alignment.CenterStart,
        ) {
            Box(Modifier.padding(px(5)).size(px(36)).background(Color.White, CircleShape))
        }
    }
}

// ---- Lyrics ----

@Composable
private fun LyricsBody(view: LyricsView, currentIndex: Int) {
    Column(Modifier.fillMaxSize().padding(horizontal = px(36), vertical = px(20))) {
        if (view.phase == LyricsPhase.Ready) {
            Text(
                LyricsFollow.chip(view.parsed), color = Color.White, fontSize = fs(22), fontWeight = FontWeight.Bold,
                modifier = Modifier.background(Accent, RoundedCornerShape(px(20))).padding(horizontal = px(16), vertical = px(4)),
            )
            Spacer(Modifier.height(px(12)))
        }
        Box(Modifier.weight(1f).fillMaxWidth()) {
            when (view.phase) {
                LyricsPhase.Ready -> LyricLines(view, currentIndex)
                LyricsPhase.Loading -> LyricNote(view.note ?: LyricsFollow.LOOKING)
                LyricsPhase.None -> LyricNote(LyricsFollow.NO_LYRICS)
                LyricsPhase.Failed -> LyricNote(LyricsFollow.NOT_LOADED)
            }
        }
    }
}

@Composable
private fun LyricNote(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = Muted, fontSize = fs(26), textAlign = TextAlign.Center, modifier = Modifier.padding(px(16)))
    }
}

/** The lines, the current synced one bright and larger; the list scrolls so the current line sits a little below the top. */
@Composable
private fun LyricLines(view: LyricsView, currentIndex: Int) {
    val lines = view.parsed?.lines.orEmpty()
    val listState = rememberLazyListState()
    LaunchedEffect(currentIndex, lines.size) {
        if (lines.isNotEmpty()) listState.animateScrollToItem((currentIndex - 2).coerceIn(0, lines.size - 1))
    }
    LazyColumn(state = listState, verticalArrangement = Arrangement.spacedBy(px(14)), modifier = Modifier.fillMaxSize()) {
        itemsIndexed(lines) { index, line ->
            val current = index == currentIndex
            Text(
                LyricsFollow.displayText(line),
                color = if (current) TextColor else Muted,
                fontSize = fs(if (current) 32 else 28),
                fontWeight = if (current) FontWeight.Bold else FontWeight.Normal,
            )
        }
    }
}

// ---- the end card ----

@Composable
private fun EndCard(card: TvEndCardView, focus: Int, imageUrl: String?, loader: TvImageLoader, modifier: Modifier) {
    val shape = RoundedCornerShape(px(18))
    Column(
        modifier
            .padding(end = px(96), bottom = px(330))
            .width(px(700))
            .background(Color(0xFF1A1B21).copy(alpha = 0.96f), shape)
            .border(px(1), Line, shape)
            .padding(px(30)),
    ) {
        Text(card.heading, color = TextColor, fontSize = fs(34), fontWeight = FontWeight.Bold)
        if (card.countdown.isNotBlank()) Text(card.countdown, color = Secondary, fontSize = fs(26))
        Row(Modifier.padding(vertical = px(20)), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(px(20))) {
            Box(
                Modifier
                    .size(width = px(150), height = px(84))
                    .background(Brush.linearGradient(listOf(Color(0xFF3A4A70), Color(0xFF1C2338))), RoundedCornerShape(px(8))),
            ) {
                TvImage(imageUrl, loader, Modifier.fillMaxSize(), RoundedCornerShape(px(8)))
            }
            Column(Modifier.weight(1f)) {
                Text(card.title, color = TextColor, fontSize = fs(28), fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Text(card.kind, color = Muted, fontSize = fs(23))
            }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(px(20))) {
            EndCardButton(card.cancel, focused = focus == 0, primary = false, modifier = Modifier.weight(1f))
            EndCardButton(card.playNow, focused = focus == 1, primary = true, modifier = Modifier.weight(1f))
        }
    }
}

@Composable
private fun EndCardButton(text: String, focused: Boolean, primary: Boolean, modifier: Modifier) {
    val shape = RoundedCornerShape(px(40))
    Text(
        text,
        color = TextColor,
        fontSize = fs(28),
        textAlign = TextAlign.Center,
        modifier = modifier
            .background(if (primary) Accent else Color(0xFF2F3039), shape)
            .border(px(3), if (focused) Ring else Color.Transparent, shape)
            .padding(vertical = px(16)),
    )
}

// ---- artwork ----

@Composable
private fun TvImage(url: String?, loader: TvImageLoader, modifier: Modifier, shape: RoundedCornerShape) {
    if (url.isNullOrBlank()) return
    val bitmap by produceState<ImageBitmap?>(null, url) {
        value = try {
            loader(url)?.asImageBitmap()
        } catch (error: CancellationException) {
            throw error
        } catch (error: Exception) {
            // A picture that cannot be drawn leaves the placeholder; nothing depends on it.
            null
        }
    }
    bitmap?.let { Image(it, null, modifier.background(Color.Transparent, shape), contentScale = ContentScale.Crop) }
}
