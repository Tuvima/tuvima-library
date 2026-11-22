package com.tuvima.library.mobile

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.CheckTone
import com.tuvima.library.core.FindAnotherPane
import com.tuvima.library.core.FindAnotherPhase
import com.tuvima.library.core.FindAnotherRow
import com.tuvima.library.core.FindAnotherRows
import com.tuvima.library.core.FindAnotherSheet
import com.tuvima.library.core.FindAnotherState
import com.tuvima.library.core.FindAnotherWording

// The Find another bottom sheet of the phone's video player (spec "Find another on TV", WP8; Find another v1 D12): the same
// rows as the television window in one column (release name, "Hearing impaired", the checks, "Best match" and "In use", the
// words of the row inside the row), a Language button, and Cancel and Use this pinned at the bottom. Dark only, controls at
// least 44dp tall, no toasts. These composables only draw: what a tap does lives in the shared FindAnotherModel.

private val Ok = Color(0xFF6FD08C)
private val Warn = Color(0xFFF2B84B)
private val Bad = Color(0xFFFF6B6B)
private val SheetShape = RoundedCornerShape(TuvimaTokens.radiusXl)

private fun toneColor(tone: CheckTone): Color = when (tone) {
    CheckTone.Ok -> Ok
    CheckTone.Warn -> Warn
    CheckTone.Bad -> Bad
    CheckTone.Neutral -> TuvimaTokens.textMuted
}

/** The footer row of the Audio & subtitles view ("Find subtitles…" or "Find lyrics…"): at least 48dp tall. */
@Composable
internal fun FindAnotherEntryRow(label: String, onClick: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().defaultMinSize(minHeight = TuvimaTokens.touchArea).clickable(onClick = onClick).padding(horizontal = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Text(label, color = TuvimaTokens.accentPrimaryHover, fontWeight = FontWeight.Bold, modifier = Modifier.padding(start = 28.dp))
    }
}

/**
 * The sheet. [onTapRow], [onUseThis], [onCancel] and [onChooseLanguage] call the shared model; [onDismiss] is a swipe down or a
 * tap outside (the host closes the sheet the same way as Cancel).
 */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
internal fun FindAnotherBottomSheet(
    state: FindAnotherState,
    itemName: String?,
    languageChoices: List<String>,
    onTapRow: (Int) -> Unit,
    onUseThis: () -> Unit,
    onCancel: () -> Unit,
    onChooseLanguage: (Int) -> Unit,
    onDismiss: () -> Unit,
) {
    var languagesOpen by remember { mutableStateOf(false) }
    val rows = FindAnotherRows.rows(state.items, state.kind)
    ModalBottomSheet(
        onDismissRequest = onDismiss,
        sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true),
        containerColor = TuvimaTokens.bgSurface,
    ) {
        // The list scrolls; the header above it and Cancel and Use this below it stay where they are.
        Column(Modifier.fillMaxWidth().fillMaxHeight(0.92f)) {
            SheetHeader(state, itemName, languagesOpen) { languagesOpen = !languagesOpen }
            Box(Modifier.weight(1f).fillMaxWidth()) {
                if (languagesOpen) {
                    LanguageChoices(languageChoices, state.language) { index ->
                        languagesOpen = false
                        onChooseLanguage(index)
                    }
                } else {
                    ListArea(state, rows, onTapRow)
                }
            }
            SheetFooter(state, hasRows = rows.isNotEmpty(), onUseThis = onUseThis, onCancel = onCancel)
        }
    }
}

@Composable
private fun SheetHeader(state: FindAnotherState, itemName: String?, languagesOpen: Boolean, onLanguage: () -> Unit) {
    Row(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            itemName?.takeIf { it.isNotBlank() }?.let {
                Text(it, color = TuvimaTokens.textMuted, style = MaterialTheme.typography.bodySmall, maxLines = 1)
            }
            Text(FindAnotherWording.windowTitle(state.kind), color = Color.White, style = MaterialTheme.typography.titleLarge)
        }
        if (state.keys.languageAvailable) {
            OutlinedButton(onClick = onLanguage, modifier = Modifier.defaultMinSize(minHeight = TuvimaTokens.controlHeight)) {
                Text(if (languagesOpen) "Back to the list" else "Language: ${state.language}", color = Color.White)
            }
        }
    }
}

@Composable
private fun LanguageChoices(choices: List<String>, current: String, onChoose: (Int) -> Unit) {
    LazyColumn(Modifier.fillMaxSize()) {
        itemsIndexed(choices) { index, name ->
            val selected = name.equals(current, ignoreCase = true)
            Row(
                Modifier
                    .fillMaxWidth()
                    .defaultMinSize(minHeight = TuvimaTokens.touchArea)
                    .selectable(selected = selected, role = Role.RadioButton, onClick = { onChoose(index) })
                    .padding(horizontal = 16.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(if (selected) "✓" else "", color = Color.White, modifier = Modifier.width(28.dp))
                Text(name, color = Color.White)
            }
        }
    }
}

@Composable
private fun ListArea(state: FindAnotherState, rows: List<FindAnotherRow>, onTapRow: (Int) -> Unit) {
    when (state.phase) {
        FindAnotherPhase.Loading -> CenteredText(FindAnotherWording.loadingText(state.kind))
        FindAnotherPhase.Problem -> CenteredText(state.problem.orEmpty())
        FindAnotherPhase.Rows -> LazyColumn(
            Modifier.fillMaxSize(),
            verticalArrangement = Arrangement.spacedBy(8.dp),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 8.dp),
        ) {
            itemsIndexed(rows, key = { _, row -> row.key }) { index, row ->
                ResultRow(
                    row = row,
                    selected = index == state.keys.row,
                    fullWords = state.keys.fullWords,
                    reading = row.key in state.reading,
                    failed = row.key in state.failed,
                    enabled = !state.applying,
                    onTap = { onTapRow(index) },
                )
            }
        }
    }
}

@Composable
private fun CenteredText(text: String) {
    Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
        Text(text, color = Color.White, textAlign = TextAlign.Center, modifier = Modifier.padding(24.dp))
    }
}

@Composable
private fun ResultRow(row: FindAnotherRow, selected: Boolean, fullWords: Boolean, reading: Boolean, failed: Boolean, enabled: Boolean, onTap: () -> Unit) {
    // The selected row wears the purple outline; a tap reads it (when it has not been read) and selects it.
    Column(
        Modifier
            .fillMaxWidth()
            .defaultMinSize(minHeight = TuvimaTokens.touchArea)
            .border(if (selected) 2.dp else 0.dp, if (selected) TuvimaTokens.accentPrimary else Color.Transparent, SheetShape)
            .background(TuvimaTokens.bgSurfaceElevated, SheetShape)
            .selectable(selected = selected, enabled = enabled, role = Role.RadioButton, onClick = onTap)
            .padding(12.dp),
        verticalArrangement = Arrangement.spacedBy(4.dp),
    ) {
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.Top) {
            // Long release names wrap; nothing is cut off.
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(row.title, color = Color.White, fontWeight = FontWeight.Bold)
                row.meta?.let { Text(it, color = TuvimaTokens.textMuted, fontSize = 13.sp) }
                row.checks.forEach { check -> Text(check.text, color = toneColor(check.tone), fontSize = 13.sp) }
            }
            // "Best match" and "In use" sit at the right of their rows.
            Column(Modifier.padding(start = 8.dp), horizontalAlignment = Alignment.End, verticalArrangement = Arrangement.spacedBy(4.dp)) {
                row.bestMatchChip?.let { Chip(it) }
                row.inUseChip?.let { Chip(it) }
            }
        }
        val pane = FindAnotherSheet.rowPane(row.item, selected, fullWords, reading, failed)
        when (pane) {
            null -> Unit
            is FindAnotherPane.Prompt -> Text(pane.text, color = TuvimaTokens.textMuted, fontSize = 13.sp)
            FindAnotherPane.Reading -> Text(FindAnotherWording.READING, color = TuvimaTokens.textMuted, fontSize = 13.sp)
            FindAnotherPane.CouldNotRead -> Text(FindAnotherWording.COULD_NOT_READ, color = TuvimaTokens.textMuted, fontSize = 13.sp)
            is FindAnotherPane.Lines -> Column(Modifier.padding(top = 4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
                pane.lines.forEach { line ->
                    Row {
                        line.atSeconds?.let {
                            Text(FindAnotherWording.clock(it), color = TuvimaTokens.accentPrimaryHover, fontSize = 13.sp, modifier = Modifier.width(52.dp))
                        }
                        Text(line.text, color = Color.White, fontSize = 14.sp)
                    }
                }
            }
        }
    }
}

@Composable
private fun Chip(text: String) {
    Text(
        text,
        color = TuvimaTokens.textOnAccent,
        fontSize = 12.sp,
        fontWeight = FontWeight.Bold,
        modifier = Modifier.background(TuvimaTokens.accentPrimary, RoundedCornerShape(50)).padding(horizontal = 10.dp, vertical = 3.dp),
    )
}

/** Cancel and Use this, pinned below the list. The app's normal "Updating…" shows while the choice is applied. */
@Composable
private fun SheetFooter(state: FindAnotherState, hasRows: Boolean, onUseThis: () -> Unit, onCancel: () -> Unit) {
    Column(Modifier.fillMaxWidth().background(TuvimaTokens.bgSurface).padding(horizontal = 16.dp, vertical = 8.dp)) {
        val note = if (state.applying) "Updating…" else state.applyProblem
        note?.let {
            Text(
                it,
                color = if (state.applying) TuvimaTokens.textMuted else Warn,
                fontSize = 13.sp,
                modifier = Modifier.padding(bottom = 8.dp),
            )
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            OutlinedButton(
                onClick = onCancel,
                enabled = !state.applying,
                modifier = Modifier.weight(1f).defaultMinSize(minHeight = TuvimaTokens.controlHeight),
            ) { Text(FindAnotherWording.CANCEL, color = Color.White) }
            if (hasRows) {
                Button(
                    onClick = onUseThis,
                    enabled = !state.applying,
                    modifier = Modifier.weight(1f).defaultMinSize(minHeight = TuvimaTokens.controlHeight),
                ) { Text(FindAnotherWording.USE_THIS) }
            }
        }
    }
}
