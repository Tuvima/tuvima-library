package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FindAnotherKeysTest {
    private fun open(rows: Int = 4, languages: Int = 3) = FindAnotherKeys.initial(rows, languages)

    private fun press(state: FindAnotherKeyState, vararg keys: FindAnotherKey): FindAnotherKeyResult {
        var result = FindAnotherKeyResult(state)
        keys.forEach { result = FindAnotherKeys.reduce(result.state, it) }
        return result
    }

    @Test
    fun theWindowOpensOnTheFirstRow() {
        val state = open()
        assertEquals(FindAnotherZone.List, state.zone)
        assertEquals(0, state.row)
    }

    @Test
    fun withNoRowsTheWindowOpensOnCancel() {
        assertEquals(FindAnotherZone.Cancel, FindAnotherKeys.initial(0, 3).zone)
    }

    @Test
    fun upAndDownMoveThroughTheListAndThePaneFollows() {
        val down = FindAnotherKeys.reduce(open(), FindAnotherKey.Down)
        assertEquals(1, down.state.row)
        assertEquals(FindAnotherEffect.RowFocused(1), down.effect)
        val up = FindAnotherKeys.reduce(down.state, FindAnotherKey.Up)
        assertEquals(0, up.state.row)
        assertEquals(FindAnotherEffect.RowFocused(0), up.effect)
    }

    @Test
    fun downStopsAtTheLastRow() {
        val last = press(open(rows = 2), FindAnotherKey.Down, FindAnotherKey.Down)
        assertEquals(1, last.state.row)
        assertEquals(FindAnotherEffect.None, last.effect)
    }

    @Test
    fun upFromTheFirstRowReachesTheLanguageButtonAndDownComesBack() {
        val onLanguage = FindAnotherKeys.reduce(open(), FindAnotherKey.Up).state
        assertEquals(FindAnotherZone.Language, onLanguage.zone)
        assertEquals(FindAnotherZone.List, FindAnotherKeys.reduce(onLanguage, FindAnotherKey.Down).state.zone)
    }

    @Test
    fun noLanguageButtonMeansUpFromTheFirstRowStaysPut() {
        val state = FindAnotherKeys.initial(3, languageCount = 3, languageAvailable = false)
        assertEquals(FindAnotherZone.List, FindAnotherKeys.reduce(state, FindAnotherKey.Up).state.zone)
    }

    @Test
    fun rightMovesFromTheListToThePaneThenToUseThis() {
        val pane = FindAnotherKeys.reduce(open(), FindAnotherKey.Right).state
        assertEquals(FindAnotherZone.Pane, pane.zone)
        assertEquals(FindAnotherZone.UseThis, FindAnotherKeys.reduce(pane, FindAnotherKey.Right).state.zone)
    }

    @Test
    fun upAndDownInThePaneScrollIt() {
        val pane = FindAnotherKeys.reduce(open(), FindAnotherKey.Right).state
        assertEquals(FindAnotherEffect.ScrollPane(1), FindAnotherKeys.reduce(pane, FindAnotherKey.Down).effect)
        assertEquals(FindAnotherEffect.ScrollPane(-1), FindAnotherKeys.reduce(pane, FindAnotherKey.Up).effect)
        assertEquals(FindAnotherZone.Pane, FindAnotherKeys.reduce(pane, FindAnotherKey.Down).state.zone)
    }

    @Test
    fun leftGoesBackTowardTheList() {
        val onUseThis = press(open(), FindAnotherKey.Right, FindAnotherKey.Right).state
        val pane = FindAnotherKeys.reduce(onUseThis, FindAnotherKey.Left).state
        assertEquals(FindAnotherZone.Pane, pane.zone)
        assertEquals(FindAnotherZone.List, FindAnotherKeys.reduce(pane, FindAnotherKey.Left).state.zone)
    }

    @Test
    fun selectOnARowOpensItsWordsInFullAndFocusesUseThis() {
        val result = press(open(), FindAnotherKey.Down, FindAnotherKey.Select)
        assertEquals(FindAnotherEffect.OpenFullWords(1), result.effect)
        assertEquals(FindAnotherZone.UseThis, result.state.zone)
        assertTrue(result.state.fullWords)
    }

    @Test
    fun selectOnUseThisAppliesTheFocusedRow() {
        val result = press(open(), FindAnotherKey.Down, FindAnotherKey.Down, FindAnotherKey.Select, FindAnotherKey.Select)
        assertEquals(FindAnotherEffect.Apply(2), result.effect)
    }

    @Test
    fun cancelIsOneRightFromUseThisAndSelectCloses() {
        val result = press(open(), FindAnotherKey.Right, FindAnotherKey.Right, FindAnotherKey.Right, FindAnotherKey.Select)
        assertEquals(FindAnotherEffect.Close, result.effect)
    }

    @Test
    fun selectOnLanguageOpensTheShortListAndChoosingOneSearchesAgain() {
        val onLanguage = FindAnotherKeys.reduce(open(), FindAnotherKey.Up).state
        val opened = FindAnotherKeys.reduce(onLanguage, FindAnotherKey.Select)
        assertEquals(FindAnotherEffect.OpenLanguageList, opened.effect)
        assertTrue(opened.state.languageListOpen)
        val chosen = press(opened.state, FindAnotherKey.Down, FindAnotherKey.Down, FindAnotherKey.Select)
        assertEquals(FindAnotherEffect.ChooseLanguage(2), chosen.effect)
        assertFalse(chosen.state.languageListOpen)
        assertEquals(FindAnotherZone.Language, chosen.state.zone)
    }

    @Test
    fun theLanguageListStopsAtItsEnds() {
        val opened = FindAnotherKeys.reduce(FindAnotherKeys.reduce(open(languages = 2), FindAnotherKey.Up).state, FindAnotherKey.Select).state
        assertEquals(0, FindAnotherKeys.reduce(opened, FindAnotherKey.Up).state.languageIndex)
        assertEquals(1, press(opened, FindAnotherKey.Down, FindAnotherKey.Down, FindAnotherKey.Down).state.languageIndex)
    }

    @Test
    fun backClosesTheLanguageListFirst() {
        val opened = press(open(), FindAnotherKey.Up, FindAnotherKey.Select).state
        val result = FindAnotherKeys.reduce(opened, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.CloseLanguageList, result.effect)
        assertFalse(result.state.languageListOpen)
    }

    @Test
    fun backClosesTheFullWordsAfterTheLanguageListAndBeforeTheWindow() {
        // Full words open, then the language list opened on top of them is not reachable (focus moved to Use this);
        // from Use this: Back closes the words and focus returns to the list.
        val inFull = press(open(), FindAnotherKey.Select).state
        assertTrue(inFull.fullWords)
        val first = FindAnotherKeys.reduce(inFull, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.CloseFullWords, first.effect)
        assertFalse(first.state.fullWords)
        assertEquals(FindAnotherZone.List, first.state.zone)
        val second = FindAnotherKeys.reduce(first.state, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.Close, second.effect)
    }

    @Test
    fun backOrderIsLanguageListThenFullWordsThenWindow() {
        // Full words open, focus walks up to the Language button and its list opens: Back must close that list first.
        var state = press(open(), FindAnotherKey.Select).state
        state = press(state, FindAnotherKey.Left, FindAnotherKey.Left, FindAnotherKey.Up).state
        assertEquals(FindAnotherZone.Language, state.zone)
        assertTrue(state.fullWords)
        state = FindAnotherKeys.reduce(state, FindAnotherKey.Select).state
        assertTrue(state.languageListOpen)
        val first = FindAnotherKeys.reduce(state, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.CloseLanguageList, first.effect)
        assertTrue(first.state.fullWords)
        val second = FindAnotherKeys.reduce(first.state, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.CloseFullWords, second.effect)
        val third = FindAnotherKeys.reduce(second.state, FindAnotherKey.Back)
        assertEquals(FindAnotherEffect.Close, third.effect)
    }

    @Test
    fun movingToAnotherRowFoldsTheFullWordsBackToTheFocusedRowsPreview() {
        val inFull = press(open(), FindAnotherKey.Select).state
        val onList = press(inFull, FindAnotherKey.Left, FindAnotherKey.Left).state
        val moved = FindAnotherKeys.reduce(onList, FindAnotherKey.Down)
        assertFalse(moved.state.fullWords)
        assertEquals(1, moved.state.row)
    }

    @Test
    fun withNoRowsSelectAndRightDoNothingAndCancelCloses() {
        val state = FindAnotherKeys.initial(0, 3)
        assertEquals(FindAnotherEffect.Close, FindAnotherKeys.reduce(state, FindAnotherKey.Select).effect)
        assertEquals(FindAnotherZone.Cancel, FindAnotherKeys.reduce(state, FindAnotherKey.Right).state.zone)
        assertEquals(FindAnotherZone.Language, FindAnotherKeys.reduce(state, FindAnotherKey.Up).state.zone)
        val onLanguage = FindAnotherKeys.reduce(state, FindAnotherKey.Up).state
        assertEquals(FindAnotherZone.Cancel, FindAnotherKeys.reduce(onLanguage, FindAnotherKey.Down).state.zone)
    }

    @Test
    fun backFromTheListClosesTheWindow() {
        assertEquals(FindAnotherEffect.Close, FindAnotherKeys.reduce(open(), FindAnotherKey.Back).effect)
    }

    // The Audio & subtitles panel

    private fun choices(vararg labels: String, selected: Int = -1) =
        labels.mapIndexed { index, label -> PanelChoice("id$index", label, index == selected) }

    @Test
    fun theFooterRowAppearsOnlyWhenASourceIsOn() {
        val subtitles = choices("English")
        assertEquals(PanelRowKind.Find, AudioSubtitlesPanel.rows(emptyList(), subtitles, canFind = true).last().kind)
        assertTrue(AudioSubtitlesPanel.rows(emptyList(), subtitles, canFind = false).none { it.kind == PanelRowKind.Find })
    }

    @Test
    fun audioIsListedOnlyWhenThereAreSeveralTracks() {
        assertTrue(AudioSubtitlesPanel.rows(choices("English"), emptyList(), false).none { it.kind == PanelRowKind.Audio })
        val rows = AudioSubtitlesPanel.rows(choices("English", "Japanese"), emptyList(), false)
        assertEquals(2, rows.count { it.kind == PanelRowKind.Audio })
    }

    @Test
    fun subtitlesListOffFirstThenEachTrackAndOffIsSelectedWhenNoneIs() {
        val rows = AudioSubtitlesPanel.rows(emptyList(), choices("English", "French"), true)
        assertEquals(
            listOf(PanelRowKind.SubtitlesHeader, PanelRowKind.SubtitlesOff, PanelRowKind.Subtitle, PanelRowKind.Subtitle, PanelRowKind.Find),
            rows.map { it.kind },
        )
        assertTrue(rows[1].selected)
        val withSelection = AudioSubtitlesPanel.rows(emptyList(), choices("English", "French", selected = 1), true)
        assertFalse(withSelection[1].selected)
        assertTrue(withSelection[3].selected)
    }

    @Test
    fun headersTakeNoFocusAndFocusStartsOnTheSelectedSubtitle() {
        val rows = AudioSubtitlesPanel.rows(choices("English", "Japanese"), choices("English", "French", selected = 1), true)
        assertTrue(AudioSubtitlesPanel.focusable(rows).none { it.kind == PanelRowKind.AudioHeader || it.kind == PanelRowKind.SubtitlesHeader })
        val index = AudioSubtitlesPanel.initialIndex(rows)
        assertEquals("id1", AudioSubtitlesPanel.focusable(rows)[index].id)
        assertEquals(PanelRowKind.Subtitle, AudioSubtitlesPanel.focusable(rows)[index].kind)
    }

    @Test
    fun focusStartsOnOffWhenNoSubtitleIsSelected() {
        val rows = AudioSubtitlesPanel.rows(emptyList(), choices("English"), false)
        assertEquals(PanelRowKind.SubtitlesOff, AudioSubtitlesPanel.focusable(rows)[AudioSubtitlesPanel.initialIndex(rows)].kind)
    }

    @Test
    fun thePanelMovesWithUpAndDownActivatesWithSelectAndClosesWithBackOrLeft() {
        assertEquals(1, AudioSubtitlesPanelKeys.reduce(0, 3, FindAnotherKey.Down).index)
        assertEquals(2, AudioSubtitlesPanelKeys.reduce(2, 3, FindAnotherKey.Down).index)
        assertEquals(0, AudioSubtitlesPanelKeys.reduce(0, 3, FindAnotherKey.Up).index)
        assertEquals(PanelEffect.Activate(1), AudioSubtitlesPanelKeys.reduce(1, 3, FindAnotherKey.Select).effect)
        assertEquals(PanelEffect.Close, AudioSubtitlesPanelKeys.reduce(1, 3, FindAnotherKey.Back).effect)
        assertEquals(PanelEffect.Close, AudioSubtitlesPanelKeys.reduce(1, 3, FindAnotherKey.Left).effect)
        assertEquals(PanelEffect.None, AudioSubtitlesPanelKeys.reduce(1, 3, FindAnotherKey.Right).effect)
    }
}
