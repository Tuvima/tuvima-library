package com.tuvima.library.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class TvPlayerPanelsTest {
    private fun episode(id: String, number: Int, title: String, state: String = "", position: Double? = null, duration: Double? = null) =
        TvEpisode(id, "w$id", 1, number, title, "A synopsis.", "22 min", position, duration, state, "http://x/$id.jpg")

    private val season = TvSeason("s1", "Season 1", listOf(episode("a", 1, "The First Wish"), episode("b", 2, "Pet Show"), episode("c", 3, "Tala")))

    private fun rows(panel: TvPanel) = (panel.body + panel.footer).filterIsInstance<TvRow>()

    // ---- Episodes ----

    @Test
    fun theCurrentEpisodeReadsPlayingWithTimeAndTheNextOneCarriesUpNext() {
        val now = TvPlayingNow("a", playing = true, positionSeconds = 462.0, durationSeconds = 1361.0, nextAssetId = "b")
        val panel = TvPanels.episodes(listOf(season), 0, lessons = false, now = now, autoplay = true)
        val list = rows(panel)
        assertEquals("Season 1", list.first { it.key == "season" }.title)
        assertEquals("3 episodes", list.first { it.key == "season" }.trailing)
        val current = list.first { it.key == "ep:a" }
        assertEquals("1 · The First Wish", current.title)
        assertEquals("Playing · 7:42 of 22:41", current.sub)
        assertTrue(current.current)
        assertEquals("Up next", list.first { it.key == "ep:b" }.chip)
        assertNull(list.first { it.key == "ep:c" }.chip)
        assertEquals("ep:a", panel.focusKey)
        assertEquals("ep:a", panel.focusable()[panel.initialIndex()].focusKey())
    }

    @Test
    fun aPausedCurrentEpisodeAndOneLeftPartWayBothReadPaused() {
        val partly = episode("c", 3, "Tala", state = "InProgress", position = 250.0, duration = 1320.0)
        val withProgress = TvSeason("s1", "Season 1", season.items.take(2) + partly)
        val now = TvPlayingNow("a", playing = false, positionSeconds = 60.0, durationSeconds = 1361.0)
        val list = rows(TvPanels.episodes(listOf(withProgress), 0, false, now, true))
        assertEquals("Paused · 1:00 of 22:41", list.first { it.key == "ep:a" }.sub)
        assertEquals("Paused · 4:10 of 22:00", list.first { it.key == "ep:c" }.sub)
        assertEquals("22 min · A synopsis.", list.first { it.key == "ep:b" }.sub)
    }

    @Test
    fun theAutoplaySwitchIsTheFooterAndLessonsHaveACountInsteadOfASeasonDropdown() {
        val now = TvPlayingNow("a", true, 0.0, 100.0)
        val episodes = TvPanels.episodes(listOf(season), 0, false, now, autoplay = false)
        val footer = episodes.footer.single() as TvSwitch
        assertEquals("Autoplay next episode", footer.label)
        assertFalse(footer.on)
        val lessons = TvPanels.episodes(listOf(season), 0, true, now, true)
        assertEquals("Lessons", lessons.title)
        assertEquals("3 lessons", (lessons.body.first() as TvNote).text)
        assertEquals("1 lesson", TvPanels.countLabel(1, true))
    }

    @Test
    fun theSeasonRowChangesWithLeftAndRightOnlyWhenThereIsMoreThanOne() {
        val two = listOf(season, season.copy(key = "s2", title = "Season 2"))
        val now = TvPlayingNow("zzz", true, 0.0, 100.0)
        assertTrue(rows(TvPanels.episodes(two, 0, false, now, true)).first { it.key == "season" }.adjustable)
        assertFalse(rows(TvPanels.episodes(listOf(season), 0, false, now, true)).first { it.key == "season" }.adjustable)
    }

    // ---- Audio & subtitles ----

    @Test
    fun audioRowsListCommentaryAndAudioDescriptionLastAndOnlyWhenThereAreSeveralTracks() {
        val audio = listOf(
            TvAudioTrack("0:0", "English", "audio/eac3-joc", 8, selected = true),
            TvAudioTrack("0:1", "English", "audio/ac3", 2, description = true),
            TvAudioTrack("0:2", "Spanish", "audio/ac3", 6),
        )
        val panel = TvPanels.audioSubtitles(audio, emptyList(), canFind = false)
        val audioRows = rows(panel).filter { it.key.startsWith("audio:") }
        assertEquals(listOf("English", "Spanish", "English · Audio description"), audioRows.map { it.title })
        assertEquals("Dolby Atmos · 7.1", audioRows.first().sub)
        assertTrue(audioRows.first().checked)
        val single = TvPanels.audioSubtitles(audio.take(1), emptyList(), false)
        assertTrue(single.body.none { it is TvLabel && it.text == "Audio" })
    }

    @Test
    fun subtitleRowsStartWithOffAndNameSdhAndForcedTracks() {
        val subs = listOf(
            TvSubtitleTrack("1:0", "English"),
            TvSubtitleTrack("1:1", "English", hearingImpaired = true),
            TvSubtitleTrack("1:2", "Spanish", forced = true),
        )
        val panel = TvPanels.audioSubtitles(emptyList(), subs, false)
        val list = rows(panel).filter { it.key.startsWith("sub:") }
        assertEquals(listOf("Off", "English", "English · SDH", "Spanish · Forced"), list.map { it.title })
        assertTrue(list.first().checked)
        assertEquals("sub:off", panel.focusKey)
        val selected = TvPanels.audioSubtitles(emptyList(), subs.mapIndexed { i, s -> s.copy(selected = i == 1) }, false)
        assertFalse(rows(selected).first { it.key == "sub:off" }.checked)
        assertEquals("sub:1:1", selected.focusKey)
    }

    @Test
    fun theFooterOffersSubtitleAppearanceAndFindSubtitlesOnlyWhenASourceIsReady() {
        assertEquals(listOf("Subtitle appearance"), TvPanels.audioSubtitles(emptyList(), emptyList(), false).footer.filterIsInstance<TvRow>().map { it.title })
        assertEquals(
            listOf("Subtitle appearance", "Find subtitles…"),
            TvPanels.audioSubtitles(emptyList(), emptyList(), true).footer.filterIsInstance<TvRow>().map { it.title },
        )
    }

    @Test
    fun subtitleAppearanceTicksTheStyleAndBackgroundAndSavesAutomatically() {
        val panel = TvPanels.subtitleAppearance("Large text", "Shadow")
        assertTrue(panel.back)
        assertEquals(setOf("style:Large text", "background:Shadow"), rows(panel).filter { it.checked }.map { it.key }.toSet())
        assertEquals("Changes saved automatically", panel.body.filterIsInstance<TvNote>().single().text)
        assertEquals("Reset to default", panel.footer.filterIsInstance<TvRow>().single().title)
    }

    // ---- Bookmarks ----

    private fun bookmark(id: String, at: Double, mine: Boolean, everyone: Boolean, label: String = "", owner: String = "") =
        TvBookmark(id, at, label, "", "http://x/$id.jpg", owner, mine, everyone)

    private val mixed = listOf(
        bookmark("m2", 331.0, mine = true, everyone = false, label = "Funny wish"),
        bookmark("m1", 130.0, mine = true, everyone = false),
        bookmark("g1", 60.0, mine = false, everyone = true, label = "Opening", owner = "Shy"),
    )

    @Test
    fun bookmarksOpenOnMineWithTheBookmarkAtButtonAndTheChoiceShowsOnlyWhenAGlobalSetExists() {
        val panel = TvPanels.bookmarks(mixed, TvBookmarkTab.Mine, 462.0, TvBookmarkView(loaded = true), ticksOn = true)
        val segment = panel.body.filterIsInstance<TvSegment>().single()
        assertEquals(listOf("Mine · 2", "Global · 1"), segment.options.map { it.label })
        assertEquals("mine", segment.selectedKey)
        assertEquals("Bookmark at 7:42", rows(panel).first { it.key == "add" }.title)
        assertEquals("add", panel.focusKey)
        assertEquals(listOf("2:10", "5:31"), rows(panel).filter { it.key.startsWith("bm:") }.map { it.title })
        val noGlobal = TvPanels.bookmarks(mixed.filter { it.isYours }, TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true), true)
        assertTrue(noGlobal.body.none { it is TvSegment })
    }

    @Test
    fun theGlobalTabListsTheEnginesEveryoneSetWithTheMakersNameAndNoDeleteButton() {
        val panel = TvPanels.bookmarks(mixed, TvBookmarkTab.Global, 0.0, TvBookmarkView(loaded = true), true)
        val row = rows(panel).first { it.key == "bm:g1" }
        assertEquals("Opening · Shy", row.sub)
        assertTrue(row.actions.isEmpty())
        assertEquals("http://x/g1.jpg", row.imageUrl)
    }

    @Test
    fun aGlobalTabIsIgnoredWhenThereIsNoGlobalSet() {
        val panel = TvPanels.bookmarks(mixed.filter { it.isYours }, TvBookmarkTab.Global, 0.0, TvBookmarkView(loaded = true), true)
        assertEquals(listOf("bm:m1", "bm:m2"), rows(panel).filter { it.key.startsWith("bm:") }.map { it.key })
    }

    @Test
    fun deleteAsksOnceWithDeleteAndKeepOnTheOwnRowOnly() {
        val asking = TvPanels.bookmarks(mixed, TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true, confirmDeleteId = "m1"), true)
        assertEquals(listOf("Delete", "Keep"), rows(asking).first { it.key == "bm:m1" }.actions.map { it.label })
        assertEquals(listOf("Delete"), rows(asking).first { it.key == "bm:m2" }.actions.map { it.label })
    }

    @Test
    fun emptyStatesUseTheContractSentencesOnceTheListHasLoaded() {
        fun notes(panel: TvPanel) = panel.body.filterIsInstance<TvNote>().map { it.text }
        assertEquals(listOf("No bookmarks of yours yet"), notes(TvPanels.bookmarks(emptyList(), TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true), true)))
        assertEquals(emptyList<String>(), notes(TvPanels.bookmarks(emptyList(), TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = false), true)))
        val onlyGlobal = listOf(bookmark("g1", 1.0, mine = false, everyone = true))
        assertEquals(listOf("Select a bookmark to jump to that moment."), notes(TvPanels.bookmarks(onlyGlobal, TvBookmarkTab.Global, 0.0, TvBookmarkView(loaded = true), true)))
    }

    @Test
    fun aFailedSaveShowsTheContractSentenceAndShowOnTimelineIsTheFooterSwitch() {
        val panel = TvPanels.bookmarks(mixed, TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true, error = true), ticksOn = false)
        assertTrue(panel.body.any { it is TvNote && it.text == "Couldn’t save · Try again" })
        val footer = panel.footer.single() as TvSwitch
        assertEquals("Show on timeline", footer.label)
        assertFalse(footer.on)
    }

    @Test
    fun theTimelineTicksSitWhereTheBookmarksAreAndNoneWithoutALength() {
        assertEquals(listOf(25.0, 50.0), TvPanels.bookmarkTicks(listOf(mixed[2].copy(positionSeconds = 25.0), mixed[0].copy(positionSeconds = 50.0)), 100.0))
        assertEquals(emptyList<Double>(), TvPanels.bookmarkTicks(mixed, 0.0))
        assertEquals(emptyList<Double>(), TvPanels.bookmarkTicks(listOf(mixed[0].copy(positionSeconds = 500.0)), 100.0))
    }

    // ---- Playback settings ----

    @Test
    fun playbackSettingsListSpeedSkipsAndInformationWithAResetFooter() {
        val panel = TvPanels.playbackSettings(1.25, TvSkip(15, 60))
        assertEquals(listOf("Playback speed", "Skip back", "Skip forward", "Playback information"), rows(panel).take(4).map { it.title })
        assertEquals("1.25× ›", rows(panel)[0].trailing)
        assertEquals("15 s ›", rows(panel)[1].trailing)
        assertEquals("60 s ›", rows(panel)[2].trailing)
        assertEquals("Reset playback preferences", panel.footer.filterIsInstance<TvRow>().single().title)
    }

    @Test
    fun aSpeedPresetIsTickedOnlyWhenTheSpeedEqualsItAndTheFineRowStepsWithLeftAndRight() {
        val on = rows(TvPanels.speed(1.5)).filter { it.checked }
        assertEquals(listOf("preset:1.5"), on.map { it.key })
        assertTrue(rows(TvPanels.speed(1.05)).none { it.checked })
        assertTrue(rows(TvPanels.speed(1.0)).first { it.key == "speed-fine" }.adjustable)
        assertEquals("Reset to 1×", TvPanels.speed(1.0).footer.filterIsInstance<TvRow>().single().title)
    }

    @Test
    fun theSkipChoicesAreTheContractVideoChoicesWithTheCurrentOneTicked() {
        assertEquals(listOf("5 s", "10 s", "15 s", "30 s"), rows(TvPanels.skipChoices(false, 10)).map { it.title })
        assertEquals(listOf("10 s", "30 s", "60 s", "90 s"), rows(TvPanels.skipChoices(true, 30)).map { it.title })
        assertEquals(listOf("seconds:30"), rows(TvPanels.skipChoices(true, 30)).filter { it.checked }.map { it.key })
    }

    // ---- Chapters ----

    private val chapters = listOf(
        Chapter(0, "Opening", 0.0, 600.0),
        Chapter(1, "The road", 600.0, 1800.0),
        Chapter(2, "Home", 1800.0, null),
    )

    @Test
    fun theChaptersPanelLeadsWithTheIdentityAndMarksTheCurrentAndFinishedChapters() {
        val panel = TvPanels.chapters(chapters, 1000.0, 3600.0, wholeBook = false)
        assertEquals("Chapter 2 of 3", (panel.body.first() as TvLabel).text)
        assertEquals("Book · 28% complete", (panel.body[1] as TvNote).text)
        val list = rows(panel)
        assertTrue(list[0].checked)
        assertEquals("Finished", list[0].sub)
        assertEquals("6:40 elapsed · 13:20 remaining", list[1].sub)
        assertTrue(list[1].current)
        assertFalse(list[2].checked)
        assertEquals("ch:1", panel.focusKey)
        assertTrue(panel.body.any { it is TvNote && it.text == "Select a chapter to start listening." })
    }

    @Test
    fun theTimelineChoiceIsChapterOrWholeBookInTheFooter() {
        val footer = TvPanels.chapters(chapters, 0.0, 3600.0, wholeBook = true).footer
        assertEquals("Timeline shows", (footer[0] as TvLabel).text)
        val segment = footer[1] as TvSegment
        assertEquals(listOf("Chapter", "Whole book"), segment.options.map { it.label })
        assertEquals("book", segment.selectedKey)
    }

    // ---- the key reducer ----

    private fun press(panel: TvPanel, focus: TvPanelFocus, key: TvKeyAction) = TvPanelKeys.reduce(panel, focus, key)

    @Test
    fun upAndDownMoveOverTheFocusableRowsStopAtTheEndAndUpFromTheFirstReachesTheHeader() {
        val panel = TvPanels.playbackSettings(1.0, TvSkip(10, 30))
        var focus = TvPanelFocus(0)
        focus = press(panel, focus, TvKeyAction.Up).first
        assertEquals(-1, focus.index)
        assertEquals(-1, press(panel, focus, TvKeyAction.Up).first.index)
        focus = press(panel, focus, TvKeyAction.Down).first
        assertEquals(0, focus.index)
        repeat(10) { focus = press(panel, focus, TvKeyAction.Down).first }
        assertEquals(panel.focusable().size - 1, focus.index)
    }

    @Test
    fun theHeaderHoldsCloseAndInANestedViewABackArrowBeforeIt() {
        val top = TvPanels.playbackSettings(1.0, TvSkip(10, 30))
        assertEquals(TvPanelEffect.Activate(TvPanelKeys.HEADER_CLOSE), press(top, TvPanelFocus(-1), TvKeyAction.Select).second)
        assertEquals(TvPanelEffect.Leave, press(top, TvPanelFocus(-1), TvKeyAction.Left).second)
        val nested = TvPanels.speed(1.0)
        assertEquals(TvPanelEffect.Activate(TvPanelKeys.HEADER_BACK), press(nested, TvPanelFocus(-1, 0), TvKeyAction.Select).second)
        val onClose = press(nested, TvPanelFocus(-1, 0), TvKeyAction.Right).first
        assertEquals(1, onClose.sub)
        assertEquals(TvPanelEffect.Activate(TvPanelKeys.HEADER_CLOSE), press(nested, onClose, TvKeyAction.Select).second)
        assertEquals(0, press(nested, onClose, TvKeyAction.Left).first.sub)
    }

    @Test
    fun selectOnARowActivatesItAndOnADeleteButtonActivatesThatButton() {
        val panel = TvPanels.playbackSettings(1.0, TvSkip(10, 30))
        assertEquals(TvPanelEffect.Activate("speed", null), press(panel, TvPanelFocus(0), TvKeyAction.Select).second)
        val bookmarks = TvPanels.bookmarks(mixed, TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true), true)
        val index = bookmarks.focusable().indexOfFirst { it.focusKey() == "bm:m1" }
        val onRow = press(bookmarks, TvPanelFocus(index), TvKeyAction.Right)
        assertEquals(1, onRow.first.sub)
        assertEquals(TvPanelEffect.Activate("bm:m1", "delete"), press(bookmarks, onRow.first, TvKeyAction.Select).second)
        assertEquals(0, press(bookmarks, onRow.first, TvKeyAction.Left).first.sub)
    }

    @Test
    fun leftFromTheEdgeOfAPanelAsksToReturnToTheToolRow() {
        val panel = TvPanels.playbackSettings(1.0, TvSkip(10, 30))
        assertEquals(TvPanelEffect.Leave, press(panel, TvPanelFocus(0), TvKeyAction.Left).second)
        assertEquals(TvPanelEffect.None, press(panel, TvPanelFocus(0), TvKeyAction.Right).second)
    }

    @Test
    fun leftAndRightOnASegmentChooseTheOtherOptionAndOnTheFineSpeedStepIt() {
        val bookmarks = TvPanels.bookmarks(mixed, TvBookmarkTab.Mine, 0.0, TvBookmarkView(loaded = true), true)
        assertEquals(TvPanelEffect.Choose("tab", "global"), press(bookmarks, TvPanelFocus(0), TvKeyAction.Right).second)
        assertEquals(TvPanelEffect.Choose("tab", "global"), press(bookmarks, TvPanelFocus(0), TvKeyAction.Select).second)
        val onGlobal = TvPanels.bookmarks(mixed, TvBookmarkTab.Global, 0.0, TvBookmarkView(loaded = true), true)
        assertEquals(TvPanelEffect.Choose("tab", "mine"), press(onGlobal, TvPanelFocus(0), TvKeyAction.Left).second)
        val speed = TvPanels.speed(1.0)
        val fine = speed.initialIndex()
        assertEquals(TvPanelEffect.Adjust("speed-fine", 1), press(speed, TvPanelFocus(fine), TvKeyAction.Right).second)
        assertEquals(TvPanelEffect.Adjust("speed-fine", -1), press(speed, TvPanelFocus(fine), TvKeyAction.Left).second)
    }

    @Test
    fun aPanelWithNothingToSelectStartsOnItsHeaderAndLeftStillReturns() {
        val info = TvPanels.playbackInformation(listOf("Video" to "1080p H264"))
        assertEquals(-1, info.initialIndex())
        assertEquals(TvPanelEffect.Leave, press(info, TvPanelFocus(-1), TvKeyAction.Left).second)
        assertEquals(TvPanelEffect.Activate(TvPanelKeys.HEADER_BACK), press(info, TvPanelFocus(-1), TvKeyAction.Select).second)
    }
}
