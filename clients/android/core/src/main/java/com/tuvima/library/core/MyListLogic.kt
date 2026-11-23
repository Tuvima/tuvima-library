package com.tuvima.library.core

import org.json.JSONObject

// The My List page: the Engine's `personal/my-list` grid read as the Dashboard reads it, the kind chips, sort choices, count line and
// the Undo offer. The words are the Dashboard's. Pure functions; nothing here draws anything.

/** The sorts the My List grid accepts (`DisplayGridValues.Sort*`), in the Dashboard's order, with its labels. */
enum class MyListSort(val wire: String, val label: String) {
    Added("added", "Date added"),
    Title("title", "Title"),
    Year("year", "Release year"),
    ;

    companion object {
        /** The sort a stored or returned value stands for; the Engine's default (Date added) when it is not one of ours. */
        fun fromWire(value: String?): MyListSort = entries.firstOrNull { it.wire.equals(value?.trim(), ignoreCase = true) } ?: Added
    }
}

/** One kind chip: the Engine's own `type` filter option with its count. */
data class MyListKind(val value: String, val label: String, val count: Int, val selected: Boolean)

/** One page of the My List grid. */
data class MyListPage(
    val cards: List<DisplayCard>,
    val totalCount: Int,
    val hasMore: Boolean,
    val sort: MyListSort,
    val kinds: List<MyListKind>,
)

object MyListLogic {
    /** The grid's lane and shelf key (`personal/my-list`), the same list the Dashboard shows. */
    const val LANE = "personal"
    const val SHELF_KEY = "my-list"
    const val PAGE_SIZE = 48

    /** How long an Undo line stays after a removal (the Dashboard's is about ten seconds). */
    const val UNDO_SECONDS = 10

    const val TITLE = "My List"
    const val ADD = "Add to My List"
    const val REMOVE = "Remove from My List"
    const val ALL = "All"
    const val SORT_BY = "Sort by"
    const val KINDS_LABEL = "Filter by kind"
    const val REMOVE_FINISHED = "Remove finished"
    const val NOTHING_FINISHED = "Nothing finished yet"
    const val CONTINUE = "Continue"
    const val EMPTY_TITLE = "Your list is empty"
    const val EMPTY_MESSAGE = "Press + on any title to save it for later."
    const val BROWSE_HOME = "Browse Home"
    const val COULD_NOT_LOAD = "My List could not load"
    const val COULD_NOT_CHANGE = "My List could not be changed. Try again."
    const val UNDO = "Undo"

    /** Reads one page of `GET /api/v1/display/grids/personal/my-list`. */
    fun parsePage(json: JSONObject, absolute: (String?) -> String?): MyListPage {
        val cards = json.optJSONArray("items")?.objects { parseDisplayCard(it, absolute) }.orEmpty()
        val typeGroup = json.optJSONArray("filters")?.objects { it }.orEmpty().firstOrNull { it.optString("key") == "type" }
        val kinds = typeGroup?.optJSONArray("options")?.objects { option ->
            MyListKind(
                value = option.optString("value"),
                label = option.optString("label"),
                count = option.optInt("count"),
                selected = option.optBoolean("selected"),
            )
        }.orEmpty().filter { it.value.isNotBlank() }
        return MyListPage(
            cards = cards,
            totalCount = json.optInt("totalCount", cards.size),
            hasMore = json.optBoolean("hasMore", false),
            sort = MyListSort.fromWire(json.optionalString("sort")),
            kinds = kinds,
        )
    }

    /** The kinds worth a chip: those with something in them, and the picked one even when it has emptied. */
    fun visibleKinds(kinds: List<MyListKind>): List<MyListKind> = kinds.filter { it.count > 0 || it.selected }

    /** With one kind saved there is nothing to narrow, so the chip row is hidden. */
    fun showsChips(kinds: List<MyListKind>): Boolean = visibleKinds(kinds).size >= 2

    /** The kinds' counts ignore the picked kind, so together they are the whole list. */
    fun allCount(kinds: List<MyListKind>, totalCount: Int): Int =
        if (kinds.isEmpty()) totalCount else kinds.sumOf { it.count }

    /** "1 title" or "12 titles"; with a kind picked, "9 movies", "4 TV shows", "1 album". Empty while nothing has loaded. */
    fun countText(total: Int, pickedKind: String?): String {
        if (total < 0) return ""
        val one = total == 1
        val word = when (pickedKind?.trim()?.lowercase()) {
            null, "" -> if (one) "title" else "titles"
            "movie" -> if (one) "movie" else "movies"
            "tv" -> if (one) "TV show" else "TV shows"
            "video" -> if (one) "video" else "videos"
            "book" -> if (one) "book" else "books"
            "comic" -> if (one) "comic" else "comics"
            "audiobook" -> if (one) "audiobook" else "audiobooks"
            "music" -> if (one) "album" else "albums"
            "playlist" -> if (one) "playlist" else "playlists"
            else -> if (one) "collection" else "collections"
        }
        return "${"%,d".format(java.util.Locale.US, total)} $word"
    }

    /** What a removal says: "Removed from My List." for one title, "3 titles removed from My List." for more. */
    fun removedText(count: Int): String =
        if (count == 1) "Removed from My List." else "${"%,d".format(java.util.Locale.US, count)} titles removed from My List."
}

/**
 * The line shown under the count after a removal. [removed] is exactly what the Engine took off (with the original dates), so Undo
 * puts back those titles and nothing else.
 */
data class UndoOffer(val message: String, val removed: List<SavedItem>) {
    companion object {
        fun forRemoved(removed: List<SavedItem>): UndoOffer? =
            if (removed.isEmpty()) null else UndoOffer(MyListLogic.removedText(removed.size), removed)
    }
}
