package com.tuvima.library.core

import kotlin.random.Random
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Playlists on the phone (spec native-apps WP19): the Engine's two fixtures read, the routes, the order, the paging and the queue a press starts. */
class PlaylistsLogicTest {
    private fun lists() = parsePlaylistsPage(WireFixtures.json("playlists.json"))

    private fun songsPage() = parsePlaylistSongsPage(WireFixtures.json("playlist-items.json"), WireFixtures.absolute)

    private fun song(order: Int, asset: String? = "asset-$order", id: String = "item-$order", duration: Double? = 120.0) = PlaylistSong(
        id = id, workId = "work-$order", title = "Song $order", artist = "Artist", album = "Album",
        coverUrl = "http://engine.test/cover/$order", assetId = asset, durationSeconds = duration, sortOrder = order,
    )

    private fun songs(count: Int) = (0 until count).map { song(it) }

    // ---- the wire ----

    @Test
    fun thePlaylistsFixtureReadsInTheOrderTheEngineGaveWithPinnedSmartAndEmptyOnesMarked() {
        val page = lists()

        assertEquals(listOf("Road trip", "Focus", "Evening wind-down", "New ideas", "Recently played"), page.items.map { it.name })
        assertEquals(5, page.totalCount)
        assertFalse(page.hasMore)
        assertNull(PlaylistsLogic.cursorFor(page))
        assertEquals(listOf(true, false, false, false, false), page.items.map { it.isPinned })
        assertEquals(listOf(false, false, false, false, true), page.items.map { it.isSmart })
        assertEquals(
            listOf(PlaylistCoverMode.Mosaic, PlaylistCoverMode.Picture, PlaylistCoverMode.Mosaic, PlaylistCoverMode.Mosaic, PlaylistCoverMode.Mosaic),
            page.items.map { it.coverMode },
        )
        assertEquals(12, page.items.first().itemCount)
        assertEquals(2874.5, page.items.first().totalDurationSeconds!!, 0.001)
        assertNull(page.items[4].totalDurationSeconds)
        assertEquals("Songs for the long drive", page.items.first().description)
    }

    @Test
    fun thePlaylistItemsFixtureReadsASongWithItsFileAlbumLengthAndOrder() {
        val page = songsPage()
        val first = page.items.first()

        assertEquals(listOf("Open Road", "Night Lights"), page.items.map { it.title })
        assertEquals("The Drivers", first.artist)
        assertEquals("Road Songs", first.album)
        assertEquals("6f0c2a3e-0000-0000-0000-000000000013", first.assetId)
        assertEquals("6f0c2a3e-0000-0000-0000-000000000012", first.workId)
        assertEquals(200.5, first.durationSeconds!!, 0.001)
        assertEquals("http://engine.test/stream/6f0c2a3e-0000-0000-0000-000000000013/cover", first.coverUrl)
        assertEquals(listOf(0, 1), page.items.map { it.sortOrder })
        assertEquals(2, page.totalCount)
        assertFalse(page.hasMore)
    }

    @Test
    fun aSongWithoutTheOptionalFieldsReadsAsMissingNotAsZeroOrBlank() {
        val json = org.json.JSONObject("""{"items":[{"id":"i1","title":"Bare","sort_order":3}],"total_count":1,"has_more":false}""")

        val bare = parsePlaylistSongsPage(json, WireFixtures.absolute).items.single()

        assertNull(bare.assetId)
        assertNull(bare.album)
        assertNull(bare.durationSeconds)
        assertNull(bare.coverUrl)
        assertNull(bare.artist)
        assertEquals(3, bare.sortOrder)
    }

    // ---- routes ----

    @Test
    fun thePlaylistRoutesStayInsideApiV1AndUseTheEnginesPageSizes() {
        assertEquals("/api/v1/collections/playlists?limit=50", ClientRoutes.playlists())
        assertEquals("/api/v1/collections/playlists?cursor=50&limit=100", ClientRoutes.playlists("50", 500))
        assertEquals("/api/v1/collections/p-1/items?limit=200", ClientRoutes.playlistItems("p-1"))
        assertEquals("/api/v1/collections/p-1/items?cursor=200&limit=200", ClientRoutes.playlistItems("p-1", "200", 999))
        assertEquals("/api/v1/collections/p-1/items?limit=12", ClientRoutes.playlistItems("p-1", null, 12))
    }

    // ---- wording ----

    @Test
    fun aTileSaysItsSongCountAndLengthAndANewSmartPlaylistSaysSo() {
        val items = lists().items

        assertEquals("12 songs · 48 min", PlaylistsLogic.tileLine(items[0]))
        assertEquals("30 songs · 2 hr 2 min", PlaylistsLogic.tileLine(items[1]))
        assertEquals("7 songs · 25 min", PlaylistsLogic.tileLine(items[2]))
        assertEquals("No songs", PlaylistsLogic.tileLine(items[3]))
        assertEquals("Smart playlist · 18 songs", PlaylistsLogic.tileLine(items[4]))
    }

    @Test
    fun lengthsReadAsMinutesAndHoursAndASongsOwnLengthAsAClock() {
        assertEquals("", PlaylistsLogic.totalLength(null))
        assertEquals("", PlaylistsLogic.totalLength(0.0))
        assertEquals("1 min", PlaylistsLogic.totalLength(20.0))
        assertEquals("3 hr", PlaylistsLogic.totalLength(10800.0))
        assertEquals("1 hr 1 min", PlaylistsLogic.totalLength(3660.0))
        assertEquals("3:20", PlaylistsLogic.songLength(200.5))
        assertEquals("1:01:40", PlaylistsLogic.songLength(3700.0))
        assertEquals("", PlaylistsLogic.songLength(null))
    }

    @Test
    fun theHeaderShowsTheCountAtOnceAndTheLengthOnlyOnceEverySongIsRead() {
        val read = listOf(song(0, duration = 90.0), song(1, duration = 150.0))

        assertEquals("2 songs", PlaylistsLogic.headerLine(2, read, allLoaded = false, smart = false))
        assertEquals("2 songs · 4 min", PlaylistsLogic.headerLine(2, read, allLoaded = true, smart = false))
        assertEquals("Smart playlist · 1,204 songs", PlaylistsLogic.headerLine(1204, read, allLoaded = false, smart = true))
        assertEquals("", PlaylistsLogic.headerLine(0, emptyList(), allLoaded = false, smart = false))
    }

    @Test
    fun aSongLineNamesArtistAndAlbumWithoutRepeatingAName() {
        assertEquals("Artist · Album", PlaylistsLogic.songLine(song(0)))
        assertEquals("Same", PlaylistsLogic.songLine(song(0).copy(artist = "Same", album = "Same")))
        assertEquals("", PlaylistsLogic.songLine(song(0).copy(artist = null, album = " ")))
    }

    @Test
    fun theWordsNameLibraryAndTheDashboardNeverTheCompany() {
        listOf(
            PlaylistsLogic.EMPTY_LIST, PlaylistsLogic.EMPTY_PLAYLIST, PlaylistsLogic.OFFLINE_LIST, PlaylistsLogic.OFFLINE_PLAYLIST,
            PlaylistsLogic.OFFLINE_NOTHING_HELD, PlaylistsLogic.NO_FILE,
        ).forEach { assertFalse(it, it.contains("Tuvima", ignoreCase = true)) }
        // Playlists are not made or changed here, and the empty states say where that happens.
        assertTrue(PlaylistsLogic.EMPTY_LIST.contains("Dashboard"))
        assertTrue(PlaylistsLogic.EMPTY_PLAYLIST.contains("Dashboard"))
    }

    // ---- covers ----

    @Test
    fun aMosaicPlaylistDrawsFourDifferentCoversAndOtherwiseOneSoItNeverLooksHalfEmpty() {
        val four = listOf("a", "b", "c", "d", "e")

        assertEquals(listOf("a", "b", "c", "d"), PlaylistsLogic.coverLayout(PlaylistCoverMode.Mosaic, four))
        assertEquals(listOf("a"), PlaylistsLogic.coverLayout(PlaylistCoverMode.Picture, four))
        assertEquals(listOf("a"), PlaylistsLogic.coverLayout(PlaylistCoverMode.Mosaic, listOf("a", "a", "a", "a", "b")))
        assertEquals(listOf("b"), PlaylistsLogic.coverLayout(PlaylistCoverMode.Mosaic, listOf(null, "", "b")))
        assertTrue(PlaylistsLogic.coverLayout(PlaylistCoverMode.Mosaic, listOf(null, null)).isEmpty())
    }

    // ---- paging and order ----

    @Test
    fun aLaterPageOfPlaylistsNeverRepeatsOneAlreadyShown() {
        val shown = lists().items

        assertEquals(5, PlaylistsLogic.mergePlaylists(shown, shown).size)
        assertEquals(shown.map { it.id }, PlaylistsLogic.mergePlaylists(emptyList(), shown).map { it.id })
    }

    @Test
    fun songsStayInThePlaylistsOrderAcrossPagesAndARepeatedRowIsShownOnce() {
        val firstPage = (0 until 200).map { song(it) }
        val secondPage = (200 until 230).map { song(it) }
        // The next request overlaps by one row, and a page can arrive late.
        val merged = PlaylistsLogic.mergeSongs(PlaylistsLogic.mergeSongs(emptyList(), secondPage + song(199)), firstPage)

        assertEquals(230, merged.size)
        assertEquals((0 until 230).toList(), merged.map { it.sortOrder })
    }

    @Test
    fun thePagingFollowsTheCursorUntilTheEngineSaysThereIsNoMoreAndNeverForever() {
        val more = PlaylistSongsPage(songs(200), 230, "200", hasMore = true)
        val last = PlaylistSongsPage(songs(30), 230, null, hasMore = false)

        assertEquals("200", PlaylistsLogic.cursorFor(more))
        assertTrue(PlaylistsLogic.hasNextSongPage(more, pagesRead = 1))
        assertNull(PlaylistsLogic.cursorFor(last))
        assertFalse(PlaylistsLogic.hasNextSongPage(last, pagesRead = 2))
        assertFalse(PlaylistsLogic.hasNextSongPage(more.copy(items = emptyList()), pagesRead = 1))
        assertFalse(PlaylistsLogic.hasNextSongPage(more, pagesRead = PlaylistsLogic.MAX_SONG_PAGES))
    }

    // ---- playing ----

    @Test
    fun aSongBecomesAQueueEntryWithItsFileCoverLengthAndWork() {
        val track = PlaylistsLogic.trackOf(song(2, duration = 200.5))!!

        assertEquals("asset-2", track.assetId)
        assertEquals("Song 2", track.title)
        assertEquals("Artist", track.artist)
        assertEquals("Album", track.album)
        assertEquals("http://engine.test/cover/2", track.artworkUrl)
        assertEquals("3:20", track.durationText)
        assertEquals("work-2", track.workId)
        assertNull(PlaylistsLogic.trackOf(song(3, asset = null)))
        assertNull(PlaylistsLogic.trackOf(song(3, asset = " ")))
    }

    @Test
    fun aPressOnASongPlaysThePlaylistFromThereInOrderUnderThePlaylistsName() {
        val list = songs(5)

        val request = PlaylistsLogic.playFrom(list, list[3], " Road trip ", held = null)!!

        assertEquals((0 until 5).map { "asset-$it" }, request.tracks.map { it.assetId })
        assertEquals(3, request.startIndex)
        assertFalse(request.shuffle)
        assertEquals("Road trip", request.sourceName)
    }

    @Test
    fun playStartsAtTheFirstSongAndASongWithNoFileIsSkippedWithoutMovingTheStart() {
        val list = listOf(song(0, asset = null), song(1), song(2))

        val fromTop = PlaylistsLogic.playFrom(list, null, "Road trip", held = null)!!
        assertEquals(listOf("asset-1", "asset-2"), fromTop.tracks.map { it.assetId })
        assertEquals(0, fromTop.startIndex)

        val fromSecond = PlaylistsLogic.playFrom(list, list[2], "Road trip", held = null)!!
        assertEquals("asset-2", fromSecond.tracks[fromSecond.startIndex].assetId)
    }

    @Test
    fun aLongPlaylistKeepsTheQueueTheSizeTheServiceHoldsAroundTheSongPressed() {
        val list = songs(230)

        val request = PlaylistsLogic.playFrom(list, list[150], "Long", held = null)!!

        assertEquals(PlayQueue.MAX_TRACKS, request.tracks.size)
        assertEquals("asset-150", request.tracks[request.startIndex].assetId)
        // The press plays on from the song, in the playlist's order.
        assertEquals("asset-151", request.tracks[request.startIndex + 1].assetId)
    }

    @Test
    fun theSameSongTwiceInAPlaylistStartsAtThePressedRowNotTheFirstCopy() {
        val list = listOf(song(0, asset = "same", id = "i0"), song(1), song(2, asset = "same", id = "i2"))

        val request = PlaylistsLogic.playFrom(list, list[2], "Dupes", held = null)!!

        assertEquals(3, request.tracks.size)
        assertEquals(2, request.startIndex)
    }

    @Test
    fun aPlaylistWithNothingToPlayStartsNothing() {
        assertNull(PlaylistsLogic.playFrom(emptyList(), null, "Empty", held = null))
        assertNull(PlaylistsLogic.playFrom(listOf(song(0, asset = null)), null, "No files", held = null))
        assertNull(PlaylistsLogic.shuffled(emptyList(), "Empty", null, Random(1)))
    }

    @Test
    fun offlineOnlyTheDownloadedSongsPlayAndTheOthersNeverReachTheQueue() {
        val list = songs(5)
        val held = setOf("asset-1", "asset-3")

        val request = PlaylistsLogic.playFrom(list, list[3], "Road trip", held)!!

        assertEquals(listOf("asset-1", "asset-3"), request.tracks.map { it.assetId })
        assertEquals(1, request.startIndex)
        // Pressing a song that is not on the phone plays from the first downloaded one.
        assertEquals(0, PlaylistsLogic.playFrom(list, list[0], "Road trip", held)!!.startIndex)
        assertNull(PlaylistsLogic.playFrom(list, null, "Road trip", held = emptySet()))
        assertEquals(listOf("asset-1", "asset-3"), PlaylistsLogic.playableSongs(list, held).map { it.assetId })
        assertEquals(5, PlaylistsLogic.playableSongs(list, null).size)
    }

    @Test
    fun shuffleStartsAtARandomSongWithShuffleOnAndAShortPlaylistKeepsEverySong() {
        val list = songs(8)

        val request = PlaylistsLogic.shuffled(list, "Road trip", null, Random(7))!!

        assertTrue(request.shuffle)
        assertEquals(8, request.tracks.size)
        assertEquals((0 until 8).map { "asset-$it" }, request.tracks.map { it.assetId })
        assertTrue(request.startIndex in 0 until 8)
        assertEquals("Road trip", request.sourceName)
    }

    @Test
    fun shuffleOfALongPlaylistTakesARandomHundredKeptInPlaylistOrderWithNoRepeats() {
        val list = songs(230)

        val request = PlaylistsLogic.shuffled(list, "Long", null, Random(3))!!

        assertEquals(PlayQueue.MAX_TRACKS, request.tracks.size)
        assertEquals(PlayQueue.MAX_TRACKS, request.tracks.map { it.assetId }.toSet().size)
        val order = request.tracks.map { it.assetId.removePrefix("asset-").toInt() }
        assertEquals(order.sorted(), order)
        assertTrue(request.shuffle)
    }

    @Test
    fun aShuffledQueueFeedsTheExistingShuffleRulesWithoutLosingASong() {
        val request = PlaylistsLogic.shuffled(songs(6), "Road trip", null, Random(11))!!

        val arranged = QueueShuffle.arranged(request, Random(5))

        assertEquals(request.tracks.map { it.assetId }.toSet(), arranged.tracks.map { it.assetId }.toSet())
        assertEquals(0, arranged.startIndex)
    }

    // ---- download all ----

    @Test
    fun downloadAllTakesTheSongsWithAFileAsMusicTracksTheOfflineBatchKnows() {
        val children = PlaylistsLogic.downloadChildren(listOf(song(0), song(1, asset = null), song(2)))

        assertEquals(listOf("asset-0", "asset-2"), children.map { it.assetId })
        assertTrue(children.all { it.entityType == "musicTrack" })
        assertEquals("song", OfflineBatchLogic.noun(children, 1))
        assertEquals("songs", OfflineBatchLogic.noun(children, 2))
        assertEquals("Artist", children.first().subtitle)
        assertEquals("work-0", children.first().workId)
        assertTrue(OfflineBatchLogic.offersBatch(listOf(DetailChildGroup("playlist", "Songs", children, 0))))
    }

    @Test
    fun aPlaylistDownloadStopsAtTheStorageLimitLikeAnAlbumDoes() {
        val children = PlaylistsLogic.downloadChildren(songs(10))

        val plan = OfflineBatchLogic.plan(
            children = children,
            held = setOf("asset-0"),
            committedBytes = 0L,
            capBytes = 25L,
            listedCount = 0,
            estimateFor = { 10L },
        )

        assertEquals(1, plan.alreadyHeld)
        assertEquals(2, plan.toQueue.size)
        assertEquals(BatchStop.StorageCap, plan.stop)
    }

    // ---- offline ----

    @Test
    fun theOfflineLineSaysWhetherAnyOfThePlaylistIsOnThePhone() {
        assertNull(PlaylistsLogic.offlineNote(online = true, heldCount = 0))
        assertEquals(PlaylistsLogic.OFFLINE_PLAYLIST, PlaylistsLogic.offlineNote(online = false, heldCount = 3))
        assertEquals(PlaylistsLogic.OFFLINE_NOTHING_HELD, PlaylistsLogic.offlineNote(online = false, heldCount = 0))
    }

    // ---- search ----

    @Test
    fun aSearchRouteOpensAPlaylistOnlyForTheProfilesOwnIdAndNotTheBuiltInLists() {
        assertEquals("p1", PlaylistsLogic.idFromRoute("/listen/music/playlists/p1"))
        assertEquals("p1", PlaylistsLogic.idFromRoute("/listen/music/playlists/p1?context=listen#x"))
        assertNull(PlaylistsLogic.idFromRoute("/listen/music/playlists/system/favorite-songs"))
        assertNull(PlaylistsLogic.idFromRoute("/listen/music/playlists/system"))
        assertNull(PlaylistsLogic.idFromRoute("/details/musicalbum/a1"))
        assertNull(PlaylistsLogic.idFromRoute(null))
        assertNotNull(PlaylistsLogic.idFromRoute("/listen/music/playlists/6f0c2a3e-0000-0000-0000-0000000000a1"))
    }
}
