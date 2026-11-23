package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class ListenLogicTest {
    private val songWork = "6f0c2a3e-0000-4000-8000-000000000001"

    private fun songs(): List<DisplayCard> =
        WireFixtures.json("display-songs.json").getJSONArray("catalog").objects { parseDisplayCard(it, WireFixtures.absolute) }

    private fun albumsPage(): DisplayShelfPage = parseDisplayShelfPage(WireFixtures.json("display-shelf-page.json"), WireFixtures.absolute)

    private fun saved(vararg ids: String): List<SavedItem> =
        ids.mapIndexed { index, id -> SavedItem(SavedKey.of(SavedKind.Song, id), "2026-11-0${index + 1}T10:00:00Z", index) }

    @Test
    fun theListenTabsAreSongsAlbumsArtistsAudiobooksAndFavoritesWithNoPlaylists() {
        assertEquals(
            listOf("Songs", "Albums", "Artists", "Audiobooks", "Favorites"),
            ListenLogic.tabs().map { it.label },
        )
    }

    @Test
    fun theEnginesSongFixtureReadsAsSongsWithArtistAlbumLengthAndHeart() {
        val first = songs().first()

        assertEquals("Opening", first.title)
        assertEquals("work", first.groupingType)
        assertEquals("Alpha Band", first.artist)
        assertEquals("Alpha Songs", first.album)
        assertEquals("3:10", first.duration)
        assertEquals("1", first.trackNumber)
        assertTrue(first.isFavorite)
        assertEquals("http://engine.test/stream/6f0c2a3e-0000-4000-8000-000000000101/cover?size=large", first.squareArtUrl)
        assertTrue(ListenLogic.isSong(first))
        assertEquals(listOf(true, false, false), songs().map { it.isFavorite })
    }

    @Test
    fun anAlbumOrArtistCardIsNeverASong() {
        val album = albumsPage().shelf.items.first()

        assertEquals("album", album.groupingType)
        assertFalse(ListenLogic.isSong(album))
        assertFalse(ListenLogic.isSong(album.copy(groupingType = "artist")))
    }

    @Test
    fun aSongIsHeartedUnderItsWorkIdAsTheSongKind() {
        val song = songs().first()

        assertEquals(SavedKey.of(SavedKind.Song, songWork), ListenLogic.songKey(song))
        assertEquals(6, ListenLogic.songKey(song)!!.kind.wire)
    }

    @Test
    fun theHeartFollowsTheSavedListOnceItIsReadAndTheCardFlagBefore() {
        val song = songs().first()
        val third = songs()[2]

        // Not read yet: the Engine's flag on the card decides.
        assertTrue(ListenLogic.isHearted(song, null))
        assertFalse(ListenLogic.isHearted(third, null))
        // Read: the saved list decides, even against the card's flag (the person has pressed the heart since).
        assertFalse(ListenLogic.isHearted(song, SavedSet.Empty))
        assertTrue(ListenLogic.isHearted(third, SavedSet.of(saved(third.workId!!))))
        assertEquals("Remove from Favorites", ListenLogic.heartLabel(true))
        assertEquals("Add to Favorites", ListenLogic.heartLabel(false))
    }

    @Test
    fun theLineUnderASongIsArtistThenAlbumAndNeverRepeatsOrLeavesAGap() {
        val song = songs().first()

        assertEquals("Alpha Band · Alpha Songs", ListenLogic.songLine(song))
        assertEquals("Alpha Band", ListenLogic.songLine(song.copy(album = null)))
        assertEquals("Alpha Band", ListenLogic.songLine(song.copy(artist = null, subtitle = "Alpha Band", album = "Alpha Band")))
        assertEquals("", ListenLogic.songLine(song.copy(artist = null, subtitle = null, album = null)))
        assertEquals("3:10", ListenLogic.songLength(song))
        assertEquals("", ListenLogic.songLength(song.copy(duration = null)))
    }

    @Test
    fun songsAreAskedForByTheBrowseRouteWithTheSearchSortAndArtist() {
        val query = ListenLogic.songsQuery("  alpha ", SongSort.Artist, "Alpha Band", offset = 48)

        assertEquals(
            "/api/v1/display/browse?lane=listen&mediaType=Music&search=alpha&creator=Alpha+Band&sort=creator&offset=48&limit=48&includeCatalog=true",
            ClientRoutes.browse(query),
        )
    }

    @Test
    fun aBlankSearchAndArtistAreLeftOutOfTheSongsRequest() {
        val url = ClientRoutes.browse(ListenLogic.songsQuery("  ", SongSort.Title, " ", offset = 0))

        assertEquals(
            "/api/v1/display/browse?lane=listen&mediaType=Music&sort=title&offset=0&limit=48&includeCatalog=true",
            url,
        )
    }

    @Test
    fun favoritesAreAskedForWithTheFavouritesFilterAndALargePage() {
        assertEquals(
            "/api/v1/display/browse?lane=listen&mediaType=Music&status=favorite&offset=0&limit=200&includeCatalog=true",
            ClientRoutes.browse(ListenLogic.favoritesQuery(0)),
        )
    }

    @Test
    fun favoritesComeInThePersonsOwnOrderFromTheSavedListNotTheEnginesFilterOrder() {
        val cards = songs()
        val (a, b, c) = cards.map { it.workId!! }

        // Hand order: c first, then a; b is not in the saved list yet, so it goes last.
        val ordered = ListenLogic.orderFavorites(cards, saved(c, a))

        assertEquals(listOf(c, a, b), ordered.map { it.workId })
    }

    @Test
    fun theSavedListsOtherKindsNeverReorderFavorites() {
        val cards = songs()
        val (a, b, _) = cards.map { it.workId!! }
        val mixed = listOf(SavedItem(SavedKey.of(SavedKind.Movie, b), null, 0)) + saved(a)

        assertEquals(cards.map { it.workId }, ListenLogic.orderFavorites(cards, mixed).map { it.workId })
    }

    @Test
    fun cardsAlreadyShownAreNotAddedAgainWhenAPageOverlaps() {
        val cards = songs()

        assertEquals(3, ListenLogic.mergeCards(cards, cards).size)
        assertEquals(listOf("Opening", "Second Song", "Slow Start"), ListenLogic.mergeCards(cards.take(2), cards).map { it.title })
    }

    @Test
    fun theAlbumsShelfIsAskedForWithoutACursorFirstThenByCursorThenByOffset() {
        assertEquals(
            "/api/v1/display/shelves/albums?lane=listen&mediaType=Music&grouping=home&limit=24",
            ClientRoutes.listenShelf(ListenLogic.SHELF_ALBUMS),
        )
        assertEquals(
            "/api/v1/display/shelves/artists?lane=listen&mediaType=Music&grouping=home&cursor=24&limit=24",
            ClientRoutes.listenShelf(ListenLogic.SHELF_ARTISTS, cursor = "24"),
        )
        assertEquals(
            "/api/v1/display/shelves/albums?lane=listen&mediaType=Music&grouping=home&offset=48&limit=24",
            ClientRoutes.listenShelf(ListenLogic.SHELF_ALBUMS, offset = 48),
        )
    }

    @Test
    fun theAlbumsShelfPageFixtureReadsAsAlbumCardsAndTheNextCursor() {
        val page = albumsPage()

        assertEquals("albums", page.shelf.key)
        assertEquals(listOf("Alpha Songs", "Beta Hours"), page.shelf.items.map { it.title })
        assertEquals("Alpha Band", page.shelf.items.first().subtitle)
        assertEquals("24", page.nextCursor)
        assertEquals(24, page.limit)
        assertTrue(ListenLogic.hasNextShelfPage(page))
        assertNotNull(page.shelf.items.first().squareArtUrl)
    }

    @Test
    fun aShelfPageWithNothingInItIsTheEndEvenIfTheEngineSaysThereIsMore() {
        val empty = albumsPage().let { it.copy(shelf = it.shelf.copy(items = emptyList()), hasMore = true) }

        assertFalse(ListenLogic.hasNextShelfPage(empty))
        assertFalse(ListenLogic.hasNextShelfPage(albumsPage().copy(hasMore = false)))
    }

    @Test
    fun anAlbumOpensItsDetailsPageAndAPlainCardWithoutFlagsStillReads() {
        val album = albumsPage().shelf.items.first()
        val plain = parseDisplayCard(org.json.JSONObject("""{"id":"x","title":"T","mediaType":"Music"}"""), WireFixtures.absolute)

        assertEquals("playAlbum", album.actions.first().type)
        assertEquals(
            "musicalbum" to "6f0c2a3e-0000-4000-8000-000000000300",
            detailTargetFromRoute(album.actions.first().webUrl),
        )
        assertNull(plain.groupingType)
        assertFalse(plain.isFavorite)
        assertNull(plain.artist)
    }

    @Test
    fun songCountsReadAsWords() {
        assertEquals("", ListenLogic.songCountText(0))
        assertEquals("1 song", ListenLogic.songCountText(1))
        assertEquals("1,204 songs", ListenLogic.songCountText(1204))
    }

    private fun musicCard(id: String, artist: String?, grouping: String? = "work") = DisplayCard(
        id = id, workId = id, assetId = null, collectionId = null, mediaType = "Music", title = "T $id", subtitle = null,
        facts = emptyList(), description = null, artworkUrl = null, actions = emptyList(), progressPercent = null,
        groupingType = grouping, artist = artist,
    )

    @Test
    fun anArtistFilterKeepsOnlyThatExactArtistEvenWhenTheEngineMatchedASharedNamePart() {
        val exact = musicCard("1", "Earth, Wind & Fire")
        val nearMiss = musicCard("2", "Earth")

        assertTrue(ListenLogic.isByArtist(exact, "earth, wind & fire "))
        assertFalse(ListenLogic.isByArtist(nearMiss, "Earth, Wind & Fire"))
        assertFalse(ListenLogic.isByArtist(musicCard("3", null), "Earth"))
    }

    @Test
    fun aSavedAlbumThatArrivesAsAPlainMusicWorkCardKeepsItsMyListKey() {
        val asWork = musicCard("alb-1", "Alpha Band")
        val saved = SavedSet.of(listOf(SavedItem(SavedKey.of(SavedKind.Album, "alb-1"), "2026-11-01T10:00:00Z", 0)))

        assertTrue(ListenLogic.isSong(asWork))
        assertEquals(SavedKey.of(SavedKind.Album, "alb-1"), ListenLogic.savedAlbumKey(asWork, saved))
    }

    @Test
    fun aSongHeartedFromListenIsNeverTakenForASavedAlbum() {
        val song = musicCard("song-1", "Alpha Band")
        val hearted = SavedSet.of(listOf(SavedItem(SavedKey.of(SavedKind.Song, "song-1"), "2026-11-01T10:00:00Z", 0)))

        assertNull(ListenLogic.savedAlbumKey(song, hearted))
        assertNull(ListenLogic.savedAlbumKey(song, null))
    }
}
