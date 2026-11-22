package com.tuvima.library.core

// What each row of the Find another list and its words pane show, worked out from the Engine's answer. Pure and
// screen-free: the television window and the phone sheet both draw these.

/** What the pane shows for the focused row. */
sealed interface FindAnotherPane {
    /** The prompt before a row has been read: "Select this result to read its first lines." */
    data class Prompt(val text: String) : FindAnotherPane

    /** "Reading…" for this row only. */
    data object Reading : FindAnotherPane

    /** "Could not read this one"; the row can still be used. */
    data object CouldNotRead : FindAnotherPane

    /** The words, with the time each appears for a subtitle. [full] is true once Select opened them in full. */
    data class Lines(val lines: List<TextTrackPreviewLine>, val full: Boolean) : FindAnotherPane
}

/** One row of the list. */
data class FindAnotherRow(
    val key: String,
    val title: String,
    val meta: String?,
    val checks: List<FindAnotherCheck>,
    /** "Best match" on the first row, null on the others. */
    val bestMatchChip: String?,
    /** "In use" on the result that is the track now in use. */
    val inUseChip: String?,
    val item: TextTrackAlternative,
)

object FindAnotherRows {
    /** The most lines the pane shows once Select opens the words in full. */
    const val FULL_WORDS_LINES = 20

    /** The rows in the Engine's order; the first is the best match (as on the Dashboard). */
    fun rows(items: List<TextTrackAlternative>, kind: TextTrackKind): List<FindAnotherRow> {
        val mixedLanguages = items.map { it.language.lowercase() }.distinct().size > 1
        return items.mapIndexed { index, item ->
            FindAnotherRow(
                key = item.key,
                title = FindAnotherWording.rowTitle(item, kind),
                meta = if (kind == TextTrackKind.Lyrics) FindAnotherWording.lyricsMeta(item)
                else FindAnotherWording.subtitleMeta(item, mixedLanguages),
                checks = if (kind == TextTrackKind.Lyrics) FindAnotherWording.lyricsChecks(item)
                else FindAnotherWording.subtitleChecks(item),
                bestMatchChip = if (index == 0) FindAnotherWording.BEST_MATCH else null,
                inUseChip = if (item.isCurrent) FindAnotherWording.IN_USE else null,
                item = item,
            )
        }
    }

    /** The list with one row's read result put in: the row keeps its place and gains its words and its timing check. */
    fun withRead(items: List<TextTrackAlternative>, read: TextTrackAlternative): List<TextTrackAlternative> =
        items.map { if (it.key == read.key) read else it }

    /**
     * What the pane shows for [item]: its words when it has them (cut to [FULL_WORDS_LINES] when [full]), "Reading…" while it
     * is being read, "Could not read this one" after a failed read, else the prompt.
     */
    fun pane(item: TextTrackAlternative, reading: Boolean, failed: Boolean, full: Boolean): FindAnotherPane = when {
        item.hasWords -> FindAnotherPane.Lines(if (full) item.previewLines.take(FULL_WORDS_LINES) else item.previewLines, full)
        reading -> FindAnotherPane.Reading
        failed -> FindAnotherPane.CouldNotRead
        else -> FindAnotherPane.Prompt(FindAnotherWording.NOT_READ_YET)
    }
}
