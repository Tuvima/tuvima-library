package com.tuvima.library.core

import java.io.File
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/**
 * Android Auto (spec native-apps WP11): the car list's order and the Now playing card's buttons are compared with the contract file itself
 * (`clients/contracts/v1/player-controls.md`, "Cars"), the media service's manifest declarations are checked, and the rules that decide what
 * the car lists and plays are pinned.
 */
class CarBrowseTest {
    private fun repoFile(path: String): File {
        var dir: File? = File("").absoluteFile
        while (dir != null) {
            val candidate = File(dir, path)
            if (candidate.isFile) return candidate
            dir = dir.parentFile
        }
        error("$path was not found above ${File("").absolutePath}")
    }

    private val contract: List<String> by lazy {
        repoFile("clients/contracts/v1/player-controls.md").readText().lines().map { it.trimEnd('\r') }
    }

    private fun table(name: String): List<List<String>> {
        val start = contract.indexOfFirst { it.trim() == "<!-- contract-table: $name -->" }
        assertTrue("the contract has no table $name", start >= 0)
        val rows = mutableListOf<List<String>>()
        for (i in start + 1 until contract.size) {
            val line = contract[i]
            if (!line.startsWith("|")) {
                if (rows.isNotEmpty()) break else continue
            }
            rows += line.split("|").drop(1).dropLast(1).map { it.trim() }
        }
        return rows.drop(2)
    }

    private fun card(
        title: String,
        mediaType: String,
        grouping: String? = null,
        asset: String? = null,
        subtitle: String? = null,
        actions: List<DisplayAction> = emptyList(),
        artist: String? = null,
        album: String? = null,
    ) = DisplayCard(
        id = "id-$title", workId = "work-$title", assetId = asset, collectionId = null, mediaType = mediaType, title = title, subtitle = subtitle,
        facts = emptyList(), description = null, artworkUrl = null, actions = actions, progressPercent = null,
        groupingType = grouping, artist = artist, album = album,
    )

    private fun song(title: String, asset: String = "a-$title") = card(title, "Music", "work", asset, subtitle = "Maya Ellis", artist = "Maya Ellis", album = "Northern Lights")

    private fun book(title: String, asset: String = "b-$title") = card(title, "Audiobook", "work", asset, subtitle = "Maya Ellis")

    private fun album(title: String, id: String) = card(
        title, "Music", "album", subtitle = "Maya Ellis",
        actions = listOf(DisplayAction("play", "Play Album", id, null, null, "/details/musicalbum/$id?context=listen")),
    )

    // ---- the contract's car list ----

    @Test
    fun theCarListEntriesAreTheContractRowsInTheContractOrder() {
        val rows = table("car-list")

        assertEquals(rows.map { it[1] }, CarEntry.entries.map { it.contractName })
        assertEquals((1..rows.size).map { it.toString() }, rows.map { it[0] })
    }

    @Test
    fun theRootKeepsFourTabsWithPlaylistsOnTopAndRecentlyPlayedMovedInsideListen() {
        val root = CarList.rootEntries()

        assertTrue(CarList.PLAYLISTS_AVAILABLE)
        assertEquals(listOf("Continue", "The Listen shelves", "Audiobooks", "Playlists"), root.map { it.contractName })
        assertTrue(root.size <= CarList.ROOT_TAB_LIMIT)
        assertEquals(CarEntry.entries.toList(), root)
        // Turning the flag off is still the only way the Playlists tab can go, and the others keep their order.
        assertEquals(
            listOf("Continue", "The Listen shelves", "Audiobooks"),
            CarList.rootEntries(playlistsAvailable = false).map { it.contractName },
        )
    }

    // ---- Playlists in the car ----

    private fun playlist(id: String, name: String, count: Int, smart: Boolean = false) = Playlist(
        id = id, name = name, description = null, itemCount = count, totalDurationSeconds = 1800.0,
        isPinned = false, isSmart = smart, coverMode = PlaylistCoverMode.Mosaic,
    )

    private fun playlistSong(order: Int, asset: String? = "asset-$order") = PlaylistSong(
        id = "item-$order", workId = "work-$order", title = "Song $order", artist = "The Drivers", album = "Road Songs",
        coverUrl = null, assetId = asset, durationSeconds = 200.0, sortOrder = order,
    )

    @Test
    fun thePlaylistsEntryListsTheProfilesPlaylistsInTheEnginesOrderAndLeavesEmptyOnesOut() {
        val rows = CarCards.playlists(
            listOf(playlist("p1", "Road trip", 12), playlist("p2", "New ideas", 0), playlist("p3", "Recently played", 18, smart = true)),
        )

        assertEquals(listOf("Road trip", "Recently played"), rows.map { it.title })
        assertEquals(listOf(CarCardKind.Playlist, CarCardKind.Playlist), rows.map { it.kind })
        assertEquals(listOf("p1", "p3"), rows.map { it.detailId })
        assertEquals("12 songs · 30 min", rows[0].line)
        assertEquals("Smart playlist · 18 songs · 30 min", rows[1].line)
    }

    @Test
    fun theCarListOfPlaylistsStaysShortAndShowsEachPlaylistOnce() {
        val many = (1..120).map { playlist("p$it", "List $it", 3) }

        assertEquals(CarList.NODE_LIMIT, CarCards.playlists(many).size)
        assertEquals(1, CarCards.playlists(listOf(playlist("same", "A", 3), playlist("same", "A again", 3))).size)
    }

    @Test
    fun aPlaylistOpensAsAFolderOfItsSongsAndItsIdReadsBack() {
        assertEquals(CarNode.Playlist("6f0c-a1"), CarList.parse(CarList.playlistId("6f0c-a1")))
        assertNull(CarList.parse("car:playlist"))
        assertNull(CarList.parse("car:playlist:"))
        assertEquals(CarNode.Entry(CarEntry.Playlists), CarList.parse(CarEntry.Playlists.nodeId))
    }

    @Test
    fun aPlaylistsSongsAreTheListInPlaylistOrderWithoutSongsThatHaveNoFile() {
        val rows = CarCards.playlistSongs(listOf(playlistSong(0), playlistSong(1, asset = null), playlistSong(2)))

        assertEquals(listOf("Song 0", "Song 2"), rows.map { it.title })
        assertEquals(listOf("asset-0", "asset-2"), rows.map { it.assetId })
        assertEquals("The Drivers · Road Songs", rows.first().line)
        assertEquals(CarList.NODE_LIMIT, CarCards.playlistSongs((0 until 80).map { playlistSong(it) }).size)
    }

    @Test
    fun aFileRepeatedInAPlaylistIsOneRowInTheCarSoNoTwoRowsShareAnId() {
        val rows = CarCards.playlistSongs(listOf(playlistSong(0, asset = "same"), playlistSong(1, asset = "same"), playlistSong(2)))

        assertEquals(listOf("same", "asset-2"), rows.map { it.assetId })
    }

    @Test
    fun anAlbumRowOfASmartPlaylistWithNoFileIsLeftOutOfTheCarList() {
        val rows = CarCards.playlistSongs(listOf(playlistSong(0, asset = ""), playlistSong(1, asset = null), playlistSong(2)))

        assertEquals(listOf("asset-2"), rows.map { it.assetId })
    }

    @Test
    fun aPressOnAPlaylistSongPlaysThePlaylistFromThatSongUnderThePlaylistsName() {
        val rows = CarCards.playlistSongs((0 until 4).map { playlistSong(it) })

        val plan = CarPlayPlan.forPress(rows, "asset-2", "Road trip") as CarPlan.Songs

        assertEquals(listOf("asset-0", "asset-1", "asset-2", "asset-3"), plan.request.tracks.map { it.assetId })
        assertEquals(2, plan.request.startIndex)
        assertEquals("Road trip", plan.request.sourceName)
    }

    @Test
    fun aPlaylistFolderItselfNeverStartsPlayingAndAPlaylistSearchResultIsNotACarResult() {
        val folder = CarCards.playlists(listOf(playlist("p1", "Road trip", 3))).single()
        assertNull(CarPlayPlan.forPress(listOf(folder), "", null))

        val hit = SearchHit("playlist", "p1", "Road trip", "Music", null, null)
        assertFalse(CarSearch.isAudio(hit))
        assertTrue(CarSearch.isAudio(hit.copy(entityType = "musicalbum")))
    }

    @Test
    fun theCarEntriesUseTheDashboardsWordsAndNeverTheCompanyName() {
        CarEntry.entries.forEach { assertFalse(it.title, it.title.lowercase().contains("tuvima")) }
        assertEquals("Listen", CarEntry.ListenShelves.title)
    }

    @Test
    fun nodeIdsReadBackToTheSamePlaceAndStrangeIdsAreRefused() {
        assertEquals(CarNode.Root, CarList.parse(CarList.ROOT_ID))
        CarEntry.entries.forEach { assertEquals(CarNode.Entry(it), CarList.parse(it.nodeId)) }
        assertEquals(CarNode.ListenShelf("new-tracks-added"), CarList.parse(CarList.shelfId("new-tracks-added")))
        assertEquals(CarNode.Album("musicalbum", "6f0c"), CarList.parse(CarList.albumId("musicalbum", "6f0c")))
        assertEquals(CarNode.Hit(audiobook = true, entityType = "work", id = "9a"), CarList.parse(CarList.hitId(true, "work", "9a")))
        assertEquals(CarNode.Hit(audiobook = false, entityType = "album", id = "9a"), CarList.parse(CarList.hitId(false, "album", "9a")))
        assertEquals(CarNode.Play("file-1", "car:continue"), CarList.parse(CarList.playId("file-1", CarEntry.Continue.nodeId)))
        assertEquals(CarNode.Play("file-1", "car:album:musicalbum:6f0c"), CarList.parse(CarList.playId("file-1", CarList.albumId("musicalbum", "6f0c"))))
        assertEquals(CarNode.Play("file-1", null), CarList.parse("car:play:file-1"))
        assertNull(CarList.parse("car:play:|car:continue"))
        assertNull(CarList.parse(null))
        assertNull(CarList.parse(""))
        assertNull(CarList.parse("car:album:musicalbum"))
        assertNull(CarList.parse("car:hit:x:work:1"))
        assertNull(CarList.parse("car:play"))
        assertNull(CarList.parse("somewhere-else"))
    }

    @Test
    fun theAudiobooksEntryAsksTheListenLaneForAudiobooksAndNothingElse() {
        val query = CarList.audiobooksQuery()

        assertEquals("listen", query.lane)
        assertEquals("Audiobook", query.mediaType)
        assertEquals(CarList.NODE_LIMIT, query.limit)
        assertTrue(ClientRoutes.browse(query).startsWith("/api/v1/display/browse?lane=listen&mediaType=Audiobook"))
    }

    @Test
    fun theListenEntryReadsTheMusicHomeThroughAnExistingRoute() {
        assertEquals(
            "/api/v1/display/browse?lane=listen&mediaType=Music&grouping=home&includeCatalog=false",
            ClientRoutes.listenHome(),
        )
    }

    // ---- what the car lists ----

    @Test
    fun onlySongsAudiobooksAndAlbumsAreEverListedSoNothingInTheCarCanStartAVideo() {
        val cards = listOf(
            card("A Film", "Movie", "work", "m1"),
            card("An Episode", "TV", "work", "e1"),
            card("A Show", "TV", "show"),
            card("A Book", "Book", "work", "k1"),
            card("A Comic", "Comic", "work", "c1"),
            card("An Artist", "Music", "artist"),
            song("Stay a Little Longer"),
            book("The Way Beyond"),
            album("Northern Lights", "al1"),
        )

        val items = CarCards.items(cards)

        assertEquals(listOf("Stay a Little Longer", "The Way Beyond", "Northern Lights"), items.map { it.title })
        assertEquals(listOf(CarCardKind.Song, CarCardKind.Audiobook, CarCardKind.Album), items.map { it.kind })
    }

    @Test
    fun aSongOrBookWithNoFileAndAnAlbumWithNoPageAreLeftOutBecauseNothingCouldPlayOrOpen() {
        val cards = listOf(
            card("No file", "Music", "work", null),
            card("Book no file", "Audiobook", "work", null),
            card("Album no page", "Music", "album"),
            card("", "Music", "work", "x"),
        )

        assertTrue(CarCards.items(cards).isEmpty())
    }

    @Test
    fun anAlbumOpensItsOwnDetailsPageAndASongCarriesItsSecondLine() {
        val items = CarCards.items(listOf(album("Northern Lights", "al1"), song("Stay a Little Longer")))

        assertEquals("musicalbum", items[0].detailType)
        assertEquals("al1", items[0].detailId)
        assertEquals("Maya Ellis", items[0].line)
        assertEquals("Maya Ellis · Northern Lights", items[1].line)
    }

    @Test
    fun aFileOrAlbumListedTwiceAppearsOnceAndAListStaysShort() {
        val twice = CarCards.items(listOf(song("One", "same"), song("One again", "same"), album("A", "al1"), album("A", "al1")))
        assertEquals(2, twice.size)

        val many = CarCards.items((1..200).map { song("Song $it") })
        assertEquals(CarList.NODE_LIMIT, many.size)
    }

    @Test
    fun theListenEntryShowsTheEnginesShelvesWithRecentlyPlayedFirstAndWithoutTheOnesThatHaveNoCarList() {
        fun shelf(key: String, vararg items: DisplayCard) = DisplayShelf(key, "Title $key", null, items.toList(), null)
        val page = DisplayPage(
            "home", "Music", null,
            listOf(
                shelf("favorite-songs", song("S")),
                shelf("new-tracks-added", song("T")),
                shelf("albums", album("B", "al2")),
                shelf("recently-played", album("A", "al1")),
                shelf("artists", card("An Artist", "Music", "artist")),
                shelf("empty"),
                shelf("videos", card("A Film", "Movie", "work", "m1")),
            ),
        )

        assertEquals(listOf("recently-played", "favorite-songs", "new-tracks-added", "albums"), CarList.listenShelves(page).map { it.key })
    }

    @Test
    fun theCarAsksForAPageAtATimeAndNeverMoreThanTheShortLimit() {
        val items = (1..120).toList()

        assertEquals((1..20).toList(), CarList.slice(items, 0, 20))
        assertEquals((21..40).toList(), CarList.slice(items, 1, 20))
        assertEquals((1..50).toList(), CarList.slice(items, 0, Int.MAX_VALUE))
        assertEquals((1..50).toList(), CarList.slice(items, 0, 0))
        assertTrue(CarList.slice(items, 9, 20).isEmpty())
        assertTrue(CarList.slice(emptyList<Int>(), 0, 20).isEmpty())
    }

    // ---- what a press or a voice request plays ----

    @Test
    fun aPressOnASongPlaysTheListFromThatSongUnderTheListsName() {
        val list = CarCards.items(listOf(song("One"), book("Skipped"), song("Two"), song("Three")))

        val plan = CarPlayPlan.forPress(list, "a-Two", "Recently played") as CarPlan.Songs

        assertEquals(listOf("One", "Two", "Three"), plan.request.tracks.map { it.title })
        assertEquals(1, plan.request.startIndex)
        assertEquals("Recently played", plan.request.sourceName)
        assertFalse(plan.request.shuffle)
    }

    @Test
    fun aPressOnAnAudiobookPlaysThatBookAloneAndAnAlbumRowPlaysNothingByItself() {
        val list = CarCards.items(listOf(book("The Way Beyond"), album("Northern Lights", "al1")))

        val plan = CarPlayPlan.forPress(list, "b-The Way Beyond", "Audiobooks") as CarPlan.Book

        assertEquals("b-The Way Beyond", plan.assetId)
        assertEquals("The Way Beyond", plan.title)
        assertEquals("Maya Ellis", plan.author)
        assertNull(CarPlayPlan.forPress(list, "missing", null))
    }

    private fun detail(title: String, subtitle: String?, children: List<DetailChildGroup>) = DetailModel(
        title = title, subtitle = subtitle, tagline = null, description = null, year = null, runtime = null, contentRating = null,
        genres = emptyList(), artworkUrl = null, cast = emptyList(), editions = emptyList(), progressPercent = null, children = children,
    )

    private fun child(title: String, type: String, asset: String) =
        DetailChild("w-$title", asset, title, "Maya Ellis", null, type, null, "tracks", "Tracks")

    @Test
    fun anAlbumPagePlaysAllItsSongsFromTheFirstAndNamesTheAlbumAsTheSource() {
        val page = detail("Northern Lights", "Maya Ellis", listOf(DetailChildGroup("tracks", "Tracks", listOf(child("One", "musicTrack", "t1"), child("Two", "musicTrack", "t2")), 0)))

        val plan = CarPlayPlan.forAlbum(page) as CarPlan.Songs

        assertEquals(listOf("t1", "t2"), plan.request.tracks.map { it.assetId })
        assertEquals("Northern Lights", plan.request.sourceName)
        assertNull(CarPlayPlan.forAlbum(detail("Empty", null, emptyList())))
    }

    @Test
    fun aVoiceHitOnAnAudiobookStartsTheBookAndOnAnAlbumStartsItsSongs() {
        val bookPage = detail("The Way Beyond", "Maya Ellis", listOf(DetailChildGroup("parts", "Parts", listOf(child("Part 1", "audiobook", "b1")), 0)))
        val albumPage = detail("Northern Lights", "Maya Ellis", listOf(DetailChildGroup("tracks", "Tracks", listOf(child("One", "musicTrack", "t1")), 0)))

        val book = CarPlayPlan.forHit(CarNode.Hit(true, "work", "w1"), bookPage) as CarPlan.Book
        val songs = CarPlayPlan.forHit(CarNode.Hit(false, "album", "w2"), albumPage) as CarPlan.Songs

        assertEquals("b1", book.assetId)
        assertEquals("The Way Beyond", book.title)
        assertEquals(listOf("t1"), songs.request.tracks.map { it.assetId })
        assertNull(CarPlayPlan.forHit(CarNode.Hit(false, "work", "w3"), detail("Nothing", null, emptyList())))
    }

    @Test
    fun aSpokenSongTitleStartsTheSongWithThatExactTitleBeforeAnyLooserMatch() {
        val plan = CarPlayPlan.forSongTitle("stay a little longer", listOf(song("Stay a Little Longer Remix"), song("Stay a Little Longer")))

        assertEquals("a-Stay a Little Longer", (plan as CarPlan.Songs).request.tracks[plan.request.startIndex].assetId)
        assertNull(CarPlayPlan.forSongTitle("x", listOf(book("Not a song"))))
    }

    // ---- voice search ----

    @Test
    fun anEmptyRequestResumesAndAShortOneIsNotSentToTheEngine() {
        assertEquals(CarQuery.Resume, CarSearch.classify(null))
        assertEquals(CarQuery.Resume, CarSearch.classify("   "))
        assertEquals(CarQuery.TooShort, CarSearch.classify("a"))
        assertEquals(CarQuery.Text("northern lights"), CarSearch.classify("  northern lights "))
    }

    @Test
    fun voiceSearchResultsAreOnlyAudioInTheEnginesOrderEachOnce() {
        fun hit(type: String, id: String, media: String, title: String = id) = SearchHit(type, id, title, media, null, null)
        val results = SearchResults(
            "north", hit("movie", "m1", "Movie"),
            listOf(
                SearchSection("watch", "Movies & TV", listOf(hit("movie", "m1", "Movie")), 1),
                SearchSection("music", "Music", listOf(hit("album", "al1", "Music"), hit("work", "s1", "Music")), 2),
                SearchSection("books", "Books", listOf(hit("book", "k1", "Book")), 1),
                SearchSection("audio", "Audiobooks", listOf(hit("work", "b1", "Audiobook"), hit("album", "al1", "Music")), 2),
            ),
            6,
        )

        val hits = CarSearch.audioHits(results)

        assertEquals(listOf("al1", "s1", "b1"), hits.map { it.id })
        assertTrue(CarSearch.nodeOf(hits[2]).audiobook)
        assertFalse(CarSearch.nodeOf(hits[0]).audiobook)
        assertEquals(1, CarSearch.audioHits(results, limit = 1).size)
    }

    // ---- the Now playing card ----

    @Test
    fun theCarCardCarriesTheLockScreensFieldsForMusicAndAudiobooks() {
        val song = AudioNow("a1", TvMediaKind.Music, "Stay a Little Longer", artist = "Maya Ellis", album = "Northern Lights")
        val songCard = LockScreen.card(NowPlayingLogic.factsFor(song, 0.0, hasNext = true))
        assertEquals("Stay a Little Longer", songCard.title)
        assertEquals("Maya Ellis · Northern Lights", songCard.line2)

        val book = AudioNow("b1", TvMediaKind.Audiobook, "The Way Beyond", author = "Maya Ellis", chapters = listOf(Chapter(1, "Chapter 8", 0.0, 100.0)))
        val bookCard = LockScreen.card(NowPlayingLogic.factsFor(book, 10.0, hasNext = false))
        assertEquals("The Way Beyond", bookCard.title)
        assertEquals("Maya Ellis · Chapter 8", bookCard.line2)
    }

    @Test
    fun theCardsOwnButtonsAreTheContractsAndComeFromTheLockScreenActions() {
        val music = LockScreen.actions(TvMediaKind.Music, hasNext = false, hasChapters = false)
        assertTrue(music.containsAll(listOf("previoustrack", "play", "nexttrack")))
        assertFalse(music.contains("seekbackward"))

        val book = LockScreen.actions(TvMediaKind.Audiobook, hasNext = false, hasChapters = true)
        assertTrue(book.containsAll(listOf("seekbackward", "seekforward", "previoustrack", "nexttrack")))
        val bookWithoutChapters = LockScreen.actions(TvMediaKind.Audiobook, hasNext = false, hasChapters = false)
        assertTrue(bookWithoutChapters.containsAll(listOf("seekbackward", "seekforward")))
        assertFalse(bookWithoutChapters.contains("previoustrack"))
        assertFalse(bookWithoutChapters.contains("nexttrack"))
    }

    @Test
    fun theRestOfAQueueWaitsUntilTheSessionHasReplacedTheItemsEvenWhenTheFirstSongWasAlreadyLoaded() {
        // The first song is already the one in play, but the session has not set the new items yet: adding now would be thrown away.
        assertFalse(CarQueueFill.canAddRest(setCountAtStart = 3, setCountNow = 3, currentId = "a1", firstId = "a1"))
        // The session has set items, but another song is in play (not the first yet).
        assertFalse(CarQueueFill.canAddRest(3, 4, "other", "a1"))
        assertFalse(CarQueueFill.canAddRest(3, 4, null, "a1"))
        assertTrue(CarQueueFill.canAddRest(3, 4, "a1", "a1"))
    }

    @Test
    fun musicHasShuffleAndRepeatAsExtraButtonsAndAnAudiobookHasSpeed() {
        val music = CarNowPlaying.extraButtons(TvMediaKind.Music, shuffle = true, repeat = TvRepeatMode.All, speed = 1.0)
        assertEquals(listOf(CarNowPlaying.Extra.Shuffle, CarNowPlaying.Extra.Repeat), music.map { it.extra })
        assertEquals(listOf("Shuffle", "Repeat all"), music.map { it.label })
        assertEquals(listOf(true, true), music.map { it.selected })
        assertEquals(listOf(false, false), CarNowPlaying.extraButtons(TvMediaKind.Music, false, TvRepeatMode.Off, 1.0).map { it.selected })
        assertEquals("Repeat one", CarNowPlaying.extraButtons(TvMediaKind.Music, false, TvRepeatMode.One, 1.0)[1].label)

        val book = CarNowPlaying.extraButtons(TvMediaKind.Audiobook, shuffle = false, repeat = TvRepeatMode.Off, speed = 1.25)
        assertEquals(listOf(CarNowPlaying.Extra.Speed), book.map { it.extra })
        assertEquals("1.25×", book.single().label)

        assertTrue(CarNowPlaying.extraButtons(TvMediaKind.Film, false, TvRepeatMode.Off, 1.0).isEmpty())
    }

    @Test
    fun theSpeedButtonStepsThroughTheContractsPresetsAndComesBackToTheFirst() {
        val presets = table("speed-presets").map { it[0].toDouble() }

        assertEquals(presets, TvPlayerLogic.SPEED_PRESETS)
        assertEquals(1.0, CarNowPlaying.nextSpeed(0.75), 0.0001)
        assertEquals(1.25, CarNowPlaying.nextSpeed(1.0), 0.0001)
        assertEquals(1.5, CarNowPlaying.nextSpeed(1.25), 0.0001)
        assertEquals(2.0, CarNowPlaying.nextSpeed(1.5), 0.0001)
        assertEquals(0.75, CarNowPlaying.nextSpeed(2.0), 0.0001)
        // A speed between presets (set on the phone) goes to the next one up; one above the last goes round.
        assertEquals(1.25, CarNowPlaying.nextSpeed(1.1), 0.0001)
        assertEquals(0.75, CarNowPlaying.nextSpeed(2.6), 0.0001)
    }

    @Test
    fun theContractsCarSectionStillNamesTheSameExtraButtons() {
        val text = contract.joinToString("\n")

        assertTrue(text.contains("Shuffle and Repeat as the template's"))
        assertTrue(text.contains("Speed presets as the template's"))
        assertTrue(text.contains("back 10 seconds, forward 30 seconds"))
        assertTrue(text.contains("Previous chapter and Next chapter"))
    }

    // ---- one profile at a time ----

    @Test
    fun anotherProfileNeverSeesWhatTheLastOneLoaded() {
        val listings = CarListings<List<String>>()
        assertTrue(listings.useProfile("adult"))
        listings["car:continue"] = listOf("Grown-up title")
        assertFalse(listings.useProfile("adult"))
        assertEquals(listOf("Grown-up title"), listings["car:continue"])

        assertTrue(listings.useProfile("kids"))

        assertNull(listings["car:continue"])
        listings["car:continue"] = listOf("Kids title")
        assertTrue(listings.useProfile(null))
        assertNull(listings["car:continue"])
    }

    @Test
    fun aKeptListCanBeDroppedSoTheNextOpenReadsItAgain() {
        val listings = CarListings<List<String>>()
        listings["north"] = listOf("old result")

        listings.remove("north")

        assertNull(listings["north"])
    }

    // ---- the manifest declarations Android Auto reads ----

    @Test
    fun theManifestDeclaresTheMediaServiceAndTheAutoMediaAppDescription() {
        val manifest = repoFile("clients/android/mobile/src/main/AndroidManifest.xml").readText()
        val description = repoFile("clients/android/mobile/src/main/res/xml/automotive_app_desc.xml").readText()

        assertTrue(manifest.contains("android:name=\"com.google.android.gms.car.application\""))
        assertTrue(manifest.contains("android:resource=\"@xml/automotive_app_desc\""))
        assertTrue(manifest.contains("android.media.browse.MediaBrowserService"))
        assertTrue(manifest.contains("androidx.media3.session.MediaLibraryService"))
        assertTrue(manifest.contains("android:foregroundServiceType=\"mediaPlayback\""))
        assertTrue(description.contains("<uses name=\"media\""))
        assertNotNull(Regex("""<service[^>]*TuvimaMediaLibraryService[^>]*android:exported="true"""", RegexOption.DOT_MATCHES_ALL).find(manifest))
    }
}
