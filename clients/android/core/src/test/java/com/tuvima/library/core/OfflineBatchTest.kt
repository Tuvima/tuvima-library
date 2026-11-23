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

    private val showPage = """
        {"title":"A Show","mediaGroups":[
          {"key":"season-1","title":"Season 1","items":[
            {"id":"w1","entityType":5,"title":"Pilot","assetId":"a1","trackNumber":"1","isOwned":true},
            {"id":"w2","entityType":5,"title":"Second","assetId":"a2","trackNumber":"2","isOwned":true},
            {"id":"w3","entityType":5,"title":"Not owned","assetId":null,"isOwned":false}]},
          {"key":"season-2","title":"Season 2","items":[
            {"id":"w4","entityType":"TvEpisode","title":"Back","assetId":"a4","trackNumber":"1","isOwned":true}]},
          {"key":"also-in-this-world","title":"Also in this world","items":[
            {"id":"w9","entityType":1,"title":"A Film","assetId":"a9","isOwned":true}]}
        ]}
    """.trimIndent()

    @Test
    fun parse_ListsOwnedEpisodesPerSeasonAndSkipsUnownedAndTheAlsoGroup() {
        val groups = DetailChildren.parse(JSONObject(showPage), none)

        assertEquals(listOf("season-1", "season-2"), groups.map { it.key })
        assertEquals(listOf("a1", "a2"), groups[0].children.map { it.assetId })
        assertEquals(1, groups[0].skipped)
        assertEquals("tvEpisode", groups[1].children.single().entityType)
        assertEquals("w4", groups[1].children.single().workId)
    }

    @Test
    fun parse_ReadsAnAlbumsSongsFromPlainWorksAndKeepsTheirNumbers() {
        val json = JSONObject(
            """{"mediaGroups":[{"key":"tracks","title":"Tracks","items":[
                {"id":"s1","entityType":0,"title":"One","assetId":"t1","trackNumber":"1","subtitle":"Artist","isOwned":true},
                {"id":"s2","entityType":0,"title":"Two","assetId":"t2","trackNumber":"2","isOwned":true}]}]}""",
        )

        val children = DetailChildren.parse(json, none).single().children

        assertEquals(listOf("musicTrack", "musicTrack"), children.map { it.entityType })
        assertEquals(listOf("1", "2"), children.map { it.trackNumber })
        assertEquals("Artist", children[0].subtitle)
    }

    @Test
    fun parse_ListsAnAudiobookFileOnceHoweverManyChaptersNameIt() {
        val json = JSONObject(
            """{"mediaGroups":[{"key":"tracks","title":"Tracks","items":[
                {"id":"b1","entityType":8,"title":"Chapter 1","assetId":"file-1","isOwned":true},
                {"id":"b1","entityType":8,"title":"Chapter 2","assetId":"file-1","isOwned":true},
                {"id":"b1","entityType":8,"title":"Chapter 3","assetId":"file-2","isOwned":true}]}]}""",
        )

        val children = DetailChildren.parse(json, none).single().children

        assertEquals(listOf("file-1", "file-2"), children.map { it.assetId })
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

        assertEquals("A Show", model.title)
        assertEquals(3, OfflineBatchLogic.allChildren(model.children).size)
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
        assertEquals("", DetailChildren.wireName(99))
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
