package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SearchLogicTest {
    private fun results() = SearchLogic.parse(WireFixtures.json("display-search.json"), WireFixtures.absolute)

    @Test
    fun theEnginesSearchFixtureReadsAsATopResultAndThreeKindsOfResult() {
        val parsed = results()

        assertEquals("alpha", parsed.query)
        assertEquals("Alpha One", parsed.topResult?.title)
        assertEquals(listOf("Books & Comics", "Movies & TV", "Music"), parsed.sections.map { it.title })
        assertEquals(3, parsed.totalCount)
    }

    @Test
    fun aHitOpensTheDetailsPageItsRouteNamesAndKeepsItsPictureOnTheServersAddress() {
        val movie = results().topResult!!

        assertEquals("work", movie.entityType)
        assertEquals("6f0c2a3e-0000-0000-0000-000000000002", movie.id)
        assertEquals("http://engine.test/stream/6f0c2a3e-0000-0000-0000-000000000002/cover", movie.artworkUrl)
        assertEquals("Ada Director · 2024", movie.line)
    }

    @Test
    fun theLineUnderAnAlbumHitNamesTheArtistOnceThenTheYear() {
        val album = results().sections.single { it.key == "music" }.hits.single()

        assertEquals("Alpha Band · 2021", album.line)
    }

    @Test
    fun theTopResultIsDrawnFirstUnderItsOwnHeadingAndIsNotRepeatedInItsKind() {
        val rows = SearchLogic.rows(results())
        val headings = rows.filterIsInstance<SearchRow.Heading>().map { it.title }
        val titles = rows.filterIsInstance<SearchRow.Hit>().map { it.hit.title }

        assertEquals(listOf("Top result", "Books & Comics", "Music"), headings)
        assertEquals(listOf("Alpha One", "Alpha Rising", "Alpha Songs"), titles)
        assertTrue(rows.first() is SearchRow.Heading)
    }

    @Test
    fun aKindWhoseOnlyHitWasAlreadyShownIsNotDrawnWithAnEmptyHeading() {
        val rows = SearchLogic.rows(results())

        assertFalse(rows.filterIsInstance<SearchRow.Heading>().any { it.title == "Movies & TV" })
    }

    @Test
    fun answersWithNothingInThemHaveNoHitsToDraw() {
        val empty = SearchLogic.parse(JSONObject("""{"query":"zzz","sections":[],"totalCount":0}"""), WireFixtures.absolute)

        assertNull(empty.topResult)
        assertFalse(SearchLogic.hasHits(empty))
        assertTrue(SearchLogic.hasHits(results()))
        assertEquals("No results for “zzz”.", SearchLogic.noResults(" zzz "))
    }

    @Test
    fun aHitWithNoTitleOrIdIsSkippedAndAnUnroutedOneFallsBackToItsOwnTypeAndId() {
        val json = JSONObject(
            """{"query":"q","sections":[{"key":"x","title":"X","totalCount":3,"results":[
                {"id":"","entityType":"book","title":"No id"},
                {"id":"b1","entityType":"person","title":"A Person","detailRoute":null},
                {"id":"b2","entityType":"book","title":""}]}]}""",
        )

        val hits = SearchLogic.parse(json, WireFixtures.absolute).sections.single().hits

        assertEquals(listOf("A Person"), hits.map { it.title })
        assertEquals("person", hits.single().entityType)
        assertEquals("b1", hits.single().id)
    }

    @Test
    fun theEngineIsNotAskedUntilAtLeastTwoCharactersAreTyped() {
        assertNull(SearchLogic.queryToSend(""))
        assertNull(SearchLogic.queryToSend(" a "))
        assertEquals("al", SearchLogic.queryToSend("  al "))
    }

    @Test
    fun theSearchRouteEncodesTheQueryAndLimitsTheAnswer() {
        assertEquals("/api/v1/display/search?q=dune+part+two&limit=24", ClientRoutes.search(" dune part two "))
        assertEquals("/api/v1/display/search?q=a%26b&limit=5", ClientRoutes.search("a&b", 5))
    }

    @Test
    fun aPlaylistHitIsDroppedBecauseThePhoneHasNoPlaylistsInVersionOne() {
        val json = JSONObject(
            """
            {"query":"road","totalCount":3,
             "topResult":{"id":"p1","title":"Road trip","entityType":"playlist","detailRoute":"/listen/music/playlists/p1"},
             "sections":[{"key":"music","title":"Music","totalCount":2,"results":[
               {"id":"p2","title":"Road songs","mediaType":"Music","detailRoute":"/listen/music/playlists/p2"},
               {"id":"a1","title":"Road Home","mediaType":"Music","detailRoute":"/details/musicalbum/a1"}]}]}
            """,
        )

        val parsed = SearchLogic.parse(json, WireFixtures.absolute)

        assertNull(parsed.topResult)
        assertEquals(listOf("Road Home"), parsed.sections.single().hits.map { it.title })
    }
}
