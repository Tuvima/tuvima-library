package com.tuvima.library.core

import org.json.JSONObject

// The phone's Bookmarks panel (spec native-apps WP10b, the contract's Bookmarks panel) without a screen: the Mine | Global choice that shows only
// when a Global set exists, the rows of one tab in time order, the search box above eight rows, the note and name limits, and the two request
// bodies (a bookmark with a note, a rename). Mine is the Engine's `isYours`, Global its `inEveryone`; a client never filters by its own profile.
// Music has no Bookmarks on the phone. Videos and audiobooks share the Engine's audiobook-bookmark routes (`ClientRoutes.bookmarks`).

/** One row of the list. [words] is the name, else the note, else "Bookmark at 34:12". [byline] is the maker's name on Global. */
data class PhoneBookmarkRow(
    val id: String,
    val time: String,
    val positionSeconds: Double,
    val words: String,
    val byline: String?,
    val frameUrl: String?,
    val note: String,
    val label: String,
    /** Mine rows only: the person may rename, add a note, or delete it. */
    val canChange: Boolean,
    /** True for one made on this phone while offline that has not reached the Engine yet; it can be deleted here but not renamed. */
    val waiting: Boolean,
)

data class PhoneBookmarkList(
    /** The Mine | Global choice is drawn only when the title has a Global set. */
    val showChoice: Boolean,
    val shown: TvBookmarkTab,
    val mineLabel: String,
    val globalLabel: String,
    val rows: List<PhoneBookmarkRow>,
    /** The search box shows above eight rows. */
    val showSearch: Boolean,
    /** The words of an empty list (no rows to show), or null when there are rows. */
    val emptyText: String?,
)

object PhoneBookmarks {
    /** A search box appears above this many rows ("A search box appears above eight rows"). */
    const val SEARCH_ABOVE = 8

    const val MAX_TEXT = 200
    const val ADD_A_NOTE = "Add a note…"
    const val SEARCH = "Search bookmarks"
    const val EDIT = "Edit"
    const val NAME = "Name"
    const val NOTE = "Note"
    const val SAVE = "Save"
    const val CANCEL = "Cancel"
    const val NO_MATCH = "No bookmarks match your search"
    const val BOOKMARKS = "Bookmarks"

    /** The two lists the Engine's answer holds: the person's own and the title's Global set. */
    fun mine(items: List<TvBookmark>): List<TvBookmark> = items.filter { it.isYours }

    fun global(items: List<TvBookmark>): List<TvBookmark> = items.filter { it.inEveryone }

    /**
     * What the panel draws for [tab] and [query]. The choice is hidden without a Global set and Mine is then always shown; the counts are of
     * the whole tab (a search narrows the rows, not the counts).
     */
    fun list(items: List<TvBookmark>, tab: TvBookmarkTab, query: String, loaded: Boolean): PhoneBookmarkList {
        val mine = mine(items)
        val global = global(items)
        val showChoice = global.isNotEmpty()
        val shown = if (showChoice && tab == TvBookmarkTab.Global) TvBookmarkTab.Global else TvBookmarkTab.Mine
        val all = if (shown == TvBookmarkTab.Global) global else mine
        val needle = query.trim()
        val matching = if (needle.isEmpty()) all else all.filter { matches(it, needle) }
        val rows = matching.sortedBy { it.positionSeconds }.map { rowOf(it, shown) }
        val empty = when {
            rows.isNotEmpty() -> null
            !loaded -> null
            needle.isNotEmpty() && all.isNotEmpty() -> NO_MATCH
            // The Global tab only exists while the title has a Global set, so an empty list here is always Mine's.
            else -> TvPlayerWords.NO_MINE
        }
        return PhoneBookmarkList(
            showChoice = showChoice,
            shown = shown,
            mineLabel = TvPlayerWords.MINE + TvPlayerWords.SEP + mine.size,
            globalLabel = TvPlayerWords.GLOBAL + TvPlayerWords.SEP + global.size,
            rows = rows,
            showSearch = all.size > SEARCH_ABOVE,
            emptyText = empty,
        )
    }

    private fun rowOf(b: TvBookmark, tab: TvBookmarkTab): PhoneBookmarkRow {
        val words = b.label.ifBlank { b.note.ifBlank { TvPanels.bookmarkAtLabel(b.positionSeconds) } }
        val waiting = OfflineBookmarkLogic.isWaiting(b.id)
        return PhoneBookmarkRow(
            id = b.id,
            time = TvPlayerLogic.formatTime(b.positionSeconds),
            positionSeconds = b.positionSeconds,
            words = words,
            byline = if (tab == TvBookmarkTab.Global) b.ownerName.takeIf { it.isNotBlank() } else null,
            frameUrl = b.frameUrl,
            note = b.note,
            label = b.label,
            canChange = tab == TvBookmarkTab.Mine && b.isYours,
            waiting = waiting,
        )
    }

    private fun matches(b: TvBookmark, needle: String): Boolean =
        b.label.contains(needle, ignoreCase = true) ||
            b.note.contains(needle, ignoreCase = true) ||
            b.ownerName.contains(needle, ignoreCase = true) ||
            TvPlayerLogic.formatTime(b.positionSeconds).contains(needle, ignoreCase = true)

    /** Text for a name or a note as the Engine takes it: trimmed, and nothing at all when blank; null when it is over the limit. */
    fun cleanText(value: String): String? {
        val trimmed = value.trim()
        return if (trimmed.length > MAX_TEXT) null else trimmed
    }

    /** True while a name or note is short enough to save. */
    fun fits(value: String): Boolean = value.trim().length <= MAX_TEXT

    /**
     * The body of "Bookmark at {0}", with the note typed in "Add a note…" when there is one. Saved at the moment it was pressed, never Global.
     * Null when the note is over the limit (the field's own counter stops that first).
     */
    fun createBody(assetId: String, positionSeconds: Double, durationSeconds: Double?, note: String): JSONObject? {
        val clean = cleanText(note) ?: return null
        val body = TvBookmarks.createBody(assetId, positionSeconds, durationSeconds)
        if (clean.isNotEmpty()) body.put("note", clean)
        return body
    }

    /** The body of Edit's Save: the new name and note (`PUT /api/v1/player/audiobooks/bookmarks/{id}`). Null when either is over the limit. */
    fun updateBody(label: String, note: String): JSONObject? {
        val name = cleanText(label) ?: return null
        val text = cleanText(note) ?: return null
        return JSONObject().put("label", if (name.isEmpty()) JSONObject.NULL else name).put("note", if (text.isEmpty()) JSONObject.NULL else text)
    }

    /** The list after the Engine answered a rename: that bookmark replaced, in place. */
    fun replace(items: List<TvBookmark>, changed: TvBookmark): List<TvBookmark> =
        items.map { if (it.id == changed.id) changed else it }

    /** The list after a delete: that bookmark gone. */
    fun without(items: List<TvBookmark>, id: String): List<TvBookmark> = items.filterNot { it.id == id }

    /** The list after a bookmark was added online: it goes in, once. */
    fun withAdded(items: List<TvBookmark>, added: TvBookmark): List<TvBookmark> = items.filterNot { it.id == added.id } + added

    /** Bookmarks can be added when the title has a work to hang them on and is not music. */
    fun canBookmark(workId: String?, isMusic: Boolean): Boolean = !isMusic && !workId.isNullOrBlank()
}
