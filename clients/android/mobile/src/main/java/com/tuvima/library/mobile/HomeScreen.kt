package com.tuvima.library.mobile

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.heightIn
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
import com.tuvima.library.core.MyListLogic
import com.tuvima.library.core.TuvimaClient
import kotlinx.coroutines.CancellationException

/**
 * Home: the Continue row first (from /display/continue), then a My List row (when the profile has saved titles), then the shelves of
 * /display/home. The My List row reads again after any plus or tick, so it never shows a title that was just removed.
 */
@Composable
internal fun HomeScreen(client: TuvimaClient, saved: SavedItems, openCard: (DisplayCard) -> Unit, openMyList: () -> Unit) {
    var attempt by remember { mutableIntStateOf(0) }
    var state by remember { mutableStateOf<Load<List<DisplayShelf>>>(Load.Loading) }
    var myList by remember { mutableStateOf<DisplayShelf?>(null) }

    LaunchedEffect(client, saved.version) {
        saved.ensureLoaded()
        // Best effort: without the row Home is still whole; the row comes back on the next change or visit.
        val page = runCatching { client.myListPage(limit = MY_LIST_ROW_SIZE) }.getOrNull()
        myList = page?.cards?.takeIf { it.isNotEmpty() }?.let { cards ->
            DisplayShelf(key = "my-list", title = MyListLogic.TITLE, subtitle = null, items = cards, seeAllRoute = null)
        }
    }

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
                val row = myList
                val withMyList = if (row == null) shelves else {
                    // My List sits right after Continue when Continue leads, otherwise first.
                    val at = if (shelves.firstOrNull()?.key == "continue") 1 else 0
                    shelves.toMutableList().apply { add(at, row) }
                }
                items(withMyList) { shelf ->
                    ShelfRow(client, shelf, openCard, onTitleClick = if (shelf.key == "my-list") openMyList else null)
                }
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
    // The Engine's own "my-list" shelf is dropped too: Home draws its own My List row, kept current after every plus or tick.
    val rest = home.shelves.filter { it.key != "continue" && it.key != "my-list" && it.items.isNotEmpty() }
    return listOfNotNull(continueShelf) + rest
}

private const val MY_LIST_ROW_SIZE = 20

/** A titled row of posters. [onTitleClick] makes the title open the full list behind the row (My List). */
@Composable
internal fun ShelfRow(
    client: TuvimaClient,
    shelf: DisplayShelf,
    openCard: (DisplayCard) -> Unit,
    onTitleClick: (() -> Unit)? = null,
) {
    Column(Modifier.padding(bottom = 20.dp)) {
        Text(
            shelf.title,
            style = MaterialTheme.typography.titleLarge,
            modifier = (if (onTitleClick != null) Modifier.clickable(onClick = onTitleClick) else Modifier)
                .heightIn(min = TuvimaTokens.tapTarget)
                .padding(horizontal = 16.dp, vertical = 8.dp),
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
