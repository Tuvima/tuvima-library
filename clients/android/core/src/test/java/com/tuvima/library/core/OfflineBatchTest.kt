package com.tuvima.library.core

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Checks reading the files a Details page lists and planning a whole season, album or audiobook download. */
class OfflineBatchTest {
    private val none: (String?) -> String? = { it }

    private fun child(
        assetId: String,
        type: String = "tvEpisode",
        groupKey: String = "season-1",
        groupTitle: String = "Season 1",
        title: String = "Episode $assetId",
        track: String? = null,
    ) = DetailChild("work-$assetId", assetId, title, null, null, type, track, groupKey, groupTitle)

    // ---- Reading a Details answer ----

    // The real Details answers (`details-*.json` in tests/fixtures/native-client-v1, also checked against the Contracts types by the Engine's tests).
    private val showPage: String get() = WireFixtures.text("details-show-sequence.json")

    @Test
    fun parse_ListsOwnedEpisodesOfAShowFromSequencePlacementBecauseItsMediaGroupsAreEmpty() {
        val groups = DetailChildren.parse(WireFixtures.json("details-show-sequence.json"), none)

        assertEquals(listOf("season-1", "season-2"), groups.map { it.key })
        assertEquals(listOf("99999999-9999-9999-9999-999999999991", "99999999-9999-9999-9999-999999999992"), groups[0].children.map { it.assetId })
        assertEquals(1, groups[0].skipped)
        val back = groups[1].children.single()
        assertEquals("tvEpisode", back.entityType)
        assertEquals("88888888-8888-8888-8888-888888888884", back.workId)
        assertEquals("11111111-1111-1111-1111-111111111111", back.showId)
        assertEquals("Back Again", back.title)
        assertEquals("1", back.trackNumber)
        assertEquals("season-2", back.groupKey)
    }

    @Test
    fun parse_NamesAShowEpisodeBySeasonAndNumberForTheDownloadsList() {
        val first = DetailChildren.parse(WireFixtures.json("details-show-sequence.json"), none)[0].children[0]

        val name = OfflineBatchLogic.nameFor("The Fixture Show", first, 0, 1)

        assertEquals("The First Door", name.title)
        assertEquals(OfflineLogic.episodeLabel(1, 1), name.subtitle)
    }

    @Test
    fun parse_ReadsAShowFromItsOrderedItemsWhenNoGroupCarriesEpisodes() {
        val json = WireFixtures.json("details-show-sequence.json")
        val placement = json.getJSONObject("sequencePlacement")
        val ordered = org.json.JSONArray()
        val groups = placement.getJSONArray("groups")
        for (g in 0 until groups.length()) {
            val items = groups.getJSONObject(g).getJSONArray("items")
            for (i in 0 until items.length()) ordered.put(items.getJSONObject(i))
            groups.getJSONObject(g).put("items", org.json.JSONArray())
        }
        placement.put("orderedItems", ordered)

        val read = DetailChildren.parse(json, none)

        assertEquals(listOf("season-1", "season-2"), read.map { it.key })
        assertEquals(3, OfflineBatchLogic.allChildren(read).size)
    }

    @Test
    fun parse_SkipsAnEpisodeWithoutAnEpisodeContextBecauseItNamesNoFile() {
        val json = WireFixtures.json("details-show-sequence.json")
        val items = json.getJSONObject("sequencePlacement").getJSONArray("groups").getJSONObject(1).getJSONArray("items")
        items.getJSONObject(0).put("episodeContext", org.json.JSONObject.NULL)

        val read = DetailChildren.parse(json, none)

        assertEquals(listOf("season-1", "season-2"), read.map { it.key })
        assertTrue(read[1].children.isEmpty())
        assertEquals(1, read[1].skipped)
    }

    @Test
    fun parse_ReadsAnAlbumsSongsFromPlainWorksAndKeepsTheirNumbers() {
        val group = DetailChildren.parse(WireFixtures.json("details-album-media-groups.json"), none).single()

        assertEquals(listOf("musicTrack", "musicTrack"), group.children.map { it.entityType })
        assertEquals(listOf("1", "2"), group.children.map { it.trackNumber })
        assertEquals("The Fixture Band", group.children[0].subtitle)
        assertEquals("5a5a5a5a-0000-0000-0000-000000000011", group.children[0].workId)
        // The song the Library does not hold has no file to keep.
        assertEquals(1, group.skipped)
    }

    @Test
    fun parse_ListsAnAudiobookFileOnceHoweverManyChaptersNameIt() {
        val children = DetailChildren.parse(WireFixtures.json("details-audiobook-media-groups.json"), none).single().children

        assertEquals(
            listOf("6f0c2a3e-0000-0000-0000-000000000053", "6f0c2a3e-0000-0000-0000-000000000054"),
            children.map { it.assetId },
        )
        assertEquals(listOf("audiobook", "audiobook"), children.map { it.entityType })
    }

    @Test
    fun parse_LeavesBooksComicsAndContainersOutBecauseThePhoneCannotKeepThem() {
        val json = JSONObject(
            """{"mediaGroups":[{"key":"items","title":"Items","items":[
                {"id":"x1","entityType":6,"title":"A Book","assetId":"bk","isOwned":true},
                {"id":"x2","entityType":9,"title":"A Comic","assetId":"cm","isOwned":true},
                {"id":"x3","entityType":3,"title":"A Show","assetId":"sh","isOwned":true},
                {"id":"x4","entityType":1,"title":"A Film","assetId":"fl","isOwned":true}]}]}""",
        )

        val group = DetailChildren.parse(json, none).single()

        assertEquals(listOf("fl"), group.children.map { it.assetId })
        assertEquals(3, group.skipped)
    }

    @Test
    fun parseDetailModel_CarriesTheListedFilesWithTheRestOfThePage() {
        val model = parseDetailModel(JSONObject(showPage), none)

        assertEquals("The Fixture Show", model.title)
        assertEquals(3, OfflineBatchLogic.allChildren(model.children).size)
    }

    @Test
    fun wireName_DecodesEveryEntityTypeTheFixturesUseToTheKindTheyDownloadAs() {
        val expected = mapOf(
            "details-show-sequence.json" to Pair(3, "TvShow"),
            "details-audiobook-media-groups.json" to Pair(8, "Audiobook"),
            "details-album-media-groups.json" to Pair(11, "MusicAlbum"),
        )
        expected.forEach { (file, pair) ->
            val page = WireFixtures.json(file)
            assertEquals(file, pair.second, DetailChildren.wireName(page.opt("entityType")))
        }
        // The page's own kind (a show, an album) is a container, never a download; a book's audiobook is the one file.
        assertNull(DetailChildren.downloadTypeFor("TvShow"))
        assertNull(DetailChildren.downloadTypeFor("MusicAlbum"))
        assertEquals("audiobook", DetailChildren.downloadTypeFor("Audiobook"))
        assertEquals("tvEpisode", DetailChildren.downloadTypeFor(DetailChildren.wireName(5)))
        assertEquals("musicTrack", DetailChildren.downloadTypeFor(DetailChildren.wireName(0)))
    }

    @Test
    fun parse_ReturnsNothingWhenThePageHasNoGroups() {
        assertTrue(DetailChildren.parse(JSONObject("""{"title":"A Film"}"""), none).isEmpty())
    }

    @Test
    fun wireName_ReadsTheNumberTheEngineSendsAndAWord() {
        assertEquals("Audiobook", DetailChildren.wireName(8))
        assertEquals("MusicAlbum", DetailChildren.wireName("11"))
        assertEquals("TvEpisode", DetailChildren.wireName("TvEpisode"))
        assertEquals("TvEpisode", DetailChildren.wireName("tvepisode"))
        assertEquals("", DetailChildren.wireName(99))
        assertEquals("", DetailChildren.wireName(null))
        assertNull(DetailChildren.downloadTypeFor("Person"))
    }

    // ---- Choices ----

    @Test
    fun scopes_OffersEverythingThenEachSeasonForAShow() {
        val groups = DetailChildren.parse(JSONObject(showPage), none)

        val scopes = OfflineBatchLogic.scopes(groups)

        assertEquals(listOf("All 3 episodes", "Season 1 (2 episodes)", "Season 2 (1 episode)"), scopes.map { it.label })
        assertEquals(listOf("all", "season-1", "season-2"), scopes.map { it.key })
    }

    @Test
    fun scopes_OffersOnlyAllForAnAlbumWithOneGroup() {
        val songs = listOf(child("t1", "musicTrack", "tracks", "Tracks"), child("t2", "musicTrack", "tracks", "Tracks"))

        val scopes = OfflineBatchLogic.scopes(listOf(DetailChildGroup("tracks", "Tracks", songs, 0)))

        assertEquals(listOf("All 2 songs"), scopes.map { it.label })
    }

    @Test
    fun offersBatch_IsTrueOnlyWhenMoreThanOneFileIsListed() {
        val one = listOf(DetailChildGroup("tracks", "Tracks", listOf(child("t1", "audiobook")), 0))
        val two = listOf(DetailChildGroup("tracks", "Tracks", listOf(child("t1", "audiobook"), child("t2", "audiobook")), 0))

        assertFalse(OfflineBatchLogic.offersBatch(one))
        assertTrue(OfflineBatchLogic.offersBatch(two))
        assertFalse(OfflineBatchLogic.offersBatch(emptyList()))
    }

    // ---- Planning ----

    private val big = { _: DetailChild -> 100L }

    @Test
    fun plan_SkipsFilesAlreadyHeldAndCountsThem() {
        val kids = listOf(child("a"), child("b"), child("c"))

        val plan = OfflineBatchLogic.plan(kids, held = setOf("b"), committedBytes = 0, capBytes = 10_000, listedCount = 0, estimateFor = big)

        assertEquals(listOf("a", "c"), plan.toQueue.map { it.assetId })
        assertEquals(1, plan.alreadyHeld)
        assertEquals(BatchStop.None, plan.stop)
    }

    @Test
    fun plan_StopsAtTheStorageLimitCountingWhatIsAlreadyChosen() {
        val kids = (1..5).map { child("e$it") }

        val plan = OfflineBatchLogic.plan(kids, emptySet(), committedBytes = 0, capBytes = 250, listedCount = 0, estimateFor = big)

        // 100 each: two fit (200), the third would take it to 300.
        assertEquals(2, plan.toQueue.size)
        assertEquals(BatchStop.StorageCap, plan.stop)
    }

    @Test
    fun plan_StopsAtTheEnginesListLimit() {
        val kids = (1..5).map { child("e$it") }

        val plan = OfflineBatchLogic.plan(kids, emptySet(), 0, 1_000_000, listedCount = 498, estimateFor = big)

        assertEquals(2, plan.toQueue.size)
        assertEquals(BatchStop.ListFull, plan.stop)
    }

    @Test
    fun plan_StopsAtTheMostOnePressMayStart() {
        val kids = (1..6).map { child("e$it") }

        val plan = OfflineBatchLogic.plan(kids, emptySet(), 0, 1_000_000, 0, big, maxBatch = 4)

        assertEquals(4, plan.toQueue.size)
        assertEquals(BatchStop.TooMany, plan.stop)
    }

    @Test
    fun plan_QueuesNothingWhenTheLimitIsAlreadyReached() {
        val plan = OfflineBatchLogic.plan(listOf(child("a")), emptySet(), committedBytes = 500, capBytes = 500, listedCount = 0, estimateFor = big)

        assertTrue(plan.toQueue.isEmpty())
        assertEquals(BatchStop.StorageCap, plan.stop)
    }

    @Test
    fun plan_CountsAFileOnceWhenAListNamesItTwice() {
        val plan = OfflineBatchLogic.plan(listOf(child("a"), child("a")), emptySet(), 0, 10_000, 0, big)

        assertEquals(1, plan.toQueue.size)
        assertEquals(1, plan.total)
    }

    // ---- Names and words ----

    @Test
    fun nameFor_NamesAnAudiobookAfterTheBookWithItsPartWhenThereAreSeveral() {
        val part = child("p2", "audiobook", "tracks", "Tracks", title = "Chapter 9")

        val several = OfflineBatchLogic.nameFor("The Book", part, partIndex = 1, partCount = 3)
        val single = OfflineBatchLogic.nameFor("The Book", part, partIndex = 0, partCount = 1)

        assertEquals(DownloadName("The Book", "Part 2 of 3"), several)
        assertEquals("The Book", single.title)
        assertNull(single.subtitle)
    }

    @Test
    fun nameFor_GivesAnEpisodeItsSeasonAndNumberAndASongItsAlbum() {
        val episode = child("e3", title = "Third", track = "3")
        val song = DetailChild("w", "t", "Song", null, null, "musicTrack", "4", "tracks", "Tracks")

        assertEquals(DownloadName("Third", "Season 1, episode 3"), OfflineBatchLogic.nameFor("A Show", episode, 0, 1))
        assertEquals(DownloadName("Song", "The Album"), OfflineBatchLogic.nameFor("The Album", song, 0, 1))
    }

    @Test
    fun message_SaysWhatStartedWhatWasHeldAndWhyItStopped() {
        val started = BatchPlan(listOf(child("a"), child("b")), alreadyHeld = 1, total = 5, stop = BatchStop.StorageCap)

        val text = OfflineBatchLogic.message(started, "episodes")

        assertTrue(text.startsWith("Started 2 episodes."))
        assertTrue(text.contains("1 already in your downloads"))
        assertTrue(text.contains("storage limit"))
    }

    @Test
    fun message_SaysEverythingIsAlreadyHeldWhenNothingIsNew() {
        val plan = BatchPlan(emptyList(), alreadyHeld = 3, total = 3, stop = BatchStop.None)

        assertEquals("Everything here is already in your downloads.", OfflineBatchLogic.message(plan, "episodes"))
    }

    @Test
    fun message_SaysNothingWasStartedWhenTheLimitCameFirst() {
        val plan = BatchPlan(emptyList(), alreadyHeld = 0, total = 3, stop = BatchStop.StorageCap)

        assertTrue(OfflineBatchLogic.message(plan, "songs").startsWith("Nothing was started."))
    }
}
