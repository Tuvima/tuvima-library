package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class MyListLogicTest {
    private fun page() = MyListLogic.parsePage(WireFixtures.json("my-list-grid.json"), WireFixtures.absolute)

    @Test
    fun theEnginesGridFixtureReadsAsCardsSortAndKindChips() {
        val page = page()

        assertEquals(listOf("Alpha One", "Beta Show"), page.cards.map { it.title })
        assertEquals(2, page.totalCount)
        assertFalse(page.hasMore)
        assertEquals(MyListSort.Added, page.sort)
        assertEquals(listOf("movie" to 1, "tv" to 1), page.kinds.map { it.value to it.count })
        assertEquals("http://engine.test/stream/5a1c0001-0000-4000-8000-000000000031/cover?size=large", page.cards[0].posterUrl)
    }

    @Test
    fun chipsShowOnlyWhenThereAreTwoKindsToChooseBetween() {
        val both = page().kinds
        val one = listOf(MyListKind("movie", "Movies", 3, false), MyListKind("tv", "TV shows", 0, false))

        assertTrue(MyListLogic.showsChips(both))
        assertFalse(MyListLogic.showsChips(one))
        assertEquals(listOf("movie"), MyListLogic.visibleKinds(one).map { it.value })
    }

    @Test
    fun thePickedKindKeepsItsChipEvenAfterItEmptied() {
        val kinds = listOf(MyListKind("movie", "Movies", 0, true), MyListKind("tv", "TV shows", 4, false))

        assertEquals(listOf("movie", "tv"), MyListLogic.visibleKinds(kinds).map { it.value })
    }

    @Test
    fun theAllCountIsTheSumOfTheKindsBecauseTheyIgnoreThePickedKind() {
        assertEquals(2, MyListLogic.allCount(page().kinds, totalCount = 1))
        assertEquals(7, MyListLogic.allCount(emptyList(), totalCount = 7))
    }

    @Test
    fun theCountLineUsesTheDashboardsWords() {
        assertEquals("1 title", MyListLogic.countText(1, null))
        assertEquals("12 titles", MyListLogic.countText(12, null))
        assertEquals("1,204 titles", MyListLogic.countText(1204, ""))
        assertEquals("9 movies", MyListLogic.countText(9, "movie"))
        assertEquals("1 TV show", MyListLogic.countText(1, "tv"))
        assertEquals("4 TV shows", MyListLogic.countText(4, "TV"))
        assertEquals("1 album", MyListLogic.countText(1, "music"))
        assertEquals("2 audiobooks", MyListLogic.countText(2, "audiobook"))
        assertEquals("3 collections", MyListLogic.countText(3, "collection"))
    }

    @Test
    fun anUnknownSortFallsBackToDateAdded() {
        assertEquals(MyListSort.Title, MyListSort.fromWire("title"))
        assertEquals(MyListSort.Year, MyListSort.fromWire("YEAR"))
        assertEquals(MyListSort.Added, MyListSort.fromWire("played"))
        assertEquals(MyListSort.Added, MyListSort.fromWire(null))
        assertEquals(listOf("Date added", "Title", "Release year"), MyListSort.entries.map { it.label })
    }

    @Test
    fun theGridRouteAsksForTheSortAndKindTheDashboardSends() {
        assertEquals(
            "/api/v1/display/grids/personal/my-list?sort=title&type=movie&offset=48&limit=48",
            ClientRoutes.myListGrid("title", "movie", 48, 48),
        )
        assertEquals("/api/v1/display/grids/personal/my-list?offset=0&limit=48", ClientRoutes.myListGrid())
        assertTrue(ClientRoutes.myListGrid(limit = 9_999).endsWith("limit=100"))
    }

    @Test
    fun theSavedRoutesAreTheEnginesProfileStateRoutes() {
        val id = "5a1c0001-0000-4000-8000-000000000001"

        assertEquals("/api/v1/profile-state/saved", ClientRoutes.PROFILE_SAVED)
        assertEquals("/api/v1/profile-state/saved/finished", ClientRoutes.PROFILE_SAVED_FINISHED)
        assertEquals("/api/v1/profile-state/saved/remove", ClientRoutes.PROFILE_SAVED_REMOVE)
        assertEquals("/api/v1/profile-state/saved/restore", ClientRoutes.PROFILE_SAVED_RESTORE)
        assertEquals("/api/v1/profile-state/saved/movie/$id", ClientRoutes.profileSaved(SavedKind.Movie, id))
        assertEquals("/api/v1/profile-state/saved/tvshow/$id", ClientRoutes.profileSaved(SavedKind.TvShow, id))
    }

    @Test
    fun removingSeveralTitlesSaysHowManyAndKeepsExactlyWhatWasTaken() {
        assertEquals("Removed from My List.", MyListLogic.removedText(1))
        assertEquals("3 titles removed from My List.", MyListLogic.removedText(3))

        val taken = listOf(SavedItem(SavedKey.of(SavedKind.Movie, "a"), "2026-11-20T10:15:00+00:00", null))
        val offer = UndoOffer.forRemoved(taken)
        assertEquals("Removed from My List.", offer?.message)
        assertEquals(taken, offer?.removed)
    }

    @Test
    fun noUndoIsOfferedWhenNothingWasRemoved() {
        assertNull(UndoOffer.forRemoved(emptyList()))
    }

    @Test
    fun theWordsAreTheDashboards() {
        assertEquals("Add to My List", MyListLogic.ADD)
        assertEquals("Remove from My List", MyListLogic.REMOVE)
        assertEquals("Remove finished", MyListLogic.REMOVE_FINISHED)
        assertEquals("Your list is empty", MyListLogic.EMPTY_TITLE)
        assertEquals("Press + on any title to save it for later.", MyListLogic.EMPTY_MESSAGE)
    }
}
