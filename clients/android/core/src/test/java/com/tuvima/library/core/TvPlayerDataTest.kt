package com.tuvima.library.core

import org.json.JSONArray
import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvPlayerDataTest {
    private fun showJson(): JSONObject = JSONObject(
        """
        {"sequencePlacement":{"groups":[
          {"key":"s1","title":"Season 1","items":[
            {"title":"The First Wish","description":"Two genies.","duration":"22 min","episodeStillUrl":"/art/a.jpg",
             "episodeContext":{"episodeAssetId":"a1","episodeWorkId":"w1","seasonNumber":1,"episodeNumber":1,"state":"Completed"}},
            {"title":"Pet Show","duration":"22 min","isOwned":false,
             "episodeContext":{"episodeAssetId":"a2","episodeWorkId":"w2","seasonNumber":1,"episodeNumber":2}},
            {"title":"Tala","duration":"21 min","positionSeconds":250,"durationSeconds":1320,
             "episodeContext":{"episodeAssetId":"a3","episodeWorkId":"w3","seasonNumber":1,"episodeNumber":3,"state":"InProgress"}}
          ]},
          {"key":"empty","title":"Season 9","items":[]},
          {"items":[
            {"title":"Hidden Treasure","episodeContext":{"episodeAssetId":"b1","episodeWorkId":"x1","seasonNumber":2,"episodeNumber":1}}
          ]}
        ]}}
        """.trimIndent(),
    )

    private val absolute: (String?) -> String? = { it?.let { path -> "http://engine.test$path" } }

    // ---- episodes ----

    @Test
    fun seasonsKeepOwnedEpisodesOnlyAndNameAnUntitledGroupFromItsFirstEpisode() {
        val seasons = TvEpisodes.seasons(showJson(), absolute)
        assertEquals(listOf("Season 1", "Season 2"), seasons.map { it.title })
        assertEquals(listOf("a1", "a3"), seasons[0].items.map { it.assetId })
        assertEquals("http://engine.test/art/a.jpg", seasons[0].items[0].stillUrl)
        assertEquals("w3", seasons[0].items[1].workId)
        assertEquals(3, seasons[0].items[1].number)
    }

    @Test
    fun anEpisodeLeftPartWayIsInProgressOnlyWithAPositionAndALength() {
        val seasons = TvEpisodes.seasons(showJson(), absolute)
        assertTrue(seasons[0].items[1].inProgress)
        assertFalse(seasons[0].items[0].inProgress)
    }

    @Test
    fun anAnswerWithNoEpisodesGivesNoSeasons() {
        assertEquals(emptyList<TvSeason>(), TvEpisodes.seasons(JSONObject("{}")))
        assertEquals(emptyList<TvSeason>(), TvEpisodes.seasons(null))
    }

    @Test
    fun previousAndNextCrossSeasonsAndAreNullAtTheEnds() {
        val seasons = TvEpisodes.seasons(showJson(), absolute)
        assertNull(TvEpisodes.neighbours(seasons, "a1").previous)
        assertEquals("a3", TvEpisodes.neighbours(seasons, "a1").next?.assetId)
        assertEquals("b1", TvEpisodes.neighbours(seasons, "A3").next?.assetId)
        assertNull(TvEpisodes.neighbours(seasons, "b1").next)
        assertEquals(TvNeighbours(null, null), TvEpisodes.neighbours(seasons, "unknown"))
        assertEquals(1, TvEpisodes.seasonIndexOf(seasons, "b1"))
        assertEquals("w1", TvEpisodes.find(seasons, "a1")?.workId)
    }

    // ---- bookmarks ----

    @Test
    fun bookmarksAreReadWithTheEnginesMineAndEveryoneFlags() {
        val list = TvBookmarks.parse(
            JSONArray(
                """[{"id":"1","positionSeconds":130.5,"label":"Genie","isYours":true,"inEveryone":false,"frameUrl":"/f/1.jpg"},
                    {"id":"2","positionSeconds":60,"ownerName":"Shy","isYours":false,"inEveryone":true}]""",
            ),
            absolute,
        )
        assertEquals(2, list.size)
        assertTrue(list[0].isYours)
        assertFalse(list[0].inEveryone)
        assertEquals("http://engine.test/f/1.jpg", list[0].frameUrl)
        assertEquals("Shy", list[1].ownerName)
        assertNull(list[1].frameUrl)
        assertEquals(emptyList<TvBookmark>(), TvBookmarks.parse(null))
    }

    @Test
    fun theBookmarkAtButtonSendsTheCurrentTimeToTheTenthAndNeverAsGlobal() {
        val body = TvBookmarks.createBody("asset-1", 462.34, 1361.0)
        assertEquals("asset-1", body.getString("assetId"))
        assertEquals(462.3, body.getDouble("positionSeconds"), 0.0)
        assertEquals(1361.0, body.getDouble("durationSeconds"), 0.0)
        assertFalse(body.getBoolean("isGlobal"))
        assertFalse(TvBookmarks.createBody("a", 1.0, null).has("durationSeconds"))
    }

    @Test
    fun bookmarkRoutesAreTheEnginesPlayerRoutes() {
        assertEquals("/api/v1/player/audiobooks/work-1/bookmarks", ClientRoutes.bookmarks("work-1"))
        assertEquals("/api/v1/player/audiobooks/bookmarks/b%2F1", ClientRoutes.bookmark("b/1"))
    }

    @Test
    fun theTabKeyRoundTripsAndDefaultsToMine() {
        assertEquals(TvBookmarkTab.Global, TvBookmarkTab.fromKey("global"))
        assertEquals(TvBookmarkTab.Mine, TvBookmarkTab.fromKey("nope"))
    }

    // ---- chapters and the timeline ----

    private val chapters = listOf(
        Chapter(0, "Opening", 0.0, 600.0),
        Chapter(1, "The road", 600.0, 1800.0),
        Chapter(2, "Home", 1800.0, null),
    )

    @Test
    fun theChapterInPlayIsTheLastOneThatHasStarted() {
        assertEquals(0, TvChapters.indexAt(chapters, 0.0))
        assertEquals(1, TvChapters.indexAt(chapters, 600.0))
        assertEquals(2, TvChapters.indexAt(chapters, 99999.0))
        assertEquals(1800.0, TvChapters.endOf(chapters, 1, 3600.0)!!, 0.0)
        assertEquals(3600.0, TvChapters.endOf(chapters, 2, 3600.0)!!, 0.0)
    }

    @Test
    fun previousChapterRestartsTheOneInPlayAfterAFewSecondsOtherwiseGoesBack() {
        assertEquals(600.0, TvChapters.previousStart(chapters, 700.0)!!, 0.0)
        assertEquals(0.0, TvChapters.previousStart(chapters, 601.0)!!, 0.0)
        assertEquals(1800.0, TvChapters.nextStart(chapters, 700.0)!!, 0.0)
        assertNull(TvChapters.nextStart(chapters, 2000.0))
        assertFalse(TvChapters.hasPrevious(chapters, 1.0))
        assertTrue(TvChapters.hasPrevious(chapters, 700.0))
        assertTrue(TvChapters.hasNext(chapters, 700.0))
    }

    @Test
    fun theTimelineShowsTheChapterOrTheWholeBook() {
        val chapter = TvChapters.timeline(chapters, 900.0, 3600.0, wholeBook = false)
        assertEquals("5:00", chapter.elapsed)
        assertEquals("-15:00", chapter.left)
        assertEquals(0.25, chapter.fraction, 1e-9)
        val book = TvChapters.timeline(chapters, 900.0, 3600.0, wholeBook = true)
        assertEquals("15:00", book.elapsed)
        assertEquals("-45:00", book.left)
        assertEquals(0.25, book.fraction, 1e-9)
        // No chapters: the whole title, whatever the choice.
        assertEquals("1:00", TvChapters.timeline(emptyList(), 60.0, 120.0, wholeBook = false).elapsed)
    }

    // ---- subtitle appearance ----

    @Test
    fun theAppearanceIsTheDevicesSavedPairElseWhatTheProfilesSizeAndBackgroundImply() {
        assertEquals(TvAppearance("Default", "None"), TvSubtitleAppearance.of(null, null))
        assertEquals(TvAppearance("Large text", "Box"), TvSubtitleAppearance.of(PlaybackPreferences(subtitleSize = "Large", subtitleBackground = true), null))
        assertEquals(
            TvAppearance("High contrast", "Shadow"),
            TvSubtitleAppearance.of(PlaybackPreferences(), TvDeviceChoices(subtitleStyle = "High contrast", subtitleBackground = "Shadow")),
        )
        // A saved value the app does not know is ignored.
        assertEquals(TvAppearance("Default", "None"), TvSubtitleAppearance.of(null, TvDeviceChoices(subtitleStyle = "Comic", subtitleBackground = "Glow")))
    }

    @Test
    fun largeTextUsesTheLargeSizeStep() {
        assertTrue(
            TvSubtitleAppearance.sizeFraction(TvAppearance("Large text", "None"), "Small") > TvSubtitleAppearance.sizeFraction(TvAppearance("Default", "None"), "Small"),
        )
    }

    // ---- the device ----

    @Test
    fun aFireTvIsAnAmazonMakerOrADeviceWithTheFireTvFeature() {
        assertTrue(TvDevice.isFireTv("Amazon", false))
        assertTrue(TvDevice.isFireTv("amazon", false))
        assertTrue(TvDevice.isFireTv("Google", true))
        assertFalse(TvDevice.isFireTv("Google", false))
        assertFalse(TvDevice.isFireTv(null, false))
        assertEquals("amazon.hardware.fire_tv", TvDevice.FIRE_TV_FEATURE)
    }

    @Test
    fun theCastButtonStaysWhereCastWorksAndIsHiddenOnAFireTv() {
        assertTrue(TvDevice.castButtonVisible(castAvailable = true, isFireTv = false))
        assertFalse(TvDevice.castButtonVisible(castAvailable = true, isFireTv = true))
        assertFalse(TvDevice.castButtonVisible(castAvailable = false, isFireTv = false))
    }

    // ---- playback information ----

    @Test
    fun playbackInformationNamesThePictureTheSoundAndTheDelivery() {
        val lines = TvPlaybackInformation.lines(null, 1080, "video/avc", "audio/ac3", 6)
        assertEquals("1080p · AVC", lines.first { it.first == "Video" }.second)
        assertEquals("AC3 · 5.1", lines.first { it.first == "Audio" }.second)
        assertTrue(TvPlaybackInformation.lines(null, 0, null, null, 0).isEmpty())
    }
}
