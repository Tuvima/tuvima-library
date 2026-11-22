package com.tuvima.library.core

// The remote's key map for the Find another window (spec "Find another on TV", section 4), as a pure reducer: a key goes in
// with the current focus state, the next focus state and one effect for the screen to carry out come out. The screen draws
// from the state; nothing here knows about views.

enum class FindAnotherKey { Up, Down, Left, Right, Select, Back }

/** Where focus is in the window. */
enum class FindAnotherZone { Language, List, Pane, UseThis, Cancel }

data class FindAnotherKeyState(
    val zone: FindAnotherZone,
    /** The focused row of the list (the pane follows it). */
    val row: Int = 0,
    val rowCount: Int = 0,
    /** True once Select opened the focused row's words in full in the pane. */
    val fullWords: Boolean = false,
    val languageListOpen: Boolean = false,
    val languageIndex: Int = 0,
    val languageCount: Int = 0,
    /** False for the sentences about a switch, a key or an item that cannot be matched: the Language button is then not offered. */
    val languageAvailable: Boolean = true,
)

/** What the screen does after a key. */
sealed interface FindAnotherEffect {
    data object None : FindAnotherEffect

    /** The focused row changed: show its words in the pane and let the reading rule watch it. */
    data class RowFocused(val row: Int) : FindAnotherEffect

    /** The pane scrolls by [lines] (negative is up). */
    data class ScrollPane(val lines: Int) : FindAnotherEffect

    /** Show the row's words in full and read it at once when it has not been read. */
    data class OpenFullWords(val row: Int) : FindAnotherEffect

    data object CloseFullWords : FindAnotherEffect

    /** Use the row: apply it, close the window and the panel, return to the video. */
    data class Apply(val row: Int) : FindAnotherEffect

    data object OpenLanguageList : FindAnotherEffect

    data object CloseLanguageList : FindAnotherEffect

    /** A language was picked: search again in it. */
    data class ChooseLanguage(val index: Int) : FindAnotherEffect

    /** Close the window and go back to the panel. */
    data object Close : FindAnotherEffect
}

data class FindAnotherKeyResult(val state: FindAnotherKeyState, val effect: FindAnotherEffect = FindAnotherEffect.None)

object FindAnotherKeys {
    /** The state when the window opens (or a new search finishes): the first row focused, or Cancel when there are no rows. */
    fun initial(rowCount: Int, languageCount: Int = 0, languageAvailable: Boolean = true): FindAnotherKeyState =
        FindAnotherKeyState(
            zone = if (rowCount > 0) FindAnotherZone.List else FindAnotherZone.Cancel,
            rowCount = rowCount,
            languageCount = languageCount,
            languageAvailable = languageAvailable && languageCount > 0,
        )

    fun reduce(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult {
        if (state.languageListOpen) return languageList(state, key)
        if (key == FindAnotherKey.Back) {
            // Back closes the language list (handled above), then the full words, then the window.
            return if (state.fullWords) {
                FindAnotherKeyResult(
                    state.copy(fullWords = false, zone = if (state.rowCount > 0) FindAnotherZone.List else FindAnotherZone.Cancel),
                    FindAnotherEffect.CloseFullWords,
                )
            } else {
                FindAnotherKeyResult(state, FindAnotherEffect.Close)
            }
        }
        return when (state.zone) {
            FindAnotherZone.Language -> language(state, key)
            FindAnotherZone.List -> list(state, key)
            FindAnotherZone.Pane -> pane(state, key)
            FindAnotherZone.UseThis -> useThis(state, key)
            FindAnotherZone.Cancel -> cancel(state, key)
        }
    }

    private fun stay(state: FindAnotherKeyState) = FindAnotherKeyResult(state)

    private fun moveTo(state: FindAnotherKeyState, zone: FindAnotherZone) = FindAnotherKeyResult(state.copy(zone = zone))

    private fun language(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Down -> moveTo(state, if (state.rowCount > 0) FindAnotherZone.List else FindAnotherZone.Cancel)
        FindAnotherKey.Select ->
            if (state.languageCount > 0) {
                FindAnotherKeyResult(state.copy(languageListOpen = true), FindAnotherEffect.OpenLanguageList)
            } else {
                stay(state)
            }
        else -> stay(state)
    }

    private fun languageList(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Up -> stay(state.copy(languageIndex = (state.languageIndex - 1).coerceAtLeast(0)))
        FindAnotherKey.Down ->
            stay(state.copy(languageIndex = (state.languageIndex + 1).coerceAtMost((state.languageCount - 1).coerceAtLeast(0))))
        FindAnotherKey.Select -> FindAnotherKeyResult(
            state.copy(languageListOpen = false, zone = FindAnotherZone.Language),
            FindAnotherEffect.ChooseLanguage(state.languageIndex),
        )
        FindAnotherKey.Back -> FindAnotherKeyResult(state.copy(languageListOpen = false), FindAnotherEffect.CloseLanguageList)
        else -> stay(state)
    }

    private fun list(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Up ->
            if (state.row > 0) {
                val row = state.row - 1
                FindAnotherKeyResult(state.copy(row = row, fullWords = false), FindAnotherEffect.RowFocused(row))
            } else if (state.languageAvailable) {
                moveTo(state, FindAnotherZone.Language)
            } else {
                stay(state)
            }
        FindAnotherKey.Down ->
            if (state.row < state.rowCount - 1) {
                val row = state.row + 1
                FindAnotherKeyResult(state.copy(row = row, fullWords = false), FindAnotherEffect.RowFocused(row))
            } else {
                stay(state)
            }
        FindAnotherKey.Right -> if (state.rowCount > 0) moveTo(state, FindAnotherZone.Pane) else stay(state)
        FindAnotherKey.Select -> openFullWords(state)
        else -> stay(state)
    }

    private fun pane(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Up -> FindAnotherKeyResult(state, FindAnotherEffect.ScrollPane(-1))
        FindAnotherKey.Down -> FindAnotherKeyResult(state, FindAnotherEffect.ScrollPane(1))
        FindAnotherKey.Left -> moveTo(state, FindAnotherZone.List)
        FindAnotherKey.Right -> moveTo(state, FindAnotherZone.UseThis)
        FindAnotherKey.Select -> openFullWords(state)
        else -> stay(state)
    }

    private fun useThis(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Select -> FindAnotherKeyResult(state, FindAnotherEffect.Apply(state.row))
        FindAnotherKey.Left, FindAnotherKey.Up -> moveTo(state, FindAnotherZone.Pane)
        FindAnotherKey.Right -> moveTo(state, FindAnotherZone.Cancel)
        else -> stay(state)
    }

    private fun cancel(state: FindAnotherKeyState, key: FindAnotherKey): FindAnotherKeyResult = when (key) {
        FindAnotherKey.Select -> FindAnotherKeyResult(state, FindAnotherEffect.Close)
        FindAnotherKey.Left -> if (state.rowCount > 0) moveTo(state, FindAnotherZone.UseThis) else stay(state)
        FindAnotherKey.Up ->
            if (state.rowCount > 0) moveTo(state, FindAnotherZone.Pane)
            else if (state.languageAvailable) moveTo(state, FindAnotherZone.Language)
            else stay(state)
        else -> stay(state)
    }

    // Select on a row: its words open in full in the pane and focus moves to Use this.
    private fun openFullWords(state: FindAnotherKeyState): FindAnotherKeyResult =
        if (state.rowCount > 0) {
            FindAnotherKeyResult(
                state.copy(fullWords = true, zone = FindAnotherZone.UseThis),
                FindAnotherEffect.OpenFullWords(state.row),
            )
        } else {
            stay(state)
        }
}

// The small Audio & subtitles side panel: Audio rows (only when there are several), Subtitles rows (Off, then each
// track) and a footer row "Find subtitles…" shown only when a source is switched on (D4, D8).

enum class PanelRowKind { AudioHeader, Audio, SubtitlesHeader, SubtitlesOff, Subtitle, Find }

/** One row of the panel. [id] is the player's own handle for a track (empty for Off and the footer). */
data class PanelRow(val kind: PanelRowKind, val label: String, val id: String = "", val selected: Boolean = false) {
    /** Headers are labels only; every other row takes focus. */
    val isFocusable: Boolean get() = kind != PanelRowKind.AudioHeader && kind != PanelRowKind.SubtitlesHeader
}

/** A track the player can switch to, as the panel lists it. */
data class PanelChoice(val id: String, val label: String, val selected: Boolean)

object AudioSubtitlesPanel {
    /**
     * The rows top to bottom. Audio is listed only when there are several tracks; Off is selected when no subtitle track is;
     * the footer row appears only when [canFind] (the sources check said Ready and the profile may use it, D5).
     */
    fun rows(audio: List<PanelChoice>, subtitles: List<PanelChoice>, canFind: Boolean): List<PanelRow> = buildList {
        if (audio.size > 1) {
            add(PanelRow(PanelRowKind.AudioHeader, "Audio"))
            audio.forEach { add(PanelRow(PanelRowKind.Audio, it.label, it.id, it.selected)) }
        }
        add(PanelRow(PanelRowKind.SubtitlesHeader, "Subtitles"))
        add(PanelRow(PanelRowKind.SubtitlesOff, "Off", selected = subtitles.none { it.selected }))
        subtitles.forEach { add(PanelRow(PanelRowKind.Subtitle, it.label, it.id, it.selected)) }
        if (canFind) add(PanelRow(PanelRowKind.Find, FindAnotherWording.FIND_SUBTITLES_ENTRY))
    }

    /** The focusable rows, in order: the index the key reducer moves over. */
    fun focusable(rows: List<PanelRow>): List<PanelRow> = rows.filter { it.isFocusable }

    /** The row focus starts on: the selected subtitle row, else the first focusable row. */
    fun initialIndex(rows: List<PanelRow>): Int {
        val rowsInFocus = focusable(rows)
        val selected = rowsInFocus.indexOfFirst { (it.kind == PanelRowKind.Subtitle || it.kind == PanelRowKind.SubtitlesOff) && it.selected }
        return if (selected >= 0) selected else 0
    }
}

/** What the screen does after a key on the panel. */
sealed interface PanelEffect {
    data object None : PanelEffect

    /** Select on the focused row: switch the track, or open Find another for the footer. */
    data class Activate(val index: Int) : PanelEffect

    /** Close the panel and go back to the video. */
    data object Close : PanelEffect
}

data class PanelKeyResult(val index: Int, val effect: PanelEffect = PanelEffect.None)

object AudioSubtitlesPanelKeys {
    /** Up and Down move over the [count] focusable rows, Select activates, Back and Left close. */
    fun reduce(index: Int, count: Int, key: FindAnotherKey): PanelKeyResult = when (key) {
        FindAnotherKey.Up -> PanelKeyResult((index - 1).coerceAtLeast(0))
        FindAnotherKey.Down -> PanelKeyResult((index + 1).coerceAtMost((count - 1).coerceAtLeast(0)))
        FindAnotherKey.Select -> if (count > 0) PanelKeyResult(index, PanelEffect.Activate(index)) else PanelKeyResult(index)
        FindAnotherKey.Back, FindAnotherKey.Left -> PanelKeyResult(index, PanelEffect.Close)
        FindAnotherKey.Right -> PanelKeyResult(index)
    }
}
