package com.tuvima.library.mobile

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import com.tuvima.library.core.DisplayCard
import com.tuvima.library.core.DisplayShelf
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException

/** Home: the Continue row first (from /display/continue), then the shelves of /display/home. */
@Composable
internal fun HomeScreen(client: TuvimaClient, openCard: (DisplayCard) -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<List<DisplayShelf>>>(Load.Loading) }

    LaunchedEffect(client, attempt) {
        state = Load.Loading
        state = try {
            Load.Ready(loadHomeShelves(client))
        } catch (cancelled: CancellationException) {
            throw cancelled
        } catch (error: Exception) {
            Load.Failed(userMessage(error))
        }
    }

    LoadView(state, retry = { attempt += 1 }) { shelves ->
        if (shelves.isEmpty()) {
            ErrorBlock("Nothing to show yet. Your library is empty or still being prepared.", { attempt += 1 })
        } else {
            LazyColumn(Modifier.fillMaxSize(), contentPadding = PaddingValues(vertical = 16.dp)) {
                item {
                    Text(
                        "Home",
                        style = MaterialTheme.typography.headlineMedium,
                        modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
                    )
                }
                items(shelves) { shelf -> ShelfRow(client, shelf, openCard) }
            }
        }
    }
}

private suspend fun loadHomeShelves(client: TuvimaClient): List<DisplayShelf> {
    // Continue is its own call so it can lead; Home's own copy of that shelf is dropped to avoid showing it twice.
    val continueShelf = runCatching { client.continueAll() }.getOrNull()
        ?.shelves?.firstOrNull { it.items.isNotEmpty() }
        ?.copy(key = "continue", title = "Continue")
    val home = client.homeShelves()
    val rest = home.shelves.filter { it.key != "continue" && it.items.isNotEmpty() }
    return listOfNotNull(continueShelf) + rest
}

/** A titled row of posters. */
@Composable
internal fun ShelfRow(client: TuvimaClient, shelf: DisplayShelf, openCard: (DisplayCard) -> Unit) {
    Column(Modifier.padding(bottom = 20.dp)) {
        Text(
            shelf.title,
            style = MaterialTheme.typography.titleLarge,
            modifier = Modifier.padding(horizontal = 16.dp, vertical = 8.dp),
        )
        LazyRow(
            contentPadding = PaddingValues(horizontal = 16.dp),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            items(shelf.items) { card ->
                PosterTile(client, card, Modifier.width(120.dp)) { openCard(card) }
            }
        }
    }
}
