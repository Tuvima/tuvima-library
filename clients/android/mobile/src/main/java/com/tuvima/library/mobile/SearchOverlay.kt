package com.tuvima.library.mobile

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalSoftwareKeyboardController
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.tuvima.library.core.SearchHit
import com.tuvima.library.core.SearchLogic
import com.tuvima.library.core.SearchResults
import com.tuvima.library.core.SearchRow
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.delay

/**
 * The full-screen search the dock's Search tab opens: a text box with Cancel, and the results as one column under a heading per kind.
 * Typing is searched once it pauses (the keyboard's search key only closes the keyboard); a press on a result calls [onOpen] and
 * Cancel, Back or the clear button never leave a half-typed search behind.
 */
@Composable
internal fun SearchOverlay(client: TuvimaClient, onOpen: (SearchHit) -> Unit, onClose: () -> Unit) {
    BackHandler(onBack = onClose)
    val keyboard = LocalSoftwareKeyboardController.current
    val focus = remember { FocusRequester() }
    var text by remember { mutableStateOf("") }
    var attempt by remember { mutableIntStateOf(0) }
    // The text the shown answer was asked for, so "No results for ..." never names what was typed since.
    var sent by remember { mutableStateOf("") }
    var state by remember { mutableStateOf<Load<SearchResults>?>(null) }

    LaunchedEffect(Unit) { focus.requestFocus() }
    LaunchedEffect(text, attempt) {
        val query = SearchLogic.queryToSend(text)
        if (query == null) {
            state = null
            return@LaunchedEffect
        }
        delay(SearchLogic.DEBOUNCE_MS)
        sent = query
        state = Load.Loading
        state = try {
            Load.Ready(client.searchResults(query))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (failure: Exception) {
            Load.Failed(userMessage(failure))
        }
    }

    val clearButton: (@Composable () -> Unit)? = if (text.isEmpty()) {
        null
    } else {
        { BareIconButton(SearchLogic.CLEAR, { text = "" }) { Text("✕", color = TuvimaTokens.textPrimary, fontSize = 18.sp) } }
    }

    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(Modifier.fillMaxSize().statusBarsPadding().navigationBarsPadding().imePadding()) {
            Row(
                Modifier.fillMaxWidth().padding(start = 12.dp, end = 4.dp, top = 8.dp, bottom = 4.dp),
                horizontalArrangement = Arrangement.spacedBy(4.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                OutlinedTextField(
                    value = text,
                    onValueChange = { text = it },
                    placeholder = { Text(SearchLogic.PLACEHOLDER) },
                    singleLine = true,
                    trailingIcon = clearButton,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { keyboard?.hide() }),
                    modifier = Modifier.weight(1f).focusRequester(focus),
                )
                TextButton(onClick = onClose, modifier = Modifier.heightIn(min = TuvimaTokens.tapTarget)) { Text(SearchLogic.CANCEL) }
            }
            when (val current = state) {
                null -> Text(
                    SearchLogic.HINT,
                    style = MaterialTheme.typography.bodyMedium,
                    color = TuvimaTokens.textMuted,
                    modifier = Modifier.padding(16.dp),
                )
                is Load.Loading -> Box(Modifier.fillMaxWidth().padding(24.dp), contentAlignment = Alignment.Center) {
                    CircularProgressIndicator()
                }
                is Load.Failed -> ErrorBlock(current.message) { attempt += 1 }
                is Load.Ready -> {
                    val rows = remember(current.value) { SearchLogic.rows(current.value) }
                    if (rows.none { it is SearchRow.Hit }) {
                        Text(
                            SearchLogic.noResults(sent),
                            style = MaterialTheme.typography.bodyMedium,
                            modifier = Modifier.padding(16.dp),
                        )
                    } else {
                        LazyColumn(Modifier.fillMaxSize()) {
                            itemsIndexed(rows) { _, row ->
                                when (row) {
                                    is SearchRow.Heading -> Text(
                                        row.title,
                                        style = MaterialTheme.typography.titleSmall,
                                        color = TuvimaTokens.textSecondaryColor,
                                        modifier = Modifier.padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 4.dp),
                                    )
                                    is SearchRow.Hit -> SearchHitRow(client, row.hit) { onOpen(row.hit) }
                                }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** One result: a small picture, the title and one grey line, the whole row a 56 dp target. */
@Composable
private fun SearchHitRow(client: TuvimaClient, hit: SearchHit, onClick: () -> Unit) {
    Row(
        Modifier
            .fillMaxWidth()
            .heightIn(min = 64.dp)
            .clickable(onClickLabel = "Open ${hit.title}", onClick = onClick)
            .padding(horizontal = 16.dp, vertical = 6.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        RemoteImage(client, hit.artworkUrl, Modifier.size(width = 44.dp, height = 56.dp).clip(RoundedCornerShape(6.dp)))
        Column(Modifier.weight(1f)) {
            Text(hit.title, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodyLarge)
            hit.line?.let {
                Text(it, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.bodySmall, color = TuvimaTokens.textMuted)
            }
        }
    }
}
