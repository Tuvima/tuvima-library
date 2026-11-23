package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.snapshotFlow
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.LyricsFollow
import com.tuvima.library.core.LyricsPhase
import com.tuvima.library.core.LyricsView
import com.tuvima.library.core.PhoneBookmarks
import com.tuvima.library.core.QueueLogic
import com.tuvima.library.core.QueueRow
import com.tuvima.library.core.QueueRowAction
import com.tuvima.library.core.QueueTab
import com.tuvima.library.core.QueueView
import com.tuvima.library.core.SleepChoice
import com.tuvima.library.core.SleepTimer
import com.tuvima.library.core.SleepTimerLogic
import com.tuvima.library.core.TvBookmark
import com.tuvima.library.core.TvBookmarkTab
import com.tuvima.library.core.TvInfoLine
import com.tuvima.library.core.TvLabel
import com.tuvima.library.core.TvNote
import com.tuvima.library.core.TvPanel
import com.tuvima.library.core.TvPlayerLogic
import com.tuvima.library.core.TvPlayerWords
import com.tuvima.library.core.TuvimaClient
import com.tuvima.library.core.TvRow
import com.tuvima.library.core.TvSegment
import com.tuvima.library.core.TvSwitch
import kotlinx.coroutines.delay

// The phone's player panels (spec native-apps WP10b, the contract's Panels table) as bottom sheets, shared by the video, music and audiobook
// players. Every word and row comes from the pure builders in `core` (`TvPanels`, `PhoneBookmarks`, `QueueLogic`, `SleepTimerLogic`,
// `LyricsFollow`); these composables only draw them and report taps. Only one sheet is open at a time (the host keeps one), closing a sheet never
// changes playback, and nothing depends on hover. Dark only, controls at least 44 dp, icon-only buttons are bare, no toasts.

private val MutedText = TuvimaTokens.textMuted
private val Accent = TuvimaTokens.accentPrimaryHover

/**
 * The frame every panel shares: a fixed header (a back arrow in a nested view, the name, Close), a middle that scrolls and an optional fixed
 * footer. [scroll] is false for a body that scrolls by itself (the lyrics).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun PlayerSheet(
    title: String,
    onClose: () -> Unit,
    onBack: (() -> Unit)? = null,
    scroll: Boolean = true,
    footer: @Composable ColumnScope.() -> Unit = {},
    body: @Composable ColumnScope.() -> Unit,
) {
    ModalBottomSheet(
        onDismissRequest = onClose,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = TuvimaTokens.bgSurface,
    ) {
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.82f)) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                if (onBack != null) {
                    BareIconButton("Back", onBack) { Text("‹", color = TuvimaTokens.textPrimary, fontSize = 24.sp) }
                } else {
                    Spacer(Modifier.width(8.dp))
                }
                Text(title, color = TuvimaTokens.textPrimary, style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
                BareIconButton(TvPlayerWords.CLOSE, onClose) { Text("✕", color = TuvimaTokens.textPrimary, fontSize = 18.sp) }
            }
            val middle = Modifier.weight(1f).fillMaxWidth()
            Column(if (scroll) middle.verticalScroll(rememberScrollState()) else middle) { body() }
            Column(Modifier.fillMaxWidth().padding(bottom = 8.dp)) { footer() }
        }
    }
}

// ---- a panel built by the television's builders, drawn for touch ----

/**
 * Draws a [TvPanel] (Episodes, Audio & subtitles, Subtitle appearance, Playback settings, Skip choices, Playback information, Chapters).
 * A tap on a row calls [onRow] with its key (and the key of one of its buttons); a segment calls [onChoose]; a switch calls [onSwitch].
 */
@Composable
internal fun PanelSheet(
    client: TuvimaClient?,
    panel: TvPanel,
    onClose: () -> Unit,
    onBack: () -> Unit,
    onRow: (key: String, action: String?) -> Unit,
    onChoose: (segmentKey: String, optionKey: String) -> Unit = { _, _ -> },
    onSwitch: (key: String) -> Unit = {},
) {
    PlayerSheet(
        title = panel.title,
        onClose = onClose,
        onBack = if (panel.back) onBack else null,
        footer = { panel.footer.forEach { Item(client, it, onRow, onChoose, onSwitch) } },
    ) {
        panel.body.forEach { Item(client, it, onRow, onChoose, onSwitch) }
    }
}

@Composable
private fun Item(
    client: TuvimaClient?,
    item: com.tuvima.library.core.TvItem,
    onRow: (String, String?) -> Unit,
    onChoose: (String, String) -> Unit,
    onSwitch: (String) -> Unit,
) {
    when (item) {
        is TvLabel -> Text(
            item.text,
            color = TuvimaTokens.textPrimary,
            fontWeight = FontWeight.Bold,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        )
        is TvNote -> Text(item.text, color = MutedText, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp))
        is TvInfoLine -> Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(item.label, color = MutedText)
            Text(item.value, color = TuvimaTokens.textPrimary)
        }
        is TvRow -> PanelRow(client, item, onRow)
        is TvSegment -> Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            item.options.forEach { option ->
                val selected = option.key == item.selectedKey
                val pick = { onChoose(item.key, option.key) }
                if (selected) {
                    Button(onClick = pick, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(option.label) }
                } else {
                    OutlinedButton(onClick = pick, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(option.label) }
                }
            }
        }
        is TvSwitch -> Row(
            Modifier.fillMaxWidth().defaultMinSize(minHeight = 48.dp).clickable { onSwitch(item.key) }.padding(horizontal = 16.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(item.label, color = TuvimaTokens.textPrimary, modifier = Modifier.weight(1f))
            Switch(checked = item.on, onCheckedChange = { onSwitch(item.key) })
        }
    }
}

@Composable
private fun PanelRow(client: TuvimaClient?, row: TvRow, onRow: (String, String?) -> Unit) {
    if (row.primary) {
        Button(
            onClick = { onRow(row.key, null) },
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp).defaultMinSize(minHeight = 48.dp),
        ) { Text("+  " + row.title) }
        return
    }
    val background = if (row.current) TuvimaTokens.bgSurfaceHover else Color.Transparent
    Row(
        Modifier.fillMaxWidth().background(background).defaultMinSize(minHeight = 56.dp)
            .clickable { onRow(row.key, null) }.padding(horizontal = 16.dp, vertical = 8.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (row.showImage && client != null) {
            RemoteImage(client, row.imageUrl, Modifier.width(72.dp).height(44.dp).clip(RoundedCornerShape(6.dp)))
            Spacer(Modifier.width(12.dp))
        }
        Column(Modifier.weight(1f)) {
            Text(row.title, color = TuvimaTokens.textPrimary, maxLines = 2, overflow = TextOverflow.Ellipsis)
            row.sub?.let { Text(it, color = MutedText, style = MaterialTheme.typography.bodySmall, maxLines = 2, overflow = TextOverflow.Ellipsis) }
        }
        row.chip?.let { Text(it, color = Accent, style = MaterialTheme.typography.labelMedium, modifier = Modifier.padding(horizontal = 8.dp)) }
        if (row.checked) Text("✓", color = TuvimaTokens.textPrimary, modifier = Modifier.padding(horizontal = 8.dp))
        row.trailing?.let { Text(it, color = MutedText, modifier = Modifier.padding(start = 8.dp)) }
        row.actions.forEach { action ->
            TextButton(onClick = { onRow(row.key, action.key) }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) { Text(action.label) }
        }
    }
}

// ---- Playback speed ----

/** The contract's Playback speed panel: the current speed, a slider from 0.5× to 3× in steps of 0.05, the presets, and Reset to 1×. */
@Composable
internal fun SpeedSheet(
    speed: Double,
    onSet: (Double) -> Unit,
    onClose: () -> Unit,
    onBack: (() -> Unit)?,
    /** When given, dragging the slider only previews the speed through this and [onSet] (which also saves it) runs once on release. */
    onPreview: ((Double) -> Unit)? = null,
) {
    val current = TvPlayerLogic.clampSpeed(speed)
    PlayerSheet(
        title = TvPlayerWords.PLAYBACK_SPEED,
        onClose = onClose,
        onBack = onBack,
        footer = {
            TextButton(onClick = { onSet(1.0) }, modifier = Modifier.padding(start = 8.dp).defaultMinSize(minHeight = 44.dp)) {
                Text(TvPlayerWords.RESET_SPEED, color = Accent)
            }
        },
    ) {
        Text(TvPlayerWords.CURRENT_SPEED, color = MutedText, modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        Text(
            TvPlayerLogic.speedLabel(current),
            color = TuvimaTokens.textPrimary,
            style = MaterialTheme.typography.headlineMedium,
            modifier = Modifier.padding(horizontal = 16.dp),
        )
        Slider(
            value = current.toFloat(),
            onValueChange = { (onPreview ?: onSet)(TvPlayerLogic.clampSpeed(it.toDouble())) },
            onValueChangeFinished = { if (onPreview != null) onSet(current) },
            valueRange = TvPlayerLogic.SPEED_MIN.toFloat()..TvPlayerLogic.SPEED_MAX.toFloat(),
            steps = 49,
            modifier = Modifier.padding(horizontal = 16.dp).semantics { contentDescription = TvPlayerWords.PLAYBACK_SPEED },
        )
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly) {
            TvPlayerLogic.SPEED_PRESETS.forEach { preset ->
                val ticked = current == preset
                TextButton(onClick = { onSet(preset) }, modifier = Modifier.defaultMinSize(minWidth = 44.dp, minHeight = 44.dp)) {
                    Text(
                        TvPlayerLogic.speedLabel(preset) + if (ticked) " ✓" else "",
                        color = if (ticked) Accent else TuvimaTokens.textPrimary,
                    )
                }
            }
        }
    }
}

// ---- Bookmarks ----

/**
 * The Bookmarks panel (contract words): "Bookmark at 34:12" saves at once with no note, an "Add a note…" field, Mine | Global when a Global set
 * exists, rows with the frame or the cover, Edit (name and note) and Delete (asks once: Delete, then Delete or Keep) on Mine rows, a search box above
 * eight rows, and the "Show on timeline" switch. This sheet holds only what is being typed; the host owns the list and makes the calls.
 */
@Composable
internal fun BookmarksSheet(
    client: TuvimaClient?,
    items: List<TvBookmark>,
    loaded: Boolean,
    saveFailed: Boolean,
    positionSeconds: Double,
    showOnTimeline: Boolean,
    coverUrl: String?,
    onAdd: (note: String) -> Unit,
    onSeek: (Double) -> Unit,
    onEdit: (id: String, name: String, note: String) -> Unit,
    onDelete: (id: String) -> Unit,
    onToggleTimeline: (Boolean) -> Unit,
    onClose: () -> Unit,
) {
    var tab by remember { mutableStateOf(TvBookmarkTab.Mine) }
    var query by remember { mutableStateOf("") }
    var note by remember { mutableStateOf("") }
    var editing by remember { mutableStateOf<String?>(null) }
    var editName by remember { mutableStateOf("") }
    var editNote by remember { mutableStateOf("") }
    var confirmDelete by remember { mutableStateOf<String?>(null) }
    val list = PhoneBookmarks.list(items, tab, query, loaded)
    PlayerSheet(
        title = PhoneBookmarks.BOOKMARKS,
        onClose = onClose,
        footer = {
            Row(
                Modifier.fillMaxWidth().defaultMinSize(minHeight = 48.dp).clickable { onToggleTimeline(!showOnTimeline) }.padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(TvPlayerWords.SHOW_ON_TIMELINE, color = TuvimaTokens.textPrimary, modifier = Modifier.weight(1f))
                Switch(checked = showOnTimeline, onCheckedChange = { onToggleTimeline(it) })
            }
        },
    ) {
        if (list.showChoice) {
            Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(TvBookmarkTab.Mine to list.mineLabel, TvBookmarkTab.Global to list.globalLabel).forEach { (value, label) ->
                    if (list.shown == value) {
                        Button(onClick = { tab = value }, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(label) }
                    } else {
                        OutlinedButton(onClick = { tab = value }, modifier = Modifier.weight(1f).defaultMinSize(minHeight = 44.dp)) { Text(label) }
                    }
                }
            }
        }
        Button(
            onClick = {
                onAdd(note)
                note = ""
            },
            enabled = PhoneBookmarks.fits(note),
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp).defaultMinSize(minHeight = 48.dp),
        ) { Text("+  " + TvPanelsBookmarkAt(positionSeconds)) }
        OutlinedTextField(
            value = note,
            onValueChange = { note = it.take(PhoneBookmarks.MAX_TEXT) },
            placeholder = { Text(PhoneBookmarks.ADD_A_NOTE) },
            singleLine = true,
            modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
        )
        if (saveFailed) Text(TvPlayerWords.COULD_NOT_SAVE, color = Color(0xFFFF6B6B), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp))
        if (list.showSearch) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                placeholder = { Text(PhoneBookmarks.SEARCH) },
                singleLine = true,
                modifier = Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp),
            )
        }
        list.emptyText?.let { Text(it, color = MutedText, modifier = Modifier.padding(16.dp)) }
        list.rows.forEach { row ->
            Column(Modifier.fillMaxWidth()) {
                Row(
                    Modifier.fillMaxWidth().defaultMinSize(minHeight = 60.dp).clickable { onSeek(row.positionSeconds) }.padding(horizontal = 16.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    if (client != null) {
                        // A frame from that moment for a video, the cover for an audiobook.
                        RemoteImage(client, row.frameUrl ?: coverUrl, Modifier.width(72.dp).height(44.dp).clip(RoundedCornerShape(6.dp)))
                        Spacer(Modifier.width(12.dp))
                    }
                    Column(Modifier.weight(1f)) {
                        Text(row.time, color = TuvimaTokens.textPrimary, fontWeight = FontWeight.Bold)
                        Text(
                            listOfNotNull(row.words, row.byline).joinToString(" · "),
                            color = MutedText,
                            style = MaterialTheme.typography.bodySmall,
                            maxLines = 2,
                            overflow = TextOverflow.Ellipsis,
                        )
                    }
                    if (row.canChange) {
                        if (confirmDelete == row.id) {
                            TextButton(onClick = { confirmDelete = null; onDelete(row.id) }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) { Text(TvPlayerWords.DELETE) }
                            TextButton(onClick = { confirmDelete = null }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) { Text(TvPlayerWords.KEEP) }
                        } else {
                            if (!row.waiting) {
                                TextButton(
                                    onClick = {
                                        editing = row.id
                                        editName = row.label
                                        editNote = row.note
                                    },
                                    modifier = Modifier.defaultMinSize(minHeight = 44.dp),
                                ) { Text(PhoneBookmarks.EDIT) }
                            }
                            TextButton(onClick = { confirmDelete = row.id }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) { Text(TvPlayerWords.DELETE) }
                        }
                    }
                }
                if (editing == row.id) {
                    Column(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 4.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        OutlinedTextField(
                            value = editName,
                            onValueChange = { editName = it.take(PhoneBookmarks.MAX_TEXT) },
                            label = { Text(PhoneBookmarks.NAME) },
                            singleLine = true,
                            modifier = Modifier.fillMaxWidth(),
                        )
                        OutlinedTextField(
                            value = editNote,
                            onValueChange = { editNote = it.take(PhoneBookmarks.MAX_TEXT) },
                            label = { Text(PhoneBookmarks.NOTE) },
                            modifier = Modifier.fillMaxWidth(),
                        )
                        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                            Button(
                                onClick = {
                                    onEdit(row.id, editName, editNote)
                                    editing = null
                                },
                                modifier = Modifier.defaultMinSize(minHeight = 44.dp),
                            ) { Text(PhoneBookmarks.SAVE) }
                            OutlinedButton(onClick = { editing = null }, modifier = Modifier.defaultMinSize(minHeight = 44.dp)) { Text(PhoneBookmarks.CANCEL) }
                        }
                    }
                }
            }
        }
        if (list.rows.isNotEmpty()) {
            Text(TvPlayerWords.SELECT_BOOKMARK, color = MutedText, style = MaterialTheme.typography.bodySmall, modifier = Modifier.padding(16.dp))
        }
    }
}

private fun TvPanelsBookmarkAt(positionSeconds: Double): String = com.tuvima.library.core.TvPanels.bookmarkAtLabel(positionSeconds)
