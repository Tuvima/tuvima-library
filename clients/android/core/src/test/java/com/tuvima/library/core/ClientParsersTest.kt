package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The wire readers, run against the shared fixtures the Engine's contract tests also pin. */
class ClientParsersTest {
    @Test
    fun theUniverseFixtureReadsItsTotalsAndFormats() {
        val universe = WireFixtures.universe()
        assertEquals("Q4242", universe.qid)
        assertEquals("Test Universe", universe.name)
        assertNull(universe.description)
        assertEquals(5, universe.itemCount)
        assertEquals(3, universe.seriesCount)
        assertEquals(listOf("Movie", "Book"), universe.mediaTypes)
    }

    @Test
    fun theUniverseFixtureKeepsTheEnginesShelfOrder() {
        val shelves = WireFixtures.universe().shelves
        assertEquals(listOf("Series Alpha", "Series Beta", "Series Gamma"), shelves.map { it.shelf.title })
        assertEquals(listOf("series", "series", "series"), shelves.map { it.groupType })
        assertEquals(listOf(2, 2, 0), shelves.map { it.itemCount })
    }

    @Test
    fun aRunShelfReadsItsCardsAndHasNoAlsoRow() {
        val alpha = WireFixtures.universe().shelves[0]
        assertEquals("6f0c2a3e-0000-0000-0000-000000000001", alpha.collectionId)
        assertEquals(listOf("Alpha One", "Alpha Three"), alpha.shelf.items.map { it.title })
        assertEquals("/api/v1/display/groups/6f0c2a3e-0000-0000-0000-000000000001", alpha.shelf.seeAllRoute)
        assertNull(alpha.alsoInThisWorld)
        assertEquals(0, alpha.alsoInThisWorldCount)
        val card = alpha.shelf.items[0]
        assertEquals("Movie", card.mediaType)
        assertEquals(listOf("PG"), card.facts)
        assertEquals("http://engine.test/stream/6f0c2a3e-0000-0000-0000-000000000003/cover", card.posterUrl)
        assertEquals(listOf("playAsset", "openWork"), card.actions.map { it.type })
        assertNull(card.progressPercent)
    }

    @Test
    fun theEmptyRunShelfReadsItsAlsoInThisWorldRow() {
        val gamma = WireFixtures.universe().shelves[2]
        assertEquals(0, gamma.itemCount)
        assertTrue(gamma.shelf.items.isEmpty())
        val also = gamma.alsoInThisWorld
        assertNotNull(also)
        assertEquals("Also in this world", also!!.title)
        assertEquals(listOf("Gamma Companion"), also.items.map { it.title })
        assertEquals(1, gamma.alsoInThisWorldCount)
    }

    @Test
    fun theUniverseFixtureReadsCastWithAndWithoutCharacters() {
        val cast = WireFixtures.universe().cast
        assertEquals(listOf("Ann Actor", "Ben Narrator"), cast.map { it.name })
        assertEquals(listOf(UniverseCharacter("Q8200", "Hero")), cast[0].characters)
        assertEquals(2, cast[0].workCount)
        assertNull(cast[0].headshotUrl)
        assertTrue(cast[1].characters.isEmpty())
        assertEquals(1, cast[1].workCount)
    }

    @Test
    fun theUniverseFixtureReadsItsAdaptationLink() {
        assertEquals(
            listOf(UniverseAdaptation("Q8100", "The Film", "Object", "based on", "Q8101", "The Novel")),
            WireFixtures.universe().adaptations,
        )
    }

    @Test
    fun anOlderUniverseAnswerWithoutTheLaterFieldsReadsAsEmpty() {
        val old = JSONObject(
            """
            {"qid": "Q1", "name": "Old Universe", "item_count": 2,
             "shelves": [{"collection_id": "c1", "item_count": 2,
                          "shelf": {"key": "c1", "title": "Old Series", "items": []}}]}
            """,
        )
        val universe = parseUniverseDetail(old, WireFixtures.absolute)
        assertEquals(0, universe.seriesCount)
        assertTrue(universe.mediaTypes.isEmpty())
        assertTrue(universe.cast.isEmpty())
        assertTrue(universe.adaptations.isEmpty())
        val shelf = universe.shelves.single()
        assertEquals("series", shelf.groupType)
        assertNull(shelf.alsoInThisWorld)
        assertEquals(0, shelf.alsoInThisWorldCount)
        assertEquals("Old Series", shelf.shelf.title)
    }

    @Test
    fun aUniverseAnswerWithoutAQidOrNameFallsBackToTheRequestedQid() {
        val universe = parseUniverseDetail(JSONObject("{}"), WireFixtures.absolute, fallbackQid = "Q7")
        assertEquals("Q7", universe.qid)
        assertEquals("Q7", universe.name)
        assertTrue(universe.shelves.isEmpty())
    }

    @Test
    fun theCollectionsFixtureReadsUniverseQids() {
        val page = parseCollectionsPage(WireFixtures.json("collections.json"), WireFixtures.absolute)
        assertEquals(3, page.totalCount)
        assertTrue(page.hasMore)
        assertEquals("2", page.nextCursor)
        val test = page.items.single { it.name == "Test Universe" }
        assertEquals("universe", test.groupType)
        assertEquals(6, test.itemCount)
        assertEquals("Q4242", test.universeQid())
        assertEquals(4, test.coverUrls.size)
        assertTrue(test.coverUrls.all { it.startsWith("http://engine.test/stream/") })
        assertEquals("Q5000", page.items.single { it.name == "Adult Universe" }.universeQid())
    }

    @Test
    fun aCollectionEntryWithoutAUniverseQidHasNone() {
        val entry = parseCollectionEntry(JSONObject("""{"id": "c1", "name": "Dune Novels", "group_type": "series"}"""), WireFixtures.absolute)
        assertNull(entry.universeQid())
        assertTrue(entry.coverUrls.isEmpty())
    }
}
