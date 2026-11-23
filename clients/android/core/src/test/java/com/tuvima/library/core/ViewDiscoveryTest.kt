package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ViewDiscoveryTest {
    private fun people() = parseViewPeoplePage(WireFixtures.json("view-people.json"))

    private fun places() = parseViewPlacesPage(WireFixtures.json("view-places.json"))

    @Test
    fun viewHasFourTabsOnThePhoneInTheDashboardsOrder() {
        assertEquals(listOf("Timeline", "Galleries", "People", "Places"), ViewDiscoveryLogic.tabs().map { it.label })
    }

    @Test
    fun theEnginesPeopleFixtureReadsAsNamedPeopleWithAPhotoAndTheCursor() {
        val page = people()

        assertEquals(listOf("Ada Lovelace", "Ben Author"), page.items.map { it.displayName })
        assertEquals(listOf(12, 1), page.items.map { it.assetCount })
        assertEquals("7a000000-0000-4000-8000-000000000011", page.items.first().representativeItemId)
        assertEquals("2", ViewDiscoveryLogic.cursorFor(page))
        assertEquals("available", page.capability?.state)
        assertTrue(page.capability?.hasIndexedData == true)
    }

    @Test
    fun theEnginesPlacesFixtureReadsAsPlacesAndTheEndOfTheList() {
        val page = places()

        assertEquals(listOf("Paris, France", "Oslo, Norway"), page.items.map { it.name })
        assertEquals(48.8566, page.items.first().latitude, 0.0001)
        assertEquals(24, page.items.first().assetCount)
        assertNull(ViewDiscoveryLogic.cursorFor(page))
    }

    @Test
    fun aPageThatSaysThereIsNoMoreHasNoCursedEvenIfItNamesOne() {
        val page = ViewPeoplePage(emptyList(), "9", hasMore = false, capability = null)

        assertNull(ViewDiscoveryLogic.cursorFor(page))
    }

    @Test
    fun aRowWithNoKeyIsSkippedAndAMissingCapabilityReadsAsNull() {
        val json = JSONObject("""{"items":[{"key":"","display_name":"Nobody","asset_count":3},{"key":"k","display_name":"Kay","asset_count":2}],"has_more":false}""")

        val page = parseViewPeoplePage(json)

        assertEquals(listOf("Kay"), page.items.map { it.displayName })
        assertNull(page.capability)
        assertNull(page.items.first().representativeItemId)
    }

    @Test
    fun countsReadAsWordsWithOnePhotoOrVideoSingular() {
        assertEquals("1 photo or video", ViewDiscoveryLogic.countText(1))
        assertEquals("24 photos and videos", ViewDiscoveryLogic.countText(24))
        assertEquals("1,200 photos and videos", ViewDiscoveryLogic.countText(1200))
    }

    @Test
    fun aPersonOrPlaceWithNoNameGetsAPlainWordNotABlankRow() {
        assertEquals("Unnamed person", ViewDiscoveryLogic.personName(people().items.first().copy(displayName = "  ")))
        assertEquals("Unnamed place", ViewDiscoveryLogic.placeName(places().items.first().copy(name = "")))
        assertEquals("Ada Lovelace", ViewDiscoveryLogic.personName(people().items.first()))
    }

    @Test
    fun anEmptyListShowsTheEnginesOwnExplanationWhenItGaveOne() {
        val capability = ViewDiscoveryCapability("empty", false, "Add names to your photos and they will show up here.")

        assertEquals("Add names to your photos and they will show up here.", ViewDiscoveryLogic.emptyText(capability, ViewDiscoveryLogic.EMPTY_PEOPLE))
        assertEquals(ViewDiscoveryLogic.EMPTY_PEOPLE, ViewDiscoveryLogic.emptyText(null, ViewDiscoveryLogic.EMPTY_PEOPLE))
        assertEquals(
            ViewDiscoveryLogic.EMPTY_PLACES,
            ViewDiscoveryLogic.emptyText(ViewDiscoveryCapability("empty", false, "  "), ViewDiscoveryLogic.EMPTY_PLACES),
        )
    }

    @Test
    fun aLaterPageNeverRepeatsAPersonOrPlaceAlreadyShown() {
        val first = people().items
        val merged = ViewDiscoveryLogic.mergePeople(first, first + first.first().copy(key = "new", displayName = "New"))

        assertEquals(listOf("ada-lovelace", "ben-author", "new"), merged.map { it.key })
        assertEquals(2, ViewDiscoveryLogic.mergePlaces(places().items, places().items).size)
        assertFalse(ViewDiscoveryLogic.mergePlaces(emptyList(), places().items).isEmpty())
    }

    @Test
    fun theViewRoutesStayInsideApiV1AndEncodeAPersonsKey() {
        assertEquals("/api/v1/view/people?limit=48", ClientRoutes.viewPeople())
        assertEquals("/api/v1/view/people?cursor=2&limit=48", ClientRoutes.viewPeople("2"))
        assertEquals("/api/v1/view/places?limit=48", ClientRoutes.viewPlaces())
        assertEquals("/api/v1/view/people/ada%20lovelace%2Fx/items?limit=100", ClientRoutes.viewPersonItems("ada lovelace/x"))
        assertEquals("/api/v1/view/people/k/items?cursor=c1&limit=100", ClientRoutes.viewPersonItems("k", "c1"))
    }
}
