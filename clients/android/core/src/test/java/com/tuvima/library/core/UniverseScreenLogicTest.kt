package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** The Universe screen's pure logic, checked against the shared wire fixture the Engine's contract tests also pin. */
class UniverseScreenLogicTest {
    private fun fixture() = WireFixtures.universe()

    private fun detail(series: Int, works: Int, formats: List<String>) =
        UniverseDetail("Q1", "U", null, works, emptyList(), seriesCount = series, mediaTypes = formats)

    private fun shelf(itemCount: Int, runCards: Int, also: Int?, alsoCount: Int = also ?: 0, group: String = "series") =
        UniverseShelf(
            collectionId = "c",
            groupType = group,
            itemCount = itemCount,
            shelf = DisplayShelf("c", "Name", null, List(runCards) { card("r$it") }, null),
            alsoInThisWorld = also?.let { DisplayShelf("c:also", "Also in this world", null, List(it) { i -> card("a$i") }, null) },
            alsoInThisWorldCount = alsoCount,
        )

    private fun card(id: String) = DisplayCard(id, id, null, null, "Book", id, null, emptyList(), null, null, emptyList(), null, null)

    @Test
    fun theFixtureKeepsTheEnginesShelfOrder() {
        val universe = fixture()
        assertEquals(listOf("Series Alpha", "Series Beta", "Series Gamma"), universe.shelves.map { it.shelf.title })
    }

    @Test
    fun theEmptyRunShelfHasAnAlsoRowAndNoRunRow() {
        val gamma = fixture().shelves[2]
        assertEquals(0, gamma.itemCount)
        assertNotNull(gamma.alsoInThisWorld)
        assertEquals(1, gamma.alsoInThisWorld!!.items.size)
        assertEquals(1, gamma.alsoInThisWorldCount)
        val rows = universeShelfRows(gamma)!!
        assertFalse(rows.showRun)
        assertNull(rows.run)
        assertEquals(1, rows.also!!.items.size)
        assertEquals("Series · Also in this world · 1 item", rows.kindLine)
        assertFalse(rows.kindLine.contains("0 items"))
    }

    @Test
    fun aShelfWithARunShowsItsCountAndNoAlsoRowWhenThereIsNone() {
        val alpha = universeShelfRows(fixture().shelves[0])!!
        assertTrue(alpha.showRun)
        assertEquals(2, alpha.run!!.items.size)
        assertNull(alpha.also)
        assertEquals("Series · 2 items", alpha.kindLine)
    }

    @Test
    fun theTotalsLineCountsSeriesWorksAndFormats() {
        assertEquals("3 Series · 5 Works · 2 formats", universeTotalsLine(fixture()))
    }

    @Test
    fun theTotalsLineUsesSingularsAndOmitsZeroParts() {
        assertEquals("1 Series · 1 Work · 1 format", universeTotalsLine(detail(1, 1, listOf("Book"))))
        assertEquals("14 Works", universeTotalsLine(detail(0, 14, emptyList())))
        assertEquals("2 Series · 2 formats", universeTotalsLine(detail(2, 0, listOf("Book", "Movie"))))
        assertEquals("", universeTotalsLine(detail(0, 0, emptyList())))
    }

    @Test
    fun aUniverseOfOnlyShowsSaysShowsLikeTheDashboard() {
        val oneShow = detail(1, 1, listOf("TV")).copy(shelves = listOf(shelf(1, 1, null, group = "show")))
        assertEquals("1 show · 1 Work · 1 format", universeTotalsLine(oneShow))
        val twoShows = oneShow.copy(seriesCount = 2, shelves = List(2) { shelf(1, 1, null, group = "show") })
        assertEquals("2 shows · 1 Work · 1 format", universeTotalsLine(twoShows))
        val mixed = oneShow.copy(seriesCount = 2, shelves = listOf(shelf(1, 1, null, group = "show"), shelf(1, 1, null)))
        assertEquals("2 Series · 1 Work · 1 format", universeTotalsLine(mixed))
    }

    @Test
    fun theCastLineNamesCharactersOrIsNull() {
        val cast = fixture().cast
        assertEquals("Ann Actor", cast[0].name)
        assertEquals("as Hero", castCharactersLine(cast[0]))
        assertEquals("Ben Narrator", cast[1].name)
        assertNull(castCharactersLine(cast[1]))
        val two = UniverseCastMember("p", "P", null, listOf(UniverseCharacter("Q1", "A"), UniverseCharacter("Q2", "B")), 2)
        assertEquals("as A, B", castCharactersLine(two))
    }

    @Test
    fun theAdaptationLineCapitalisesTheRelationship() {
        val link = fixture().adaptations.single()
        assertEquals("The Film — Based on The Novel", adaptationLine(link))
        assertEquals(
            "Show — Derivative of Book",
            adaptationLine(UniverseAdaptation("Q1", "Show", null, "derivative of", "Q2", "Book")),
        )
    }

    @Test
    fun aShelfWithNeitherRunNorAlsoItemsIsSkipped() {
        assertNull(universeShelfRows(shelf(itemCount = 0, runCards = 0, also = null)))
        assertNull(universeShelfRows(shelf(itemCount = 0, runCards = 0, also = 0)))
    }

    @Test
    fun theKindLabelAndSingularCountReadForAShow() {
        assertEquals("Show · 1 item", universeShelfRows(shelf(1, 1, null, group = "show"))!!.kindLine)
    }

    @Test
    fun theAlsoRowUsesTheEnginesCountAndFallsBackToTheCardsGiven() {
        assertEquals("Also in this world · 7 items", universeShelfRows(shelf(2, 2, 3, alsoCount = 7))!!.alsoHeading())
        assertEquals("Also in this world · 3 items", universeShelfRows(shelf(2, 2, 3, alsoCount = 0))!!.alsoHeading())
    }
}
