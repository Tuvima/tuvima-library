package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

/** Search across the library (/display/search); a result opens its details. */
@Composable
internal fun SearchScreen(client: TuvimaClient, openDetail: (entityType: String, id: String) -> Unit) {
    val scope = rememberCoroutineScope()
    var query by remember { mutableStateOf("") }
    var results by remember { mutableStateOf<Load<List<MobileSearchResult>>?>(null) }

    fun submit() {
        val text = query.trim()
        if (text.isBlank()) return
        results = Load.Loading
        scope.launch {
            results = try {
                Load.Ready(parseMobileSearch(client.search(text)))
            } catch (cancelled: CancellationException) {
                throw cancelled
            } catch (failure: Exception) {
                Load.Failed(userMessage(failure))
            }
        }
    }

    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Text("Search", style = MaterialTheme.typography.headlineMedium)
        Row(
            Modifier.fillMaxWidth().padding(top = 12.dp),
            horizontalArrangement = Arrangement.spacedBy(8.dp),
        ) {
            OutlinedTextField(
                value = query,
                onValueChange = { query = it },
                label = { Text("Search your library") },
                singleLine = true,
                keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                keyboardActions = KeyboardActions(onSearch = { submit() }),
                modifier = Modifier.weight(1f),
            )
            Button(onClick = { submit() }) { Text("Search") }
        }
        Spacer(Modifier.height(12.dp))
        when (val current = results) {
            null -> Text("Find movies, shows, books, comics, music and audiobooks you own.")
            is Load.Loading -> CircularProgressIndicator()
            is Load.Failed -> Text(current.message)
            is Load.Ready -> if (current.value.isEmpty()) {
                Text("No results for “${query.trim()}”.")
            } else {
                LazyColumn(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    items(current.value) { result ->
                        Card(Modifier.fillMaxWidth().clickable { openDetail(result.entityType, result.id) }) {
                            Column(Modifier.padding(16.dp)) {
                                Text(result.title)
                                result.subtitle?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                            }
                        }
                    }
                }
            }
        }
    }
}

/** Downloads: a placeholder until the offline downloads screen is built. */
@Composable
internal fun DownloadsScreen() {
    Column(Modifier.fillMaxSize().padding(24.dp), verticalArrangement = Arrangement.Center) {
        Text("Downloads", style = MaterialTheme.typography.headlineMedium)
        Spacer(Modifier.height(12.dp))
        Text(
            "Offline downloads will appear here. You can already tap Download on a title to prepare an offline copy; " +
                "the list of what is saved on this phone arrives in a later update.",
        )
    }
}
