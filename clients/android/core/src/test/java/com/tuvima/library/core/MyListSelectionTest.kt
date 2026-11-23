package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertSame
import org.junit.Assert.assertTrue
import org.junit.Test

class MyListSelectionTest {
    private fun card(id: String, mediaType: String = "Movies") = DisplayCard(
        id = id, workId = id, assetId = null, collectionId = null, mediaType = mediaType, title = "T $id", subtitle = null,
        facts = emptyList(), description = null, artworkUrl = null, actions = emptyList(), progressPercent = null,
    )

    private val cards = listOf(card("a"), card("b"), card("c"))

    private fun keyFor(card: DisplayCard): SavedKey? = SavedKey.of(SavedKind.Movie, card.id)

    @Test
    fun selectModeStartsOffAndNothingCanBeTickedUntilItIsStarted() {
        val off = MyListSelection.Off

        assertFalse(off.active)
        assertSame(off, off.toggle("a"))
        assertEquals(0, off.count)
        assertFalse(off.canRemove)
    }

    @Test
    fun startingSelectModeTicksNothingAndPromptsForTitles() {
        val started = MyListSelection.Off.start()

        assertTrue(started.active)
        assertEquals(0, started.count)
        assertEquals("Select titles", started.label())
        assertFalse(started.canRemove)
    }

    @Test
    fun aPressTicksATitleAndASecondPressUnticksIt() {
        val one = MyListSelection.Off.start().toggle("a")
        val none = one.toggle("a")

        assertTrue("a" in one)
        assertEquals("1 selected", one.label())
        assertTrue(one.canRemove)
        assertFalse("a" in none)
        assertFalse(none.canRemove)
    }

    @Test
    fun theCountReadsAsWordsWithThousandsSeparated() {
        val ids = (1..1200).map { "id$it" }

        assertEquals("1,200 selected", MyListSelection.Off.start().selectAll(ids).label())
        assertEquals("3 selected", MyListSelection.Off.start().selectAll(listOf("a", "b", "c")).label())
    }

    @Test
    fun selectAllTicksEveryCardShownAndClearUnticksThemButStaysInSelectMode() {
        val all = MyListSelection.Off.start().selectAll(cards.map { it.id })

        assertTrue(all.allSelected(cards.map { it.id }))
        val cleared = all.clear()
        assertTrue(cleared.active)
        assertEquals(0, cleared.count)
        assertFalse(cleared.allSelected(cards.map { it.id }))
    }

    @Test
    fun nothingIsAllSelectedWhenThereAreNoCards() {
        assertFalse(MyListSelection.Off.start().allSelected(emptyList()))
    }

    @Test
    fun cancelLeavesSelectModeAndForgetsTheTicks() {
        val cancelled = MyListSelection.Off.start().toggle("a").cancel()

        assertFalse(cancelled.active)
        assertEquals(0, cancelled.count)
    }

    @Test
    fun theTitlesToRemoveAreTheTickedOnesInTheOrderShownEachOnce() {
        val picked = MyListSelection.Off.start().toggle("c").toggle("a")

        val keys = picked.keysToRemove(cards, ::keyFor)

        assertEquals(listOf(SavedKey.of(SavedKind.Movie, "a"), SavedKey.of(SavedKind.Movie, "c")), keys)
    }

    @Test
    fun aTickedCardNoLongerShownIsNotRemovedAndACardWithNoKeyIsLeftOutNotGuessed() {
        val picked = MyListSelection.Off.start().toggle("gone").toggle("b").toggle("c")

        val keys = picked.keysToRemove(cards) { card -> if (card.id == "b") null else keyFor(card) }

        assertEquals(listOf(SavedKey.of(SavedKind.Movie, "c")), keys)
    }

    @Test
    fun twoCardsForTheSameSavedTitleAreRemovedOnce() {
        val twin = cards + card("a2")
        val picked = MyListSelection.Off.start().toggle("a").toggle("a2")

        val keys = picked.keysToRemove(twin) { SavedKey.of(SavedKind.TvShow, "same") }

        assertEquals(1, keys.size)
    }

    @Test
    fun theWordsAreTheDashboardsOwn() {
        assertEquals("Select", MyListSelection.SELECT)
        assertEquals("Cancel", MyListSelection.CANCEL)
        assertEquals("Remove", MyListSelection.REMOVE)
        assertEquals("Select all", MyListSelection.SELECT_ALL)
    }

    @Test
    fun reloadingTheListDropsTicksOnTitlesThatAreNoLongerShown() {
        val ticked = MyListSelection.Off.start().toggle("a").toggle("c")

        val after = ticked.retain(listOf("a", "b"))

        assertEquals(1, after.count)
        assertTrue("a" in after)
        assertFalse("c" in after)
        assertSame(after, after.retain(listOf("a", "b")))
        assertSame(MyListSelection.Off, MyListSelection.Off.retain(emptyList()))
    }
}
