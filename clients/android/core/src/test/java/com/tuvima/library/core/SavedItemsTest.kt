package com.tuvima.library.core

import org.json.JSONArray
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class SavedItemsTest {
    private val movie = "5a1c0001-0000-4000-8000-000000000001"

    @Test
    fun theEnginesFixtureReadsAsTheSavedTitlesInOrder() {
        val items = parseSavedItems(JSONArray(WireFixtures.text("my-list-saved.json")))

        assertEquals(
            listOf(SavedKind.Movie, SavedKind.TvShow, SavedKind.Book),
            items.map { it.key.kind },
        )
        assertEquals("2026-11-01T09:30:00+00:00", items[0].savedAt)
        assertNull(items[0].position)
    }

    @Test
    fun aHeartedSongKeepsItsFavoritesPosition() {
        val items = parseSavedItems(JSONArray("""[{"entityKind":6,"entityId":"$movie","savedAt":"2026-11-12T00:00:00+00:00","position":-2}]"""))

        assertEquals(SavedKind.Song, items.single().key.kind)
        assertEquals(-2, items.single().position)
    }

    @Test
    fun aKindArrivesAsANumberOrAsItsName() {
        assertEquals(SavedKind.Album, SavedKind.fromWire(5))
        assertEquals(SavedKind.TvShow, SavedKind.fromWire("TvShow"))
        assertEquals(SavedKind.TvShow, SavedKind.fromWire("tvshow"))
        assertEquals(SavedKind.Playlist, SavedKind.fromWire("8"))
        assertNull(SavedKind.fromWire(99))
        assertNull(SavedKind.fromWire(null))
    }

    @Test
    fun aRowWithAnUnknownKindOrNoIdIsSkipped() {
        val items = parseSavedItems(
            JSONArray(
                """[{"entityKind":99,"entityId":"$movie"},{"entityKind":0},{"entityKind":0,"entityId":"$movie"}]""",
            ),
        )

        assertEquals(listOf(SavedKey(SavedKind.Movie, movie)), items.map { it.key })
    }

    @Test
    fun idsAreKeptInLowerCaseSoTheSameTitleIsAlwaysOneKey() {
        assertEquals(SavedKey.of(SavedKind.Movie, movie), SavedKey.of(SavedKind.Movie, movie.uppercase()))
    }

    @Test
    fun aDetailsRouteStandsForTheTitleMyListStoresIt() {
        assertEquals(SavedKey.of(SavedKind.Movie, movie), savedKeyForEntity("movie", movie))
        assertEquals(SavedKey.of(SavedKind.TvShow, movie), savedKeyForEntity("tvshow", movie))
        assertEquals(SavedKey.of(SavedKind.TvShow, movie), savedKeyForEntity("tvShow", movie))
        assertEquals(SavedKey.of(SavedKind.Book, movie), savedKeyForEntity("book", movie))
        assertEquals(SavedKey.of(SavedKind.Comic, movie), savedKeyForEntity("comicIssue", movie))
        assertEquals(SavedKey.of(SavedKind.Audiobook, movie), savedKeyForEntity("audiobook", movie))
        assertEquals(SavedKey.of(SavedKind.Album, movie), savedKeyForEntity("musicAlbum", movie))
    }

    @Test
    fun anEpisodeIsSavedAsItsShowAndNeverGuessed() {
        val show = "5a1c0001-0000-4000-8000-000000000009"

        assertEquals(SavedKey.of(SavedKind.TvShow, show), savedKeyForEntity("tvEpisode", movie, showId = show))
        assertNull(savedKeyForEntity("tvEpisode", movie))
    }

    @Test
    fun groupsAndUnknownThingsGetNoPlus() {
        assertNull(savedKeyForEntity("collection", movie))
        assertNull(savedKeyForEntity("work", movie))
        assertNull(savedKeyForEntity("movie", ""))
    }

    @Test
    fun removeSendsTheKindAsTheEnginesNumber() {
        val body = removeSavedBody(listOf(SavedKey.of(SavedKind.Movie, movie), SavedKey.of(SavedKind.Audiobook, movie)))

        val items = body.getJSONArray("items")
        assertEquals(2, items.length())
        assertEquals(0, items.getJSONObject(0).getInt("entityKind"))
        assertEquals(4, items.getJSONObject(1).getInt("entityKind"))
        assertEquals(movie, items.getJSONObject(0).getString("entityId"))
    }

    @Test
    fun undoSendsBackTheOriginalDatesAndPositions() {
        val removed = parseSavedItems(JSONArray(WireFixtures.text("my-list-saved.json"))).take(2)

        val body = restoreSavedBody(removed)

        val items = body.getJSONArray("items")
        assertEquals("2026-11-01T09:30:00+00:00", items.getJSONObject(0).getString("savedAt"))
        assertTrue(items.getJSONObject(0).isNull("position"))
        assertEquals(1, items.getJSONObject(1).getInt("entityKind"))
    }

    @Test
    fun theSavedSetShowsAPressAtOnceAndCanPutItBack() {
        val key = SavedKey.of(SavedKind.Movie, movie)
        val empty = SavedSet.Empty

        assertTrue(empty.pressWouldSave(key))
        val saved = empty.with(key)
        assertTrue(key in saved)
        assertFalse(saved.pressWouldSave(key))
        assertFalse(key in saved.without(key))
        // The old state is untouched, so a refused press can simply go back to it.
        assertFalse(key in empty)
        assertEquals(1, saved.size)
        assertEquals(1, saved.with(key).size)
    }

    @Test
    fun aGroupSavedFromTheDashboardIsFoundByTheIdOnItsCard() {
        val collection = "5a1c0001-0000-4000-8000-0000000000aa"
        val set = SavedSet.of(
            listOf(SavedItem(SavedKey.of(SavedKind.Collection, collection), null, null)),
        )

        assertEquals(SavedKind.Collection, set.keyForId(collection.uppercase())?.kind)
        assertNull(set.keyForId("5a1c0001-0000-4000-8000-0000000000bb"))
        assertNull(set.keyForId(null))
    }
}
