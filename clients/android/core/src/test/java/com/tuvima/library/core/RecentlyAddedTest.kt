package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class RecentlyAddedTest {
    private fun page() = RecentlyAddedLogic.parsePage(WireFixtures.json("display-recent.json"), WireFixtures.absolute)

    @Test
    fun theEnginesFixtureReadsAsCatalogueCardsAndTheCursor() {
        val page = page()

        // The personal photo (a View asset) is left to the View section.
        assertEquals(listOf("Alpha One"), page.cards.map { it.title })
        assertEquals("opaque-cursor-1", page.nextCursor)
        assertTrue(page.hasMore)
        assertEquals(listOf("watch", "read", "view"), page.availableTypes)
    }

    @Test
    fun filtersAreOfferedOnlyForTheTypesWithSomethingAndNeverForView() {
        assertEquals(
            listOf("watch" to "Watch", "read" to "Read"),
            RecentlyAddedLogic.typeChips(page().availableTypes),
        )
        assertTrue(RecentlyAddedLogic.typeChips(listOf("watch", "view")).isEmpty())
        assertTrue(RecentlyAddedLogic.typeChips(null).isEmpty())
    }

    @Test
    fun aLaterPageNeverRepeatsATitleAlreadyShown() {
        val first = page().cards
        val merged = RecentlyAddedLogic.merge(first, first)

        assertEquals(1, merged.size)
    }

    @Test
    fun theRecentRouteCarriesTheTypeAndCursorOnlyWhenGiven() {
        assertEquals("/api/v1/display/recent?limit=18", ClientRoutes.recent())
        assertEquals("/api/v1/display/recent?type=read&cursor=2&limit=18", ClientRoutes.recent("read", "2"))
        assertNull(RecentlyAddedLogic.parsePage(org.json.JSONObject("""{"items":[],"hasMore":false}"""), WireFixtures.absolute).nextCursor)
    }
}
